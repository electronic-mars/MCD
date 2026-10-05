"""Which meaning-group an icon belongs to.

Hugeicons' own sixty categories run from fifteen icons to six hundred, which is
no way to browse a picker: one page is an entire afternoon and the next is a
line. These groups are drawn so that what the program carries - a few hundred to
two thousand icons - falls into groups of about the same size, each a thing a
person would look under: "Food", "Sport", "Money and work".

An icon is placed by its Hugeicons category first; a category that is a grab-bag
(logos, games, editing, foods) is then split by the words in the icon's name.
Rules run in order and the first match wins, so the order is part of the
meaning. Nothing here is clever, on purpose: when a group looks wrong, the fix
is a word added to a list.

    python tools/panel/groups.py    # the sizes, for the icons ticked and the whole set
"""

from __future__ import annotations

import collections
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]

# id -> (English, Russian), in the order the picker lists them.
GROUPS: dict[str, tuple[str, str]] = {
    "ui": ("Interface", "Интерфейс и стрелки"),
    "design": ("Shapes and drawing", "Фигуры и рисование"),
    "docs": ("Files and learning", "Файлы и учёба"),
    "comm": ("Social and messaging", "Соцсети и связь"),
    "soft": ("Apps and code", "Программы и код"),
    "money": ("Money and work", "Деньги и работа"),
    "tech": ("Devices and power", "Устройства и энергия"),
    "places": ("Transport and places", "Транспорт и места"),
    "food": ("Food", "Еда"),
    "kitchen": ("Kitchen and drinks", "Кухня и напитки"),
    "health": ("Medicine", "Медицина"),
    "sport": ("Sport", "Спорт"),
    "fun": ("Games and fun", "Игры и досуг"),
    "nature": ("Nature and space", "Природа и космос"),
    "people": ("People and faces", "Люди и эмоции"),
}

# Hugeicons category -> group, for what is not split further.
CATEGORY = {
    "arrows": "ui", "add-remove": "ui", "check": "ui", "menu": "ui", "layout": "ui",
    "filter-sorting": "ui", "link-unlink": "ui", "search": "ui", "login-logout": "ui",
    "download-upload": "ui", "shapes": "design", "animation": "ui", "alert": "ui",
    "bookmark": "ui", "mathematics": "ui", "hierarchy": "ui", "editing": "ui",
    "settings": "ui", "dashboard": "ui", "date-time": "ui", "other": "ui",
    "image-camera": "design",
    "files-folders": "docs", "notes-tasks": "docs", "education": "docs",
    "presentation": "docs", "legal": "docs",
    "communications": "comm",
    "programming": "soft", "ai": "soft", "security": "soft", "git": "soft", "logos": "soft",
    "business": "money", "e-commerce": "money", "crypto": "money", "award": "money",
    "devices": "tech", "mouse": "tech", "wifi": "tech", "energy": "tech",
    "logistics": "places", "maps": "places", "buildings": "places", "home": "places",
    "furnitures": "places",
    "foods": "food", "kitchen": "kitchen",
    "medical": "health",
    "gym": "sport",
    "games": "fun", "media": "fun", "islamic": "fun",
    "weather": "nature", "space": "nature", "science-technology": "nature",
    "users": "people", "hands": "people", "emojis": "people", "clothing": "people",
}


def words(text: str) -> list[str]:
    return [w for w in re.split(r"[^a-z0-9]+", text.lower()) if w]


def has(name: str, *needles: str) -> bool:
    """Whether any needle is a whole word of the name, or begins one."""
    parts = words(name)
    return any(p == n or p.startswith(n) for p in parts for n in needles)


