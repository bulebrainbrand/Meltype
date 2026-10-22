/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Copyright (C) 2026 Konayukiw */

import { copyText, escapeHtml, formatDate, formatNumber, getJson, icon, initHeader, relativeTime, safeIcon, toast,a,div,h2,h3,b,button,span,option,pre,dt,dd,dl,p } from "./common.js";
import { FORMS, formById, formatBytes, privateLabels, toMarkdown } from "./report-schema.js";

const REPO = "https://github.com/yksr-melt/Meltype";
const COLORS = { bug: "is-pink", misdetection: "is-sky", dictionary: "is-peri", idea: "is-sky" };
const LABELS = { bug: "bug", misdetection: "誤判定", dictionary: "辞書", idea: "提案" };
const STATUS = { new: "届いた", uploading: "添付を送っている途中" };
const GROUP_ICONS = { image: "image", video: "video", text: "file", json: "braces" };
const PREVIEW_BYTES = 64 * 1024;
const ISSUE_BODY_LIMIT = 6000;

const list = document.querySelector("[data-list]");
const detail = document.querySelector("[data-detail]");
const more = document.querySelector("[data-more]");
const newButton = document.querySelector("[data-new-reports]");
const filters = {
  q: document.querySelector('[data-filter="q"]'),
  form: document.querySelector('[data-filter="form"]'),
  status: document.querySelector('[data-filter="status"]'),
};
const state = { items: [], next: null, selected: null, topId: 0, loading: false, current: null, days: null };

filters.form.replaceChildren(option("","","すべての種類"),FORMS.map(form => option("",form.id,form.label)));

function query(extra = {}) {
  const params = new URLSearchParams();
  for (const [key, element] of Object.entries(filters)) {
    if (element.value.trim()) params.set(key, element.value.trim());
  }
  for (const [key, value] of Object.entries(extra)) if (value != null) params.set(key, value);
  return params.toString();
}

function drawChart(days) {
  const svg = document.querySelector("[data-chart]");
  const width = Math.max(280, Math.round(svg.clientWidth || 600));
  const height = 160;
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  state.days = days;
  const bottom = 136;
  const max = Math.max(1, ...days.map((d) => d.count));
  const slot = width / days.length;
  const parts = [`<line class="grid" x1="0" y1="${bottom}" x2="${width}" y2="${bottom}"/>`];
  days.forEach((day, index) => {
    const h = Math.max(day.count ? 4 : 0, Math.round((day.count / max) * 118));
    const x = index * slot + 3;
    const label = day.day.slice(5).replace("-", "/");
    parts.push(
      `<rect class="bar${index === days.length - 1 ? " is-today" : ""}" x="${x.toFixed(1)}" y="${bottom - h}" width="${(slot - 6).toFixed(1)}" height="${h}" rx="3"><title>${label}: ${day.count} 件</title></rect>`,
    );
    const last = index === days.length - 1;
    if (index % 7 === 1 || last) {
      parts.push(`<text class="axis" x="${(last ? x + slot - 6 : x + (slot - 6) / 2).toFixed(1)}" y="${height - 6}" text-anchor="${last ? "end" : "middle"}">${label}</text>`);
    }
  });
  parts.push(`<text class="axis" x="4" y="12">最大 ${max} 件</text>`);
  svg.innerHTML = parts.join("");
}

function renderKinds(byForm, total) {
  document.querySelector("[data-kinds]").replaceChildren(...FORMS.map((form) => {
    const count = byForm[form.id] || 0;
    const ratio = total ? Math.round((count / total) * 100) : 0;
    const spanEle = document.createElement("span");
    spanEle.setAttribute("data-ratio",String(ratio))
    return div("kind-bar",
      div(
        span("",form.label),
        b("",formatNumber(count))
      ),
      span("meter",spanEle)
    )
  }));
  document.querySelectorAll("[data-ratio]").forEach((bar) => (bar.style.width = `${bar.dataset.ratio}%`));
}

async function loadStats() {
  try {
    const stats = await getJson("/api/dashboard/stats");
    for (const key of ["total", "today", "week", "withFiles"]) {
      document.querySelector(`[data-stat="${key}"]`).textContent = formatNumber(stats[key]);
    }
    drawChart(stats.days);
    renderKinds(stats.byForm, stats.total);
  } catch (e) {
    toast(e.message);
  }
}

function itemHtml(report) {
  const form = formById(report.form);
  const files = report.files
    ? `<span>${icon(report.groups.length === 1 ? GROUP_ICONS[report.groups[0]] || "clip" : "clip")} ${report.uploaded}/${report.files}</span>`
    : "";
  const status = report.status === "uploading" ? `<span class="badge is-pink">${STATUS.uploading}</span>` : "";
  return `<li class="report-item"><button type="button" data-id="${report.id}"${state.selected === report.id ? ' aria-current="true"' : ""}>
    <span class="report-no">#${report.id}</span>
    <span class="report-title">${escapeHtml(report.title)}</span>
    <span class="report-sub"><span class="badge ${COLORS[report.form]}">${escapeHtml(form ? form.label : report.form)}</span><span title="${escapeHtml(formatDate(report.created_at * 1000, true))}">${relativeTime(report.created_at * 1000)}</span>${files}${status}</span>
  </button></li>`;
}

