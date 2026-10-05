"use strict";

const $ = (id) => document.getElementById(id);

function el(tag, attrs = {}, ...kids) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else node.setAttribute(k, v);
  }
  for (const kid of kids.flat()) {
    if (kid == null) continue;
    node.append(kid.nodeType ? kid : document.createTextNode(kid));
  }
  return node;
}

function drawing(path) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", path);
  svg.append(p);
  return svg;
}

function pairs(target, rows, code) {
  target.replaceChildren(...rows.flatMap((r) => [
    el("dt", {}, code ? el("code", {}, r.name) : r.name),
    el("dd", {}, r.about),
  ]));
}

async function main() {
  const data = await (await fetch("data/elements.json", { cache: "no-store" })).json();
  let carried = 0;
  try {
    carried = (await (await fetch("data/icons.json", { cache: "no-store" })).json()).inProgram;
  } catch (_) { /* the icons page has its own count */ }

  $("stats").replaceChildren(
    ...[
      [data.widgets.length, "видов виджетов"],
      [data.pages.length, "страниц настроек"],
      [data.keys.length, "сочетания клавиш"],
      [carried || "—", "значков в программе"],
    ].map(([n, label]) => el("div", { class: "stat" }, el("b", {}, String(n)), el("span", {}, label))),
  );

  const groups = new Map();
  for (const w of data.widgets) {
    const g = w.group || "Другое";
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g).push(w);
  }

  const order = ["Показания", "Звук", "Система", "Запуск", "Устройства", "Другое"];
  const container = $("groups");

  for (const g of order) {
    const list = groups.get(g);
    if (!list) continue;

    container.append(el("h3", { style: "margin:22px 0 10px;font-size:15px;color:var(--ink2);font-weight:600" }, g));

    container.append(el("div", { class: "grid" }, list.map((w) => {
      const card = el("div", { class: "card" });
      card.append(el("div", { class: "head" },
        el("div", { class: "ico" }, data.drawn[w.icon] ? drawing(data.drawn[w.icon]) : null),
        el("div", {},
          el("h3", {}, w.name),
          el("div", { class: "sub" }, `${w.type} · значок ${w.icon}`),
        ),
      ));
      card.append(el("p", { class: "about" }, w.about));
      if (w.how) card.append(el("ul", {}, w.how.map((t) => el("li", {}, t))));
      if (w.options) card.append(el("div", { class: "meta" }, el("b", {}, "Настройки"), w.options.join(" · ")));
      if (w.reads) card.append(el("div", { class: "meta" }, el("b", {}, "Читает"), w.reads));
      if (w.note) card.append(el("div", { class: "meta" }, el("b", {}, "Заметка"), w.note));
      return card;
    })));
  }

  pairs($("bar"), data.bar);
  pairs($("gestures"), data.gestures);
  pairs($("keys"), data.keys);
  pairs($("sources"), data.sources);
  pairs($("files"), data.files, true);
  pairs($("dev"), data.dev, true);

  $("pages").replaceChildren(...data.pages.map((p) => el("div", { class: "card" },
    el("h3", {}, p.name),
    el("p", { class: "about" }, p.intro),
    el("ul", {}, p.has.map((t) => el("li", {}, t))),
  )));
}

main().catch((e) => {
  document.querySelector("main").prepend(el("p", { class: "lead" },
    "Данных нет: запустите python tools/panel/build.py. (" + e + ")"));
});