SOCIAL = (
    "facebook", "instagram", "twitter", "tiktok", "whatsapp", "telegram", "viber", "wechat", "line",
    "messenger", "snapchat", "skype", "signal", "slack", "discord", "vk", "imo", "threads", "mastodon",
    "bluesky", "reddit", "pinterest", "tumblr", "linkedin", "youtube", "twitch", "vimeo", "quora",
    "medium", "blogger", "flickr", "bebo", "digg", "forrst", "foursquare", "hangout", "periscope",
    "plaxo", "stumbleupon", "vine", "xing", "yelp", "zoom", "behance", "dribbble", "deviantart",
    "wattpad", "soundcloud", "spotify", "last", "swarm", "mymind", "skool", "kickstarter", "tiltify",
    "ko", "castbox", "amie", "airbnb", "uber", "waze", "klarna",
)
DESIGN_LOGO = (
    "adobe", "figma", "sketch", "framer", "webflow", "capcut", "canva", "creative", "envato",
    "flaticon", "iconjar", "lottiefiles", "pexels", "unsplash", "shutterstock", "picasa", "layers",
    "brandfetch", "hugeicons", "shadcn", "tailwindcss", "bootstrap",
)
SPORT = (
    "american", "badminton", "baseball", "basketball", "bicycle", "bike", "billiard", "bowling", "boxer",
    "boxing", "cricket", "curling", "fencing", "fins", "football", "frisbee", "golf", "gymnastic",
    "hockey", "ice", "kayak", "kite", "paragliding", "pool", "ski", "surfboard", "swimming",
    "tennis", "trampoline", "volleyball", "water", "wind", "olympic", "racing", "roller", "whistle",
    "dart", "archer", "skipping", "running", "hiking", "yoga", "workout", "treadmill", "dumbbell",
)
ANIMALS = (
    "cat", "bird", "horse", "panda", "rabbit", "rat", "squirrel", "turtle", "mushroom", "paw", "clover",
    "snail", "crab", "fish", "octopus", "seal", "worm", "bacteria", "feather", "flower", "plant", "tree",
    "leaf", "leafy", "pine", "cactus", "sprout", "wheat",
)
DRINK_KITCHEN = (
    "beer", "wine", "tea", "coffee", "milk", "soda", "drink", "juice", "bubble", "cup", "glass",
    "bottle", "kettle", "pot", "pan", "knife", "knives", "fork", "spoon", "spatula", "whisk", "oven",
    "stove", "fridge", "refrigerator", "microwave", "mixer", "blender", "dish", "plate", "utensils",
    "chopsticks", "rolling", "apron", "napkins", "mortar", "jar", "bbq", "grill", "cooking", "kitchen",
    "restaurant", "chef", "waiter", "cutlery", "silverware", "toaster", "fry", "hand", "soft", "cocktail",
    "martini", "bar", "ice", "tissue",
)
BODY = (
    "ear", "nose", "lungs", "kidneys", "liver", "brain", "bone", "blood", "digestion", "eye", "tooth",
    "dental", "heart", "muscle", "biceps", "shoulder", "back", "tongue", "sperm", "cells", "dna",
    "bacteria", "stethoscope", "pulse", "cardiogram", "x", "virus",
)
UI_TOOLS = (
    "brush", "paint", "pen", "lasso", "magic", "wand", "drafting", "drawing", "highlighter", "quill",
    "pencil", "droplet", "blur", "transparency", "layer", "torus", "dropper", "pipette", "palette",
    "eraser", "scissor", "scissors", "bezier", "vector", "shape",
)


