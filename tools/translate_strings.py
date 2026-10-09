"""Translate the interface into the other languages, English as the source.

    python tools/translate_strings.py                 # what each language is missing
    python tools/translate_strings.py --only de-DE,ja-JP
    python tools/translate_strings.py --review        # a second model reads every language
    python tools/translate_strings.py --check         # only the mechanical checks, no requests

Free models through NVIDIA NIM. The key is read from NVIDIA_NIM_API_KEY, in the
environment or in ~/.fcc/.env, and never written anywhere.

What Master Audio Switcher taught, kept here:
- only the keys a language is missing are sent, thirty at a time, so no answer
  outgrows the model's limit and a re-run after one new string costs seconds;
- nothing is written until every value in the answer has passed the checks
  below; a value that fails is asked for again with the reason, and one that
  still fails stays English and is named, not passed off as a translation;
- the checks are the ones that caught real faults: a lost placeholder, a
  product name translated, Cyrillic in a Latin language, a label too long for
  the strip of the bar it sits on.
"""
import argparse
import html
import json
import os
import re
import sys
import threading
import time
import urllib.error
import urllib.request
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STRINGS = ROOT / "src" / "MCD.App" / "Strings"
SOURCE = "en-US"
API = "https://integrate.api.nvidia.com/v1/chat/completions"
KIMI = "moonshotai/kimi-k3"
GEMMA = "google/gemma-4-31b-it"
NEMOTRON = "nvidia/nemotron-3-super-120b-a12b"
# Kimi writes the best text but the free tier lets few requests through at a
# time; most languages go to Gemma, which has its own allowance and wrote the same text on trial.
# Each language is then read by the model that did not write it.
BY_GEMMA = {"zh-Hans", "ja-JP", "ko-KR", "tr-TR", "es-ES", "pt-BR", "it-IT", "nl-NL", "pl-PL"}


def translators(code: str) -> list:
    return [GEMMA if code in BY_GEMMA else KIMI, NEMOTRON]


def reviewers(code: str) -> list:
    return [KIMI if code in BY_GEMMA else GEMMA, NEMOTRON]

CHUNK = 30
REVIEW_CHUNK = 60

# Folder name: (the language's own name, what the model is told).
LANGUAGES = {
    "uk-UA": ("Українська", "Ukrainian"),
    "de-DE": ("Deutsch", "German"),
    "es-ES": ("Español", "Spanish (Spain)"),
    "fr-FR": ("Français", "French (France)"),
    "it-IT": ("Italiano", "Italian"),
    "pt-BR": ("Português (Brasil)", "Brazilian Portuguese"),
    "pl-PL": ("Polski", "Polish"),
    "cs-CZ": ("Čeština", "Czech"),
    "nl-NL": ("Nederlands", "Dutch"),
    "tr-TR": ("Türkçe", "Turkish"),
    "zh-Hans": ("简体中文", "Simplified Chinese"),
    "ja-JP": ("日本語", "Japanese"),
    "ko-KR": ("한국어", "Korean"),
}
CYRILLIC = {"uk-UA", "ru-RU"}
CJK = {"zh-Hans", "ja-JP", "ko-KR"}

KEEP = ["Master Control Dock", "Master Audio Switcher", "Windows", "GitHub",
        "PawnIO", "PowerToys", "Microsoft", "GPL-3.0-or-later", "MIT", "ACPI",
        "Program Files", "Ctrl+Z", "Ctrl", "Alt", "Win", "Esc", "Enter",
        "Caps Lock", "Wi-Fi", "CPU", "GPU", "RAM", "°C"]

# A caption under a figure on the bar: a few letters wide.
BAR = re.compile(r"^(Label\w+|Group(Storage|Board))$")
# Segments, tabs, navigation, buttons and the gallery's captions: little room.
TIGHT = re.compile(r"^(Short\w+|Seg\w+|Nav\w+\.Text|Part\w+|Mode(Pinned|Hide|Desktop)|BarSize(Compact|Normal|Large)"
                   r"|Edge(Left|Top|Right|Bottom)|SensorKind\w+|\w+Button(One)?|\w+Button\.Content|Lamp\w+)$")

