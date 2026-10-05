"""Builds the data the control-panel site shows.

    python tools/panel/build.py

Writes ``tools/panel/site/data/icons.json`` and ``elements.json``. Both are
generated and not committed: the icons come from the free Hugeicons set (MIT,
through Iconify, with the categories and search words from hugeicons.com's own
catalogue), and the elements are read out of this repository - the widget
catalogue, the string tables, the settings model - so the page cannot drift
from the program it describes.

The two source files are cached under ``tools/panel/cache`` after the first
run; delete that folder to fetch them again.
"""

from __future__ import annotations

import io
import json
import pathlib
import re
import urllib.request
import xml.etree.ElementTree as ET

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
CACHE = HERE / "cache"
DATA = HERE / "site" / "data"

ICONIFY = "https://raw.githubusercontent.com/iconify/icon-sets/master/json/hugeicons.json"
CATALOGUE = "https://hugeicons.com/api/icons"
AGENT = {"User-Agent": "master-control-dock/panel"}


def fetch(url: str, to: pathlib.Path) -> None:
    CACHE.mkdir(parents=True, exist_ok=True)
    print(f"fetching {url}")
    request = urllib.request.Request(url, headers=AGENT)

    with urllib.request.urlopen(request, timeout=180) as response:
        to.write_bytes(response.read())


def cached(name: str, url: str) -> dict:
    path = CACHE / name

    if not path.exists():
        fetch(url, path)

    return json.loads(path.read_text(encoding="utf-8"))


# --------------------------------------------------------------------------
# Icons
# --------------------------------------------------------------------------

def program_icons() -> dict[str, str]:
    """Program name -> Hugeicons name, as tools/fetch_icons.py has it."""
    source = (ROOT / "tools" / "fetch_icons.py").read_text(encoding="utf-8-sig")
    block = source.split("ICONS: dict[str, str] = {", 1)[1].split("\n}", 1)[0]
    return dict(re.findall(r'"(\w+)":\s*"([^"]+)"', block))


def build_icons() -> dict:
    import groups

    iconify = cached("hugeicons.json", ICONIFY)
    catalogue = {i["name"]: i for i in cached("catalogue.json", CATALOGUE)["icons"]}
    carried = program_icons()
    named = {huge: name for name, huge in carried.items()}

    # Placed in the meaning-groups of tools/panel/groups.py, the same ones the
    # program's own picker lists; Hugeicons' own category is kept as "k" so it
    # can still be searched for.
    counts: dict[str, int] = {}
    icons = []

    for name, icon in sorted(iconify["icons"].items()):
        meta = catalogue.get(name, {})
        category = meta.get("category") or "other"
        group = groups.group_of(name, category, meta.get("tags", ""))
        counts[group] = counts.get(group, 0) + 1

        entry = {"n": name, "c": group, "k": category, "b": icon["body"]}

        if meta.get("tags"):
            entry["t"] = meta["tags"]

        if name in named:
            entry["p"] = named[name]

        icons.append(entry)

    return {
        "license": "Hugeicons free set, MIT (via Iconify)",
        "inProgram": len(named),
        "categories": [
            {"id": g, "name": ru, "count": counts.get(g, 0)}
            for g, (_, ru) in groups.GROUPS.items()
        ],
        "icons": icons,
    }


# --------------------------------------------------------------------------
# Elements
# --------------------------------------------------------------------------


def resw(lang: str) -> dict[str, str]:
    path = ROOT / "src" / "MCD.App" / "Strings" / lang / "Resources.resw"
    tree = ET.parse(io.StringIO(path.read_text(encoding="utf-8-sig")))
    return {d.get("name"): (d.findtext("value") or "") for d in tree.getroot().iter("data")}


def drawn_icons() -> dict[str, str]:
    """Icon name -> the path the program draws it with."""
    source = (ROOT / "src" / "MCD.App" / "Widgets" / "IconLibrary.g.cs").read_text(encoding="utf-8-sig")
    return dict(re.findall(r'\["(\w+)"\]\s*=\s*"([^"]+)"', source))


