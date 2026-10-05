"use strict";

const $ = (id) => document.getElementById(id);

const state = {
  icons: [],
  cats: [],
  catName: new Map(),
  selected: new Set(),
  text: "",
  cat: "",
  mode: "all",
  saved: null, // the last list the disk is known to hold
  server: true,
};

const STORE = "mcd-panel-selected";

// ---------------------------------------------------------------- loading

async function load() {
  const data = await (await fetch("data/icons.json", { cache: "no-store" })).json();
  state.icons = data.icons;
  state.cats = data.categories;
  for (const c of data.categories) state.catName.set(c.id, c.name);

  $("n-all").textContent = data.icons.length.toLocaleString("ru");
  $("n-cats").textContent = String(data.categories.length);
  $("n-prog").textContent = String(data.inProgram);

  const select = $("cat");
  select.append(new Option("Все разделы", ""));
  for (const c of data.categories) select.append(new Option(`${c.name} (${c.count})`, c.id));

  let chosen = [];
  try {
    const reply = await fetch("/api/selection", { cache: "no-store" });
    if (!reply.ok) throw new Error(reply.status);
    chosen = (await reply.json()).selected || [];
  } catch (_) {
    state.server = false;
    try { chosen = JSON.parse(localStorage.getItem(STORE) || "[]"); } catch (_) { /* nothing kept */ }
  }

  const known = new Set(state.icons.filter((i) => !i.p).map((i) => i.n));
  state.selected = new Set(chosen.filter((n) => known.has(n)));
  state.saved = [...state.selected].sort().join("\n");

  counters();
  status();
  rebuild();
}

// ---------------------------------------------------------------- filtering

function matches(icon) {
  if (state.cat && icon.c !== state.cat) return false;

  if (state.mode === "new" && icon.p) return false;
  if (state.mode === "program" && !icon.p) return false;
  if (state.mode === "selected" && !state.selected.has(icon.n)) return false;

  if (!state.text) return true;

  const hay = `${icon.n} ${icon.t || ""} ${icon.p || ""} ${state.catName.get(icon.c) || icon.c}`.toLowerCase();
  return state.text.split(/\s+/).every((word) => hay.includes(word));
}

// ---------------------------------------------------------------- drawing

let watcher = null;

function tile(icon) {
  const carried = Boolean(icon.p);
  const on = state.selected.has(icon.n);
  const label = document.createElement("label");
  label.className = "tile" + (carried ? " carried" : "");
  label.title = carried
    ? `${icon.n}: уже в программе как ${icon.p}`
    : `${icon.n}${icon.t ? "\n" + icon.t : ""}`;
  label.innerHTML =
    `<input type="checkbox" data-n="${icon.n}"${on ? " checked" : ""}${carried ? " disabled" : ""}>` +
    `<span class="pic"><svg viewBox="0 0 24 24" aria-hidden="true">${icon.b}</svg></span>` +
    `<span class="nm">${icon.n}</span>` +
    (carried ? `<span class="badge">в программе · ${icon.p}</span>` : "");
  return label;
}

function rebuild() {
  watcher?.disconnect();

  const sections = $("sections");
  sections.replaceChildren();

  const byCat = new Map();
  for (const icon of state.icons) {
    if (!matches(icon)) continue;
    if (!byCat.has(icon.c)) byCat.set(icon.c, []);
    byCat.get(icon.c).push(icon);
  }

  $("empty").hidden = byCat.size > 0;

  watcher = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const grid = entry.target;
      watcher.unobserve(grid);
      const list = grid._icons;
      const frag = document.createDocumentFragment();
      for (const icon of list) frag.append(tile(icon));
      grid.replaceChildren(frag);
      grid._filled = true;
    }
  }, { rootMargin: "1400px 0px" });

  for (const c of state.cats) {
    const list = byCat.get(c.id);
    if (!list) continue;

    const free = list.filter((i) => !i.p);
    const grid = document.createElement("div");
    grid.className = "tiles";
    grid._icons = list;
    grid.style.minHeight = Math.min(list.length, 12) * 12 + "px";

    const head = document.createElement("header");
    head.innerHTML =
      `<h2>${c.name}</h2><span class="count">${list.length}${list.length !== c.count ? ` из ${c.count}` : ""}</span>`;

    if (free.length) {
      const all = document.createElement("span");
      all.className = "all";
      const mark = document.createElement("button");
      mark.type = "button";
      mark.textContent = `Отметить все (${free.length})`;
      mark.onclick = () => { for (const i of free) state.selected.add(i.n); afterChange(grid); };
      const unmark = document.createElement("button");
      unmark.type = "button";
      unmark.textContent = "Снять";
      unmark.onclick = () => { for (const i of free) state.selected.delete(i.n); afterChange(grid); };
      all.append(mark, unmark);
      head.append(all);
    }

    const section = document.createElement("section");
    section.className = "cat";
    section.append(head, grid);
    sections.append(section);
    watcher.observe(grid);
  }
}