PROMPT = """Translate the VALUES of the JSON object below from English into {lang}.

The program is Master Control Dock, a small bar along the edge of the screen in Windows 11. The bar
holds widgets: CPU, GPU and memory load, temperatures, network speed, volume, microphone mute,
media buttons, battery, Wi-Fi, keyboard language, clock, pinned apps. A settings window has the pages
Bars, Widgets, Appearance, General, Sensors and About. The KEY of each entry hints at where the text
appears: *Hint is a one-line explanation under a setting, *Tip is a tooltip on the bar, Undo* is the
name of an action after "Undo -", Seg*/Mode*/Edge*/BarSize* are segments of a segmented control,
Nav* are the page names in the side menu, Sense* describe a sensor reading, Label* is a tiny caption
under a number on the bar.

Rules:
- Return a JSON object with exactly the same keys. Translate every value.
- Keep placeholders such as {{0}}, {{1}}, {{0:0}}, {{0:0.#}}, {{0:t}} exactly, each once.
- Do not translate: {keep}.
- Use the words Windows 11 itself uses in {lang} for system terms (taskbar, notification area,
  sign in, settings, shortcut, sleep, drive, screen, app, mute) and address the person the way
  Windows 11 does in {lang}.
- Plain technical language, short, no marketing, no exclamation marks, no metaphors.
- Labels, buttons, segments and menu names: as short as the English, a word or two.
- Label* values: at most 10 characters; use the abbreviation Task Manager uses in {lang} if the
  word is longer.
- Where a number placeholder counts something and the noun would need a plural form that depends on
  the number, write "Noun: {{0}}" instead, so it reads correctly for every number.
- Undo* values are short noun phrases naming the action.
{extra}
Return ONLY the JSON object.

{payload}
"""

REVIEW = """You are reviewing the {lang} translation of a Windows 11 utility, Master Control Dock (a bar
along the screen edge with widgets for CPU/GPU load, temperatures, sound, media, clock; a settings
window with pages Bars, Widgets, Appearance, General, Sensors, About).

Each entry is KEY: [English source, current {lang} translation]. Read every translation as a native
{lang} speaker who uses Windows 11 in {lang} every day. Report ONLY entries that are wrong:
mistranslation or changed meaning, grammar or spelling errors, words Windows 11 does not use for that
system term in {lang}, inconsistent terms between entries, an unnatural word-for-word rendering, a
label or button much longer than needed, a mix of formal and informal address.

Keep placeholders like {{0}} and {{0:0}} and these names untouched: {keep}.
Do not report entries that are merely different from what you would write; only real faults.

Return a JSON object: {{"KEY": "corrected translation", ...}} with only the entries you correct.
If nothing is wrong, return {{}}. Return ONLY the JSON object.

{payload}
"""

_lock = threading.Lock()

# The free tier answers 429 to a dozen requests at once to one model. A few at a
# time per model, and a refusal is waited out rather than counted as a failure.
_gates = defaultdict(lambda: threading.Semaphore(5))


def say(*parts):
    with _lock:
        print(*parts, flush=True)


def key_for_nim() -> str:
    if os.environ.get("NVIDIA_NIM_API_KEY"):
        return os.environ["NVIDIA_NIM_API_KEY"]
    env = Path.home() / ".fcc" / ".env"
    if env.is_file():
        for line in env.read_text(encoding="utf-8").splitlines():
            name, _, value = line.partition("=")
            if name.strip() == "NVIDIA_NIM_API_KEY" and value.strip():
                return value.strip().strip('"').strip("'")
    raise SystemExit("NVIDIA_NIM_API_KEY is not set (environment or ~/.fcc/.env).")