def widget_types() -> list[dict]:
    """The widget catalogue, read from the code: type id, names, descriptions, icon."""
    ru, en = resw("ru-RU"), resw("en-US")
    sources = {}

    for path in (ROOT / "src" / "MCD.App" / "Widgets").glob("*.cs"):
        text = path.read_text(encoding="utf-8-sig")
        klass = re.search(r"class (\w+)", text)
        type_id = re.search(r'public const string Type = "([\w.]+)"', text)

        if klass and type_id:
            sources[klass.group(1)] = type_id.group(1)

    catalogue = (ROOT / "src" / "MCD.App" / "Widgets" / "WidgetCatalog.cs").read_text(encoding="utf-8-sig")
    block = catalogue.split("public static ImmutableArray<WidgetType> All", 1)[1].split("];", 1)[0]

    found = []

    for klass, name_key, name_default, desc_key, desc_default, icon in re.findall(
        r'new\(\s*(\w+)\.Type,\s*Loc\.Tr\("(\w+)",\s*"([^"]*)"\),\s*'
        r'Loc\.Tr\("(\w+)",\s*"([^"]*)"\),\s*"(\w+)"',
        block,
    ):
        found.append({
            "type": sources.get(klass, klass),
            "name": ru.get(name_key) or name_default,
            "nameEn": en.get(name_key) or name_default,
            "about": ru.get(desc_key) or desc_default,
            "icon": icon,
        })

    return found