function renderList() {
  list.innerHTML = state.items.length
    ? state.items.map(itemHtml).join("")
    : `<li class="empty">${filters.q.value || filters.form.value || filters.status.value ? "条件に合う報告はありません" : "まだ報告は届いていません"}</li>`;
  more.hidden = !state.next;
}

async function loadList(reset) {
  if (state.loading) return;
  state.loading = true;
  try {
    const result = await getJson(`/api/dashboard/reports?${query({ before: reset ? null : state.next })}`);
    state.items = reset ? result.reports : [...state.items, ...result.reports];
    state.next = result.next;
    if (reset && !filters.q.value && !filters.form.value && !filters.status.value && result.reports[0]) state.topId = Math.max(state.topId, result.reports[0].id);
    renderList();
  } catch (e) {
    const li =document.createElement("li");
    li.className = "empty";
    li.textContent = e.message
    list.replaceChildren(li)
  } finally {
    state.loading = false;
  }
}

function valueHtml(field, value) {
  if (Array.isArray(value)) return value.join("、");
  if (field && field.type === "code") return pre("",value);
  return value;
}

function fieldsHtml(fields, values) {
  const rows = fields
    .filter((field) => values[field.id] != null && values[field.id] !== "" && !(Array.isArray(values[field.id]) && !values[field.id].length))
    .map((field) => div(
      dt("",field.label),
      dd("",valueHtml(field, values[field.id]))
    ));
  return rows.length ? dl("kv",...rows) : p("small","書かれていません");
}

function fileUrl(report, attachment, download) {
  return `/api/dashboard/reports/${report.id}/files/${attachment.idx}${download ? "?download=1" : "?inline=1"}`;
}

function attachmentHtml(report, attachment) {
  const head = (...elements) => div("attach-head",
    safeIcon(GROUP_ICONS[attachment.grp] || "clip"),
    b("", attachment.name),
    span("", formatBytes(attachment.size)),
    ...elements
  );
  if (!attachment.uploaded_at) return div("attach",head(span("badge is-pink","届いていません")))
  const openLink = a("","開く");
  openLink.href = fileUrl(report, attachment, false)
  openLink.target = "_blank"
  openLink.rel = "noopener"
  const saveLink = a("","保存")
  saveLink.href = fileUrl(report, attachment, true)
  if (attachment.grp === "image") {
    const image = document.createElement("img")
    image.src = fileUrl(report, attachment, false)
    image.alt = attachment.name
    image.loading = "lazy"
    const link = openLink.cloneNode().append();
    link.append(image)
    return div("attach",
      head(openLink,saveLink),
      link
      );
    }
  if (attachment.grp === "video") {
    const video = document.createElement("video");
    video.src = fileUrl(report, attachment, false)
    video.controls = true
    video.preload = "metadata"
    return div("attach",head(openLink,saveLink),video)
  }
  const loadingMessage = p("","読み込んでいます…");
  loadingMessage.setAttribute("data-preview",attachment.idx)
  return div(head(openLink,saveLink),loadingMessage)
}

async function loadPreviews(report) {
  for (const element of detail.querySelectorAll("[data-preview]")) {
    const attachment = report.attachments.find((a) => String(a.idx) === element.dataset.preview);
    try {
      const response = await fetch(fileUrl(report, attachment, false), { headers: { range: `bytes=0-${PREVIEW_BYTES - 1}` } });
      if (!response.ok) throw new Error(String(response.status));
      const text = await response.text();
      if (state.current !== report) return;
      element.textContent = attachment.size > PREVIEW_BYTES ? `${text}\n\n… (最初の ${formatBytes(PREVIEW_BYTES)} だけ表示しています)` : text;
    } catch {
      element.textContent = "読み込めませんでした";
    }
  }
}

function markdownFor(report) {
  const form = formById(report.form);
  const lines = [toMarkdown(form, report.fields)];
  const files = report.attachments.filter((a) => a.uploaded_at);
  if (files.length) lines.push(`### 添付\n\n${files.map((a) => `- ${a.name} (${formatBytes(a.size)})`).join("\n")}\n\n添付は報告ダッシュボードの #${report.id} にあります。`);
  const hidden = privateLabels(form, report.fields);
  if (hidden.length) lines.push(`${hidden.join("・")}は公開しないため載せていません。報告ダッシュボードの #${report.id} にあります。`);
  lines.push(`---\n報告フォームから (#${report.id}、${formatDate(report.created_at * 1000, true)})`);
  return lines.join("\n\n");
}

function issueUrl(report) {
  let body = markdownFor(report);
  if (body.length > ISSUE_BODY_LIMIT) body = `${body.slice(0, ISSUE_BODY_LIMIT)}\n\n…(長いので途中までです。続きは報告ダッシュボードの #${report.id} を見てください)`;
  const params = new URLSearchParams({ title: report.title, body, labels: LABELS[report.form] || "" });
  return `${REPO}/issues/new?${params}`;
}