def group_of(name: str, category: str = "other", tags: str = "") -> str:
    base = CATEGORY.get(category, "ui")

    if category == "logos":
        if has(name, *SOCIAL):
            return "comm"
        if has(name, *DESIGN_LOGO):
            return "design"
        return "soft"

    if category == "games":
        if has(name, *SPORT):
            return "sport"
        if has(name, *ANIMALS):
            return "nature"
        if has(name, "hand", "handshake", "handbag", "person", "contact", "mask", "drama", "hat", "ghost"):
            return "people"
        return "fun"

    if category == "foods":
        if has(name, *DRINK_KITCHEN):
            return "kitchen"
        return "food"

    if category == "editing":
        if has(name, *UI_TOOLS):
            return "design"
        if has(name, *ANIMALS) or has(name, "bike", "bus"):
            return "nature" if has(name, *ANIMALS) else "places"
        if has(name, "biceps", "eye", "frown"):
            return "people"
        if has(name, "book", "languages", "alphabet", "heading", "bold", "case"):
            return "docs"
        if has(name, "briefcase", "dollar", "wallet"):
            return "money"
        return "ui"

    if category == "business":
        if has(name, "restaurant", "waiter", "party", "balloon", "balloons", "umbrella", "luggage", "passport"):
            return "kitchen" if has(name, "restaurant", "waiter") else "fun" if has(name, "party", "balloon", "balloons") else "places"
        if has(name, "microscope", "brain", "scan", "lightbulb"):
            return "nature" if has(name, "microscope") else "soft"
        return "money"

    if category == "devices":
        if has(name, "game", "gamepad", "joystick", "console", "nintendo", "vr"):
            return "fun"
        return "tech"

    if category == "medical" and has(name, *SPORT):
        return "sport"

    if category in ("files-folders", "education") and has(name, "graduation", "graduate", "student", "school"):
        return "docs"

    if category == "media" and has(name, "camera", "photo", "image", "picture", "video"):
        return "design"

    if category == "energy" and has(
        name, "sun", "wind", "solar", "renewable", "nuclear", "fire", "water", "mountain", "plant", "eco",
        "tree", "test", "flask", "recycle", "radiation", "loading"):
        return "nature"

    if category == "shapes" and has(name, "zodiac"):
        return "nature"

    return base


def program_icons() -> dict[str, str]:
    """Program name -> Hugeicons name, as tools/fetch_icons.py has it."""
    source = (ROOT / "tools" / "fetch_icons.py").read_text(encoding="utf-8-sig")
    block = source.split("ICONS: dict[str, str] = {", 1)[1].split("\n}", 1)[0]
    return dict(re.findall(r'"(\w+)":\s*"([^"]+)"', block))


def write_program_groups() -> int:
    """
    Writes ``tools/icon-groups.json``: the group of every icon the program
    carries, by Hugeicons name. Committed, so that generating the icon library
    needs no network; fetch_icons.py reads it.
    """
    cache = HERE / "cache" / "catalogue.json"
    known = {i["name"]: i for i in json.loads(cache.read_text(encoding="utf-8"))["icons"]} \
        if cache.exists() else {}
    names = sorted(set(program_icons().values()))
    placed: dict[str, str] = {}

    for name in names:
        # A drawing made here from another (coffee-04-empty is coffee-04 with
        # its steam taken off) belongs with the one it was made from.
        meta = known.get(name) or known.get(name.removesuffix("-empty"), {})
        placed[name] = group_of(name.removesuffix("-empty"), meta.get("category", "other"), meta.get("tags", ""))

    target = ROOT / "tools" / "icon-groups.json"
    target.write_text(
        json.dumps({"order": list(GROUPS), "groups": placed}, ensure_ascii=False, indent=1) + "\n",
        encoding="utf-8",
    )
    return len(placed)


def report() -> None:
    data = json.loads((HERE / "site" / "data" / "icons.json").read_text(encoding="utf-8"))
    tags = {i["n"]: (i["c"], i.get("t", "")) for i in data["icons"]}
    selected = set()
    path = ROOT / "assets" / "icons-selected.json"

    if path.exists():
        selected = set(json.loads(path.read_text(encoding="utf-8"))["selected"])

    carried = {i["n"] for i in data["icons"] if i.get("p")}
    final = [n for n in tags if n in selected or n in carried]

    def count(names):
        c = collections.Counter(group_of(n, *tags[n]) for n in names)
        return c

    print(f"the program would carry {len(final)} icons")

    for gid, (en, ru) in GROUPS.items():
        print(f"  {gid:8} {count(final)[gid]:4}   (whole set {count(tags)[gid]:4})   {ru}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")

    if "--write" in sys.argv:
        print(f"{write_program_groups()} icons placed in tools/icon-groups.json")
    else:
        report()