def read_resw(path: Path) -> dict:
    text = path.read_text(encoding="utf-8-sig")
    return {k: html.unescape(v) for k, v in
            re.findall(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', text, re.S)}


def write_resw(code: str, values: dict) -> None:
    """The English file with its values swapped, through a temporary file."""
    template = (STRINGS / SOURCE / "Resources.resw").read_text(encoding="utf-8-sig")

    def swap(m):
        return m.group(1) + html.escape(values[m.group(2)], quote=False) + m.group(4)

    text = re.sub(r'(<data name="([^"]+)"[^>]*>\s*<value>)(.*?)(</value>)', swap, template, flags=re.S)
    folder = STRINGS / code
    folder.mkdir(exist_ok=True)
    tmp = folder / ".Resources.resw.new"
    tmp.write_text("﻿" + text, encoding="utf-8", newline="\r\n" if "\r\n" in template else "\n")
    assert read_resw(tmp).keys() == values.keys()
    tmp.replace(folder / "Resources.resw")


def ask(key: str, model: str, prompt: str) -> dict:
    for wait in (30, 60, 90, 120, 180, 240):
        try:
            with _gates[model]:
                return _ask(key, model, prompt)
        except urllib.error.HTTPError as e:
            if e.code != 429:
                raise
            time.sleep(wait)
    raise RuntimeError(f"{model} kept answering 429")


def _ask(key: str, model: str, prompt: str) -> dict:
    body = json.dumps({
        "model": model,
        "messages": [{"role": "system", "content": "You are a professional software localizer. Output only valid JSON."},
                     {"role": "user", "content": prompt}],
        "temperature": 0.2,
        "max_tokens": 16000,
        "stream": True,
    }).encode("utf-8")
    req = urllib.request.Request(API, data=body, headers={
        "Authorization": f"Bearer {key}", "Content-Type": "application/json", "Accept": "text/event-stream"})
    text = []
    with urllib.request.urlopen(req, timeout=1800) as res:
        for raw in res:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:") or line == "data: [DONE]":
                continue
            choices = json.loads(line[5:]).get("choices") or [{}]   # the last chunk may carry only usage
            text.append(choices[0].get("delta", {}).get("content") or "")
    answer = re.sub(r"<think>.*?</think>", "", "".join(text), flags=re.S).strip()
    start, end = answer.find("{"), answer.rfind("}")
    if start < 0 or end < start:
        raise ValueError(f"no JSON in the answer ({len(answer)} chars)")
    return json.loads(answer[start:end + 1])


def placeholders(s: str) -> list:
    return sorted(re.findall(r"\{\d+(?::[^}]*)?\}", s))


def fault(code: str, key: str, english: str, value) -> str | None:
    """Why this value cannot go in, or None."""
    if not isinstance(value, str) or not value.strip():
        return "empty"
    if placeholders(value) != placeholders(english):
        return f"placeholders must be exactly {placeholders(english)}"
    for name in KEEP:
        if name in english and name not in value and name not in ("CPU", "GPU", "RAM", "Enter", "Win", "Esc"):
            return f'"{name}" must stay as it is'
    if code not in CYRILLIC and re.search("[Ѐ-ӿ]", value):
        return "Cyrillic letters in a non-Cyrillic language"
    if code in CYRILLIC and code == "uk-UA" and re.search("[ыэъё]", value.lower()):
        return "Russian letters (ы, э, ъ, ё) in Ukrainian"
    width = sum(2 if ord(c) > 0x2E80 else 1 for c in value)
    if BAR.match(key) and width > max(10, len(english)):
        return f"too long for the bar: at most {max(10, len(english))} characters"
    # The side menu has room for a long word; a segment or button does not.
    room = 24 if key.startswith(("Nav", "Lamp")) else max(14, 2 * len(english))
    if TIGHT.match(key) and width > room:
        return f"too long for a button or segment: at most {room} characters"
    return None


def extra_for(code: str) -> str:
    if code == "uk-UA":
        return ("- The Russian version is given as \"ru\" beside the English for reference; translate from the "
                "English meaning, in natural modern Ukrainian (not a calque of Russian).\n")
    return ""


def translate_language(nim: str, code: str, source: dict, russian: dict) -> list:
    lang = LANGUAGES[code][1]
    path = STRINGS / code / "Resources.resw"
    have = read_resw(path) if path.is_file() else {}
    # Kept beside this script, not beside the strings: the build would ship it.
    marker = ROOT / "tools" / "untranslated" / f"{code}.json"
    untranslated = set(json.loads(marker.read_text("utf-8"))) if marker.is_file() else set()
    missing = [k for k in source if k not in have or k in untranslated]
    say(f"{code}: {len(missing)} to translate")
    done = {k: v for k, v in have.items() if k in source and k not in untranslated}

    for start in range(0, len(missing), CHUNK):
        part = missing[start:start + CHUNK]
        notes = {}
        for attempt in range(4):
            payload = {k: ({"en": source[k], "ru": russian[k]} if code == "uk-UA" else source[k]) for k in part}
            if notes:
                payload = {k: payload[k] for k in notes}
                fix = "\nThese were rejected before; fix exactly this:\n" + "\n".join(f"- {k}: {why}" for k, why in notes.items())
            else:
                fix = ""
            model = translators(code)[0 if attempt < 2 else 1]
            prompt = PROMPT.format(lang=lang, keep=", ".join(KEEP), extra=extra_for(code) + fix,
                                   payload=json.dumps(payload, ensure_ascii=False, indent=1))
            try:
                got = ask(nim, model, prompt)
            except Exception as e:
                say(f"  {code} {start + 1}: {model} failed ({e})")
                time.sleep(15)
                continue
            notes = {}
            for k in (list(payload)):
                why = fault(code, k, source[k], got.get(k))
                if why:
                    notes[k] = why
                else:
                    done[k] = got[k].strip()
            if not notes:
                break
        if notes:
            say(f"  {code} {start + 1}: still rejected {notes}")
        say(f"  {code}: {min(start + CHUNK, len(missing))}/{len(missing)}")
        save(code, source, done, marker)

    left = save(code, source, done, marker)
    say(f"{code}: written, {len(left)} left in English")
    return left


def save(code: str, source: dict, done: dict, marker: Path) -> list:
    """After every chunk, so a stopped run loses one chunk and not a language."""
    left = [k for k in source if k not in done]
    write_resw(code, {k: done.get(k, source[k]) for k in source})
    if left:
        marker.parent.mkdir(exist_ok=True)
        marker.write_text(json.dumps(left, indent=1) + "\n", encoding="utf-8")
    elif marker.is_file():
        marker.unlink()
    return left


def review_language(nim: str, code: str, source: dict, log: Path) -> int:
    lang = LANGUAGES[code][1]
    path = STRINGS / code / "Resources.resw"
    have = read_resw(path)
    keys = list(source)
    changed = 0
    lines = []
    for start in range(0, len(keys), REVIEW_CHUNK):
        part = keys[start:start + REVIEW_CHUNK]
        payload = {k: [source[k], have[k]] for k in part}
        prompt = REVIEW.format(lang=lang, keep=", ".join(KEEP), payload=json.dumps(payload, ensure_ascii=False, indent=1))
        got = None
        for model in reviewers(code) + reviewers(code)[:1]:
            try:
                got = ask(nim, model, prompt)
                break
            except Exception as e:
                say(f"  {code} review {start + 1}: {model} failed ({e})")
                time.sleep(20)
        if got is None:
            say(f"  {code} review {start + 1}: not reviewed")
            continue
        for k, v in got.items():
            if k not in payload or not isinstance(v, str) or v.strip() == have[k]:
                continue
            why = fault(code, k, source[k], v)
            if why:
                lines.append(f"REJECTED {k}: {have[k]!r} -> {v!r} ({why})")
                continue
            lines.append(f"{k}: {have[k]!r} -> {v.strip()!r}")
            have[k] = v.strip()
            changed += 1
        say(f"  {code} review: {min(start + REVIEW_CHUNK, len(keys))}/{len(keys)}, {changed} corrected")
    write_resw(code, have)
    with _lock:
        with log.open("a", encoding="utf-8") as f:
            f.write(f"## {code}: {changed} corrected\n" + "\n".join(lines) + "\n\n")
    return changed


def check(codes) -> int:
    source = read_resw(STRINGS / SOURCE / "Resources.resw")
    bad = 0
    for code in codes:
        path = STRINGS / code / "Resources.resw"
        if not path.is_file():
            print(f"{code}: no file")
            bad += 1
            continue
        have = read_resw(path)
        if have.keys() != source.keys():
            print(f"{code}: keys differ from English")
            bad += 1
        for k in source:
            why = fault(code, k, source[k], have.get(k))
            if why:
                print(f"{code} {k}: {why}: {have.get(k)!r}")
                bad += 1
        same = [k for k in source if have.get(k) == source[k] and re.search("[a-z]{4}", source[k])
                and not any(source[k] == n for n in KEEP)]
        print(f"{code}: {len(have)} strings, {len(same)} identical to English")
    return bad


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--review", action="store_true")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--log", default=str(ROOT / "review.log"))
    args = ap.parse_args()
    codes = [c.strip() for c in args.only.split(",") if c.strip()] or list(LANGUAGES)
    if args.check:
        return 1 if check(codes) else 0

    nim = key_for_nim()
    source = read_resw(STRINGS / SOURCE / "Resources.resw")
    russian = read_resw(STRINGS / "ru-RU" / "Resources.resw")
    with ThreadPoolExecutor(max_workers=len(codes)) as pool:
        if args.review:
            results = list(pool.map(lambda c: review_language(nim, c, source, Path(args.log)), codes))
            print("corrected:", dict(zip(codes, results)))
        else:
            results = list(pool.map(lambda c: translate_language(nim, c, source, russian), codes))
            left = {c: len(r) for c, r in zip(codes, results) if r}
            if left:
                print("left in English:", left)
                return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