// Redraws the tiles already on screen after a bulk change, without losing the scroll place.
function afterChange(grid) {
  for (const box of document.querySelectorAll("input[data-n]")) {
    if (!box.disabled) box.checked = state.selected.has(box.dataset.n);
  }
  changed();
}

// ---------------------------------------------------------------- saving

let timer = null;

function changed() {
  counters();
  status("…");
  clearTimeout(timer);
  timer = setTimeout(save, 450);
}

async function save() {
  const list = [...state.selected].sort();
  try { localStorage.setItem(STORE, JSON.stringify(list)); } catch (_) { /* private window */ }

  if (!state.server) {
    status("только в браузере: сервер не запущен (tools/panel.cmd)");
    return;
  }

  try {
    const reply = await fetch("/api/selection", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ selected: list }),
    });
    if (!reply.ok) throw new Error(reply.status);
    state.saved = list.join("\n");
    status();
  } catch (e) {
    status("не удалось сохранить на диск: " + e, true);
  }
}

function counters() {
  $("n-sel").textContent = String(state.selected.size);
}

function status(text, bad) {
  const node = $("status");
  node.className = bad ? "bad" : "";

  if (text === "…") {
    node.textContent = `Отмечено: ${state.selected.size} · сохраняю…`;
    return;
  }

  if (text) {
    node.textContent = `Отмечено: ${state.selected.size} · ${text}`;
    return;
  }

  node.className = "good";
  node.textContent = state.server
    ? `Отмечено: ${state.selected.size} · сохранено на диск ${new Date().toLocaleTimeString("ru")}`
    : `Отмечено: ${state.selected.size} · сервер не запущен, выбор лежит только в браузере`;
}

// ---------------------------------------------------------------- wiring

document.addEventListener("change", (e) => {
  const box = e.target;
  if (!(box instanceof HTMLInputElement) || !box.dataset.n) return;
  if (box.checked) state.selected.add(box.dataset.n);
  else state.selected.delete(box.dataset.n);
  changed();
  if (state.mode === "selected" && !box.checked) rebuildSoon();
});

let soon = null;
function rebuildSoon() {
  clearTimeout(soon);
  soon = setTimeout(rebuild, 700);
}

$("q").addEventListener("input", (e) => {
  state.text = e.target.value.trim().toLowerCase();
  clearTimeout(soon);
  soon = setTimeout(rebuild, 160);
});

$("cat").addEventListener("change", (e) => { state.cat = e.target.value; rebuild(); });

for (const b of document.querySelectorAll(".seg button")) {
  b.addEventListener("click", () => {
    state.mode = b.dataset.mode;
    for (const other of document.querySelectorAll(".seg button")) {
      other.setAttribute("aria-pressed", String(other === b));
    }
    rebuild();
  });
}

$("size").addEventListener("input", (e) => {
  document.documentElement.style.setProperty("--tile", e.target.value + "px");
});

$("clear").addEventListener("click", () => {
  if (!state.selected.size) return;
  if (!confirm(`Снять все отметки (${state.selected.size})?`)) return;
  state.selected.clear();
  afterChange();
  if (state.mode === "selected") rebuild();
});

$("copy").addEventListener("click", async () => {
  const text = [...state.selected].sort().join("\n");
  try {
    await navigator.clipboard.writeText(text);
    status("список скопирован");
  } catch (_) {
    status("не вышло скопировать: список в assets/icons-selected.json", true);
  }
});

load().catch((e) => {
  $("status").className = "bad";
  $("status").textContent = "Данных нет: запустите python tools/panel/build.py (" + e + ")";
});