function renderDetail(report) {
  state.current = report;
  const form = formById(report.form);
  const reportSub = div("report-sub",
    b("",report.id),
    span(`badge ${COLORS[report.form]}`,form ? form.label : report.form),
    report.status === "uploading" ? span("badge is-pink",STATUS.uploading) : span("badge is-sky",STATUS.new),
    span("",formatDate(report.created_at * 1000, true))
  )

  const title = h2("",report.title);

  const copyAsIssueButton = button("btn btn-small",safeIcon("copy"),"Issue の形でコピー")
  copyAsIssueButton.type = "button";
  copyAsIssueButton.setAttribute("data-copy-md",true)
  const issueLink = a("btn btn-ghost btn-small",safeIcon("external"),"GitHub で Issue を作る")
  issueLink.href = issueUrl(report);
  issueLink.target = "_blank"
  issueLink.rel = "noopener"
  const copyLink = button("btn btn-ghost btn-small",safeIcon("link"),"この報告へのリンク");
  copyLink.type = "button"
  copyLink.setAttribute("data-copy-link",true);
  const detailActions = div("detail-actions",copyAsIssueButton,issueLink,copyLink)

  const detailHead = div("detail-head",reportSub,title,detailActions);
  detail.replaceChildren(
    detailHead,
    div("detail-section",
      h3("",
        span("pane-tag is-req","必須"),
        "かならず書く所"),
      fieldsHtml(form ? form.required : [], report.fields)
    ),
    div("detail-section",
      h3("",
        span("pane-tag is-opt","任意"),
        "あれば書く所"),
      fieldsHtml(form ? form.optional : [], report.fields)
    ),
    div("detail-section",
      h3("",
        safeIcon("clip"),
        `添付 (${report.attachments.length})`
      ),
      report.attachments.length ? 
        div("attach-grid", ...report.attachments.map((a) => attachmentHtml(report, a)))
      : p("small","添付はありません")
    )
  );
  detail.querySelector("[data-copy-md]").addEventListener("click", async () => toast((await copyText(markdownFor(report))) ? "Issue の形でコピーしました" : "コピーできませんでした"));
  detail.querySelector("[data-copy-link]").addEventListener("click", async () => toast((await copyText(`${location.origin}/dashboard/?id=${report.id}`)) ? "リンクをコピーしました" : "コピーできませんでした"));
  loadPreviews(report);
}

async function select(id, scroll) {
  state.selected = id;
  list.querySelectorAll("button[data-id]").forEach((button) => {
    if (Number(button.dataset.id) === id) button.setAttribute("aria-current", "true");
    else button.removeAttribute("aria-current");
  });
  const url = new URL(location.href);
  url.searchParams.set("id", String(id));
  history.replaceState(null, "", url);
  detail.replaceChildren( p("detail-empty","読み込んでいます…"));
  try {
    renderDetail(await getJson(`/api/dashboard/reports/${id}`));
    if (scroll && window.matchMedia("(max-width: 1000px)").matches) detail.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (e) {
    detail.replaceChildren(p("detail-empty",e.message))
  }
}

async function checkNew() {
  if (document.hidden) return;
  try {
    const result = await getJson("/api/dashboard/reports?limit=1");
    const top = result.reports[0];
    if (top && top.id > state.topId && state.topId) {
      newButton.textContent = `新しい報告 #${top.id} が届きました`;
      newButton.hidden = false;
    }
  } catch {
    return;
  }
}

let searchTimer = 0;
filters.q.addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => loadList(true), 300);
});
filters.form.addEventListener("change", () => loadList(true));
filters.status.addEventListener("change", () => loadList(true));
more.addEventListener("click", () => loadList(false));
list.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-id]");
  if (button) select(Number(button.dataset.id), true);
});
list.addEventListener("keydown", (event) => {
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
  const buttons = [...list.querySelectorAll("button[data-id]")];
  const index = buttons.indexOf(document.activeElement);
  if (index < 0) return;
  event.preventDefault();
  const next = buttons[Math.min(buttons.length - 1, Math.max(0, index + (event.key === "ArrowDown" ? 1 : -1)))];
  next.focus();
  select(Number(next.dataset.id), false);
});
document.querySelector("[data-refresh]").addEventListener("click", () => {
  newButton.hidden = true;
  loadStats();
  loadList(true);
});
newButton.addEventListener("click", () => {
  newButton.hidden = true;
  loadStats();
  loadList(true);
});

let resizeTimer = 0;
window.addEventListener("resize", () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => state.days && drawChart(state.days), 150);
});

initHeader();
loadStats();
await loadList(true);
const initialId = Number(new URLSearchParams(location.search).get("id"));
if (Number.isSafeInteger(initialId) && initialId > 0) select(initialId, false);
setInterval(checkNew, 60000);