# What each widget does, as a person meets it. Written by hand: this is the
# part that cannot be read out of the code. Keyed by widget type id.
WIDGET_NOTES = {
    "mcd.gauge": {
        "group": "Показания",
        "how": [
            "Одна цифра: загрузка процессора, памяти или видеокарты, либо скорость сети (отправка и приём).",
            "У процессора, памяти и видеокарты рядом можно показать температуру под тем же значком.",
            "Цифры стоят в своей коробке: панель не дёргается, когда число меняется.",
            "На обычной панели значок крупный, справа название сверху и цифры снизу; на компактной — одна строка.",
        ],
        "options": ["Писать нагрузку", "Писать температуру (хотя бы одно из двух всегда включено)"],
        "reads": "Счётчики производительности Windows, память, NVML (NVIDIA), датчики процессора через службу.",
        "note": "Значок показания один на все панели: «Оформление → Значки показаний».",
    },
    "mcd.temp": {
        "group": "Показания",
        "how": [
            "Температура одной детали: процессор, память, видеокарта, диск.",
            "Деталь выбирается в настройках виджета из любых датчиков температуры.",
            "Самая горячая деталь помечена ▲ перед именем.",
            "Цвет меняется по порогам, которые называет сама деталь.",
        ],
        "options": ["Какой датчик показывать"],
        "reads": "Датчики температуры процессора, видеокарты, модулей памяти и дисков.",
    },
    "mcd.icon": {
        "group": "Запуск",
        "how": [
            "Нажатие запускает программу, открывает папку или адрес.",
            "Файл, брошенный на панель, становится значком; ярлык закрепляется как то, на что он указывает.",
            "Значок — родной (из файла) или нарисованный из набора; меняется в настройках виджета.",
            "Один и тот же файл дважды перетаскиванием не добавляется.",
        ],
        "options": ["Имя", "Значок: из набора или «значок самой программы»"],
        "note": "Полка «Программы» в галерее — готовые закреплённые значки для программ, которые приносит Windows.",
    },
    "mcd.sound": {
        "group": "Звук",
        "how": [
            "Динамик: нажатие открывает ползунок громкости.",
            "Цифры процентов: нажатие выключает или включает звук.",
            "Колесо мыши над виджетом меняет громкость на 2 % за щелчок.",
            "Подъём громкости при выключенном звуке снимает выключение, как на клавиатуре.",
            "Динамик рисуется в одном из четырёх состояний: тихо, мало, средне, громко.",
        ],
        "options": ["Писать громкость цифрой (есть и на компактной панели)"],
        "reads": "Громкость устройства, на которое сейчас идёт звук.",
        "note": "Прячется на машине без устройства воспроизведения.",
    },
    "mcd.mic": {
        "group": "Звук",
        "how": [
            "Нажатие выключает микрофон во всей системе, в любой программе; ещё одно — включает.",
            "Пока микрофон выключен, значок красный.",
            "Есть общее сочетание клавиш «Выключить микрофон» (страница «Общие»).",
        ],
        "reads": "Устройство записи по умолчанию.",
        "note": "Прячется на машине без устройства записи.",
    },
    "mcd.switcher": {
        "group": "Звук",
        "how": [
            "Master Audio Switcher прямо на панели: нажатие переводит звук на следующее устройство.",
            "Следующее устройство рисуется сразу по нажатию; если ответа нет 10 секунд, состояние перечитывается.",
            "Если у Master Audio Switcher выключен звук, показан красный крестик.",
            "Правый клик: открыть Master Audio Switcher или выбрать устройство из списка.",
        ],
        "note": "Предлагается в галерее, только пока программа запущена. Значок из трея она отдаёт панели только пока виджет на экране.",
    },
    "mcd.battery": {
        "group": "Система",
        "how": [
            "Заряд батареи ноутбука: значок из четырёх состояний и цифра.",
            "На стационарном компьютере не показывается и места не занимает.",
            "Подсказка: процент, питание от сети, сколько осталось.",
        ],
        "reads": "Состояние питания Windows.",
    },
    "mcd.wifi": {
        "group": "Система",
        "how": [
            "Уровень сигнала Wi-Fi: значок из четырёх уровней.",
            "Только в беспроводной сети; при проводе слот отдаётся.",
            "Подсказка: сеть, диапазон, скорость соединения.",
        ],
        "options": ["Писать имя сети"],
        "reads": "WLAN API Windows.",
    },
    "mcd.clock": {
        "group": "Система",
        "how": [
            "Время; на обычной панели под ним дата.",
            "Слот сделан под самое широкое время, поэтому панель не сдвигается.",
        ],
        "options": ["Показывать секунды", "Показывать дату под временем"],
    },
    "mcd.media": {
        "group": "Звук",
        "how": [
            "Кнопки «назад», «пауза», «вперёд» для того, что сейчас играет, и обложка: нажатие на неё открывает плеер.",
            "Пока ничего не играет, кнопки тусклые, а не пропадают.",
            "Название трека пишется на панели или лежит в подсказке.",
        ],
        "options": ["Писать трек на панели", "Какой плеер ведут кнопки"],
        "reads": "Системные медиасессии Windows.",
    },
    "mcd.device": {
        "group": "Устройства",
        "how": [
            "Заряд беспроводной мыши, гарнитуры или клавиатуры: значок батареи вместо имени.",
            "Жёлтый при 10 %, красный при 5 %, красный треугольник на углу значка при критическом заряде.",
            "Неактивное устройство рисуется полупрозрачным вместе со значком батареи.",
        ],
        "reads": "Заряд, который Windows сама читает с Bluetooth-устройства; у ключа гарнитуры HyperX — напрямую.",
    },
    "mcd.awake": {
        "group": "Система",
        "how": [
            "Полная чашка с паром: компьютер не засыпает, а чаты не показывают «нет на месте». Пустая — как обычно.",
            "Правый клик: на 30 минут, на час, на два часа или пока не выключу.",
            "Курсор не двигается и ничего не нажимается: раз в 50 секунд идёт сдвиг мыши на ноль пикселей, который сбрасывает счётчик простоя.",
            "Включена по умолчанию: с первой чашкой на панели компьютер уже не спит. Выключили вручную — остаётся выключенной до следующего запуска программы.",
        ],
    },
    "mcd.layout": {
        "group": "Система",
        "how": [
            "Две буквы языка, на котором печатает окно, где вы сейчас работаете.",
            "Нажатие переключает раскладку у этого окна.",
            "Включённый Caps Lock — плашка цвета акцента под буквами; ширина виджета не меняется.",
        ],
    },
    "mcd.settings": {
        "group": "Система",
        "how": [
            "Шестерёнка: нажатие открывает это окно настроек.",
            "Нужна тем, кто ещё не нашёл правый клик по панели.",
        ],
    },
}

PAGES = [
    ("docks", "NavDocks", "DocksIntro.Text", [
        "Включить или выключить панель на каждом экране.",
        "Край экрана, режим (закрепить, прятать, на рабочем столе), толщина (обычная или компактная), поверх ли остальных окон.",
        "Схема экранов с лампами: какой экран сейчас редактируется.",
    ]),
    ("widgets", "NavWidgets", "WidgetsIntro.Text", [
        "Что лежит на выбранной панели и галерея того, что можно добавить.",
        "Добавить: плюс на плашке ставит виджет в первый свободный промежуток, никого не двигая; можно и перетащить на панель.",
        "Настройки каждого виджета открываются выбором его в списке «Что на панели».",
    ]),
    ("pins", "NavPins", "PinsIntro.Text", [
        "Закреплённые программы, папки и адреса: список, имя, значок, «Открыть папку».",
        "Добавить можно и перетаскиванием файла прямо на панель.",
    ]),
    ("presets", "NavPresets", "PresetsIntro.Text", [
        "Сохранённые наборы виджетов: сохранить то, что на панели сейчас, и поставить любой набор на панель выбранного экрана.",
    ]),
    ("appearance", "NavAppearance", "AppearanceIntro.Text", [
        "Тема (светлая, тёмная, как в Windows), подложка (прозрачная, сплошная, свой цвет, картинка), акцент, размер показаний.",
        "Значки показаний: каким рисунком рисуется процессор, память и остальные, на всех панелях сразу.",
        "Живой образец полосы.",
    ]),
    ("general", "NavGeneral", "GeneralIntro.Text", [
        "Сочетания клавиш: спрятать или показать панели, открыть окно, выключить звук, выключить микрофон.",
        "Язык, запуск вместе с Windows, резервная копия и загрузка копии.",
    ]),
    ("sensors", "NavSensors", "SensorsIntro.Text", [
        "Все датчики, которые программа находит на этой машине, и их имена: карандаш переименовывает.",
    ]),
    ("about", "NavAbout", "AboutIntro", [
        "Версия, лицензия, источники значков и кода.",
    ]),
]

KEYS = [
    ("Показать или спрятать все панели", "Панели уходят и возвращаются; расстановка сохраняется."),
    ("Открыть окно настроек", "Откуда угодно, в том числе поверх полноэкранной программы."),
    ("Выключить звук", "То же, что клавиша mute, для клавиатур, где её нет."),
    ("Выключить микрофон", "Выключает и снова включает микрофон во всей системе."),
]

BAR = [
    ("Край", "Слева, сверху, справа или снизу. Панель сверху и снизу идёт вдоль экрана, слева и справа — по вертикали."),
    ("Режим", "Закреплена (занимает место, развёрнутые окна упираются в неё), прячется (выезжает, когда указатель у края), лежит на рабочем столе (ничего не занимает, любое окно её закрывает)."),
    ("Толщина", "Обычная (две строки, имена видны) или компактная (одна строка)."),
    ("Поверх окон", "Панель выше остальных окон; полноэкранная программа всё равно её перекрывает."),
    ("Клетки", "Панель — ряд клеток. Виджет стоит на какой-то клетке и занимает столько, сколько ему нужно; пустое место — свободные клетки. Всё, что вы расставили вручную, остаётся как было, в том числе при перезапуске."),
]

GESTURES = [
    ("Перетащить виджет", "Взяться за него и перенести в другое место панели; голубая метка показывает, куда он встанет."),
    ("Удержать полсекунды", "Белая рамка: виджет взят. Можно тащить, а если отпустить на месте — это просто нажатие."),
    ("Утащить с панели", "За край: виджет полупрозрачный, красные клетки; отпустить — убрать."),
    ("Правый клик", "Меню виджета (если есть своё), «Настроить…», «Спрятать все панели», «Выход»."),
    ("Бросить файл на панель", "Закрепляется как значок в той клетке, куда бросили."),
    ("Перетащить плашку из окна настроек", "Виджет встаёт в клетку под указателем; рядом с указателем ничего не рисуется, место видно по голубой метке."),
    ("Отмена", "После каждого изменения внизу окна настроек появляется «Готово: … · Отменить»."),
]

SOURCES = [
    ("Нагрузка и сеть", "Счётчики производительности Windows (PDH)."),
    ("Память", "GlobalMemoryStatusEx."),
    ("Видеокарта NVIDIA", "NVML: загрузка, температура, вентилятор, частоты, мощность."),
    ("Диски", "Температура NVMe и SATA, в том числе пределы, которые называет сам диск; доступ на нулевое чтение, права администратора не нужны."),
    ("Процессор и модули памяти", "Через службу MasterControlDock.Sensors (драйвер PawnIO читает только администратор, поэтому читает служба, а панель остаётся обычной)."),
    ("Заряд устройств", "Свойство «уровень заряда» Bluetooth-устройства в Windows и ключ-приёмник гарнитуры HyperX."),
    ("Батарея и Wi-Fi", "Состояние питания Windows и WLAN API."),
]

FILES = [
    ("%LOCALAPPDATA%\\MCD\\config.json", "Все настройки: панели, виджеты и их клетки. Рядом config.bak — предыдущая запись."),
    ("%LOCALAPPDATA%\\MCD\\logs\\mcd.log", "Журнал (три файла по кругу). Каждое изменение настроек и каждый запуск программы в нём названы."),
    ("%LOCALAPPDATA%\\Programs\\MasterControlDock", "Установленная программа."),
    ("tools\\panel", "Эта панель управления. Запуск: tools\\panel.cmd."),
]

DEV = [
    ("MCD_SELFTEST=1", "Программа сама себя закрывает через MCD_SELFTEST_SECONDS и прогоняет репетицию: окна, меню, перестройку панелей."),
    ("MCD_DATA_DIR", "Свой каталог настроек: проверка на копии, не трогая живые настройки."),
    ("MCD_SELFTEST_FLIP", "density, bars, press, switch, editswitch, glyphs: нажатия, которые делает человек, без человека."),
    ("MCD_SELFTEST_PAGE", "Обойти и сфотографировать страницы настроек."),
]


def build_elements() -> dict:
    ru = resw("ru-RU")
    en = resw("en-US")
    widgets = []

    for entry in widget_types():
        note = WIDGET_NOTES.get(entry["type"], {})
        widgets.append({**entry, **note})

    pages = []

    for tag, nav_key, intro_key, bullets in PAGES:
        pages.append({
            "tag": tag,
            "name": ru.get(nav_key + ".Text") or ru.get(nav_key) or en.get(nav_key + ".Text") or tag,
            "intro": ru.get(intro_key) or en.get(intro_key) or "",
            "has": bullets,
        })

    return {
        "widgets": widgets,
        "pages": pages,
        "keys": [{"name": n, "about": a} for n, a in KEYS],
        "bar": [{"name": n, "about": a} for n, a in BAR],
        "gestures": [{"name": n, "about": a} for n, a in GESTURES],
        "sources": [{"name": n, "about": a} for n, a in SOURCES],
        "files": [{"name": n, "about": a} for n, a in FILES],
        "dev": [{"name": n, "about": a} for n, a in DEV],
        "drawn": drawn_icons(),
    }


def main() -> None:
    DATA.mkdir(parents=True, exist_ok=True)

    icons = build_icons()
    (DATA / "icons.json").write_text(
        json.dumps(icons, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"icons.json: {len(icons['icons'])} icons in {len(icons['categories'])} categories, "
          f"{icons['inProgram']} already in the program")

    elements = build_elements()
    (DATA / "elements.json").write_text(
        json.dumps(elements, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"elements.json: {len(elements['widgets'])} widgets, {len(elements['pages'])} pages")


if __name__ == "__main__":
    main()
