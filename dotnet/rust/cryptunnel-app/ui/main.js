// 前端与 Rust 后端桥接：经 Tauri invoke 调用命令，经 listen 接收隧道事件。
import { invoke } from "https://unpkg.com/@tauri-apps/api@2/core.js";
import { getCurrentWindow } from "https://unpkg.com/@tauri-apps/api@2/window.js";
import { listen } from "https://unpkg.com/@tauri-apps/api@2/event.js";
import { getVersion } from "https://unpkg.com/@tauri-apps/api@2/app.js";

const $ = (id) => document.getElementById(id);

// ============================================================================
// 内存态
// ============================================================================
// projects: [{name, display_name, enabled, server_url, local_port, cipher,
//            running, state, connections, bytesIn, bytesOut, alert}]
// state 取值与后端 state_text 对齐：监听中 / WS 连接中 / WS 已连接 /
// HTTP 连接中 / HTTP 已连接 / 错误 / 已停止
let projects = [];
let editingName = null; // 编辑器当前编辑的项目名（null = 新建）

// ============================================================================
// 视图切换
// ============================================================================
function showView(name) {
  document.querySelectorAll(".view").forEach((v) => v.classList.remove("active"));
  document.querySelectorAll(".nav-btn").forEach((b) => b.classList.remove("active"));
  $("view-" + name).classList.add("active");
  document.querySelector(`.nav-btn[data-view="${name}"]`).classList.add("active");
  if (name === "settings") loadSettings();
}
document.querySelectorAll(".nav-btn").forEach((b) =>
  b.addEventListener("click", () => showView(b.dataset.view))
);

// ============================================================================
// 日志：单一日志源（ring buffer）+ 两个面板（仪表盘/全屏）各自过滤渲染
// ============================================================================
const LOG_LEVELS = ["info", "warn", "error"]; // state 归入 info
const LOG_CAP = 1500;
let logSeq = 0;
const logEntries = []; // {seq, time, tunnel, level, text}

const LOG_PANELS = [
  {
    el: () => $("dash-log"),
    project: () => $("log-filter").value,
    level: () => $("dash-log-level").value,
    search: () => $("dash-log-search").value,
    autoscroll: () => $("dash-log-autoscroll").checked,
  },
  {
    el: () => $("full-log"),
    project: () => "", // 全屏页不再按项目过滤（仪表盘已有）
    level: () => $("full-log-level").value,
    search: () => $("full-log-search").value,
    autoscroll: () => $("full-log-autoscroll").checked,
  },
];

function levelOfKind(kind) {
  if (kind === "warn") return "warn";
  if (kind === "error" || kind === "fatal") return "error";
  return "info"; // info / state / 其他
}

function compileSearch(pattern) {
  if (!pattern) return null;
  try {
    return new RegExp(pattern, "i");
  } catch {
    // 非法正则不报错、不当过滤器——按普通子串匹配（对用户输入最宽容）。
    const lower = pattern.toLowerCase();
    return { test: (s) => s.toLowerCase().includes(lower) };
  }
}

function entryVisible(e, panel) {
  const proj = panel.project();
  if (proj && e.tunnel !== proj) return false;
  const lv = panel.level();
  if (lv && e.level !== lv) return false;
  const re = compileSearch(panel.search());
  if (re && !re.test(e.text)) return false;
  return true;
}

function renderPanel(panel) {
  const el = panel.el();
  el.innerHTML = "";
  const frag = document.createDocumentFragment();
  for (const e of logEntries) {
    if (!entryVisible(e, panel)) continue;
    const line = document.createElement("div");
    line.className = "log-line log-" + (e.level === "info" && e.state ? "state" : e.level);
    line.textContent = `[${e.time}] ${e.tunnel ? `[${e.tunnel}] ` : ""}${e.text}`;
    frag.appendChild(line);
  }
  el.appendChild(frag);
  if (panel.autoscroll()) el.scrollTop = el.scrollHeight;
}

function renderAllPanels() {
  for (const p of LOG_PANELS) renderPanel(p);
}

function appendLog(tunnel, kind, message) {
  const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  logEntries.push({
    seq: logSeq++,
    time,
    tunnel: tunnel && tunnel !== "default" ? tunnel : "",
    level: levelOfKind(kind),
    state: kind === "state",
    text: message,
  });
  while (logEntries.length > LOG_CAP) logEntries.shift();
  renderAllPanels();
}

// 过滤控件事件：任一变化重渲染两个面板。
for (const id of [
  "log-filter", "dash-log-level", "dash-log-search",
  "full-log-level", "full-log-search",
]) {
  $(id).addEventListener("input", renderAllPanels);
}
$("btn-clearlog").addEventListener("click", () => { logEntries.length = 0; renderAllPanels(); });
$("btn-clearlog-full").addEventListener("click", () => { logEntries.length = 0; renderAllPanels(); });

async function openLogDir() {
  try {
    await invoke("reveal_log_file");
  } catch (e) {
    appendLog("", "error", "打开日志目录失败：" + e);
  }
}
$("btn-open-logdir").addEventListener("click", openLogDir);
$("btn-open-logdir-2").addEventListener("click", openLogDir);

// ============================================================================
// 项目表格渲染 + 统计卡
// ============================================================================
function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function fmtBytes(n) {
  if (!n) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v : v.toFixed(1)) + " " + units[i];
}

function stateClass(p) {
  if (!p.enabled) return "st-off";
  if (p.state === "错误") return "st-err";
  if (p.state === "WS 已连接" || p.state === "HTTP 已连接") return "st-conn";
  if (p.state === "监听中" || p.state === "WS 连接中" || p.state === "HTTP 连接中") return "st-run";
  return "st-stop";
}

function stateText(p) {
  if (!p.enabled) return "已停用";
  return p.state || (p.running ? "监听中" : "已停止");
}

function renderProjects() {
  const tbody = $("project-rows");
  tbody.innerHTML = "";
  $("empty-hint").style.display = projects.length ? "none" : "";

  // 更新日志项目过滤器选项
  const logFilter = $("log-filter");
  const cur = logFilter.value;
  logFilter.innerHTML = '<option value="">全部项目</option>';
  for (const p of projects) {
    const o = document.createElement("option");
    o.value = p.name;
    o.textContent = p.display_name || p.name;
    logFilter.appendChild(o);
  }
  logFilter.value = cur;

  for (const p of projects) {
    const tr = document.createElement("tr");
    tr.className = stateClass(p);
    tr.innerHTML = `
      <td>
        <div class="p-name">${esc(p.display_name || p.name)}</div>
        <div class="p-sub">${esc(p.name)} · ${esc(p.cipher)}</div>
      </td>
      <td><code>${p.local_port}</code></td>
      <td class="p-url" title="${esc(p.server_url)}">${esc(p.server_url)}</td>
      <td><span class="badge ${stateClass(p)}">${stateText(p)}</span></td>
      <td class="num">${p.connections}</td>
      <td class="num">${fmtBytes(p.bytesIn)} / ${fmtBytes(p.bytesOut)}</td>
      <td class="p-alert">${p.alert ? esc(p.alert) : "—"}</td>
      <td class="col-actions">
        <button class="p-toggle ${p.running ? "" : "primary"}" ${p.enabled ? "" : "disabled"}>${p.running ? "停止" : "启动"}</button>
        <button class="p-health">检查</button>
        <button class="p-edit">编辑</button>
        <button class="p-del danger">删除</button>
      </td>`;
    tr.querySelector(".p-toggle").addEventListener("click", () => toggleProject(p));
    tr.querySelector(".p-health").addEventListener("click", () => healthCheck(p));
    tr.querySelector(".p-edit").addEventListener("click", () => openEditor(p.name));
    tr.querySelector(".p-del").addEventListener("click", () => deleteProject(p.name));
    tr.addEventListener("dblclick", (e) => {
      if (!e.target.closest("button")) openEditor(p.name);
    });
    tbody.appendChild(tr);
  }

  // 统计卡
  $("st-total").textContent = projects.length;
  $("st-running").textContent = projects.filter((p) => p.running).length;
  $("st-conn").textContent = projects.filter(
    (p) => p.state === "WS 已连接" || p.state === "HTTP 已连接"
  ).length;
  $("st-error").textContent = projects.filter((p) => p.state === "错误" || p.alert).length;
  $("st-down").textContent = fmtBytes(projects.reduce((a, p) => a + p.bytesIn, 0));
  $("st-up").textContent = fmtBytes(projects.reduce((a, p) => a + p.bytesOut, 0));
}

// ============================================================================
// 加载项目列表
// ============================================================================
async function refreshProjects() {
  try {
    const r = await invoke("list_projects");
    const fresh = r.projects || [];
    // 保留运行期累计字段（list_projects 不回传连接数/流量/告警）。
    for (const p of fresh) {
      const old = projects.find((x) => x.name === p.name);
      if (old) {
        p.state = old.state;
        p.connections = old.connections;
        p.bytesIn = old.bytesIn;
        p.bytesOut = old.bytesOut;
        p.alert = old.alert;
      }
    }
    projects = fresh;
    for (const p of projects) {
      p.state = p.state || (p.running ? "监听中" : "已停止");
      p.connections = p.connections || 0;
      p.bytesIn = p.bytesIn || 0;
      p.bytesOut = p.bytesOut || 0;
      p.alert = p.alert || "";
    }
    $("config-dir").textContent = r.config_dir || "";
    const errBox = $("cfg-errors");
    if (r.errors && r.errors.length) {
      errBox.style.display = "block";
      errBox.innerHTML = "<strong>配置错误（这些文件未被加载）：</strong>" +
        r.errors.map((e) => `<div class="cfg-err"><code>${esc(e.file)}</code> ${esc(e.message)}</div>`).join("");
    } else {
      errBox.style.display = "none";
    }
    renderProjects();
  } catch (e) {
    appendLog("", "error", "加载配置失败：" + e);
  }
}

// ============================================================================
// 启停 / 健康检查 / 删除
// ============================================================================
async function toggleProject(p) {
  try {
    if (p.running) {
      appendLog(p.name, "info", await invoke("stop_project", { name: p.name }));
      p.running = false;
      p.state = "已停止";
    } else {
      appendLog(p.name, "info", await invoke("start_project", { name: p.name }));
      p.running = true;
      p.state = "监听中";
      p.alert = "";
    }
  } catch (e) {
    appendLog(p.name, "error", String(e));
  }
  renderProjects();
}

async function healthCheck(p) {
  appendLog(p.name, "info", "开始健康检查（真实链路探活）…");
  try {
    const report = await invoke("health_check", { name: p.name });
    const lines = report.split("\n");
    // 结论行进日志；完整报告进弹窗。
    appendLog(p.name, lines[0].includes("异常") ? "warn" : "info", "健康检查 " + lines[0]);
    alert(`健康检查 — ${p.display_name || p.name}\n\n${report}`);
  } catch (e) {
    appendLog(p.name, "error", "健康检查失败：" + e);
  }
}

async function deleteProject(name) {
  if (!confirm(`确定删除项目「${name}」？会先停止其隧道。`)) return;
  try {
    await invoke("delete_project", { name });
    appendLog(name, "info", "项目已删除。");
    await refreshProjects();
  } catch (e) {
    appendLog(name, "error", "删除失败：" + e);
  }
}

$("btn-start-all").addEventListener("click", async () => {
  for (const p of projects) {
    if (p.enabled && !p.running) {
      try { await invoke("start_project", { name: p.name }); p.running = true; p.state = "监听中"; }
      catch (e) { appendLog(p.name, "error", String(e)); }
    }
  }
  renderProjects();
  appendLog("", "info", "已尝试启动所有已启用项目。");
});
$("btn-stop-all").addEventListener("click", async () => {
  try { await invoke("stop_all_projects"); } catch {}
  for (const p of projects) { p.running = false; p.state = "已停止"; p.connections = 0; }
  renderProjects();
  appendLog("", "info", "已停止所有运行中项目。");
});
$("btn-refresh").addEventListener("click", refreshProjects);

// ============================================================================
// 编辑器
// ============================================================================
function openEditor(name) {
  editingName = name;
  showView("editor");
  $("editor-title").textContent = name ? `编辑项目「${name}」` : "新建项目";
  $("e-delete").style.display = name ? "" : "none";
  $("e-msg").style.display = "none";
  if (!name) {
    for (const id of ["e-name","e-display","e-server","e-aes","e-auth","e-target","e-wspath","e-httpbase"])
      $(id).value = "";
    $("e-name").disabled = false;
    $("e-port").value = 3307; $("e-address").value = "127.0.0.1";
    $("e-cipher").value = "aes-256-cbc-hmac-sha256"; $("e-mode").value = "";
    $("e-enabled").checked = true; $("e-allownlb").checked = false; $("e-health").checked = false;
    return;
  }
  // 编辑：从磁盘回填原始配置（含密钥）
  invoke("read_project", { name }).then((pf) => {
    $("e-name").value = pf.name || name;
    $("e-name").disabled = true; // name 是文件名，不可改
    $("e-display").value = pf.display_name || "";
    $("e-server").value = pf.server_url || "";
    $("e-port").value = (pf.local && pf.local.port) || 3307;
    $("e-address").value = (pf.local && pf.local.address) || "127.0.0.1";
    $("e-aes").value = pf.aes_key || "";
    $("e-auth").value = pf.auth_key || "";
    $("e-target").value = pf.target_id || "";
    $("e-wspath").value = pf.ws_path || "";
    $("e-httpbase").value = pf.http_base_path || "";
    $("e-cipher").value = pf.cipher || "aes-256-cbc-hmac-sha256";
    $("e-mode").value = (pf.transport && pf.transport.mode) || "";
    $("e-enabled").checked = pf.enabled !== false;
    $("e-allownlb").checked = !!(pf.local && pf.local.allow_non_loopback);
    $("e-health").checked = !!(pf.health && pf.health.enabled);
  }).catch((e) => editorMsg("读取失败：" + e, true));
}

function editorMsg(text, isErr) {
  const m = $("e-msg");
  m.style.display = "block";
  m.textContent = text;
  m.className = "output " + (isErr ? "err" : "ok");
}

$("btn-new-project").addEventListener("click", () => openEditor(null));
$("e-cancel").addEventListener("click", () => showView("dashboard"));

$("e-save").addEventListener("click", async () => {
  const name = $("e-name").value.trim();
  const pf = {
    schema_version: 1,
    name,
    display_name: $("e-display").value.trim() || null,
    enabled: $("e-enabled").checked,
    server_url: $("e-server").value.trim(),
    aes_key: $("e-aes").value,
    auth_key: $("e-auth").value,
    cipher: $("e-cipher").value,
    target_id: $("e-target").value.trim() || null,
    ws_path: $("e-wspath").value.trim() || null,
    http_base_path: $("e-httpbase").value.trim() || null,
    local: {
      port: parseInt($("e-port").value, 10),
      address: $("e-address").value.trim() || null,
      allow_non_loopback: $("e-allownlb").checked,
    },
    transport: { mode: $("e-mode").value || null },
    health: { enabled: $("e-health").checked, interval_sec: null },
  };
  if (!name || !pf.server_url || !pf.aes_key || !pf.auth_key) {
    editorMsg("请填完整：name / serverUrl / aesKey / authKey", true);
    return;
  }
  try {
    editorMsg(await invoke("save_project", { pf }), false);
    editingName = name;
    $("e-name").disabled = true;
    $("e-delete").style.display = "";
    await refreshProjects();
  } catch (e) {
    editorMsg("保存失败：" + e, true);
  }
});

$("e-delete").addEventListener("click", async () => {
  if (!editingName) return;
  await deleteProject(editingName);
  editingName = null;
  showView("dashboard");
});

// ============================================================================
// 设置
// ============================================================================
async function loadSettings() {
  try { $("autostart").checked = await invoke("get_autostart"); } catch {}
  try { $("config-dir").textContent = await invoke("get_config_dir"); } catch {}
  try { $("log-dir").textContent = await invoke("get_log_dir"); } catch {}
}
$("autostart").addEventListener("change", async (e) => {
  try { await invoke("set_autostart", { enabled: e.target.checked }); }
  catch { e.target.checked = !e.target.checked; }
});
$("btn-crypto").addEventListener("click", async () => {
  const out = $("output");
  out.style.display = "block";
  try {
    out.textContent = await invoke("crypto_self_check");
    out.className = "output ok";
  } catch (e) {
    out.textContent = "加密自检失败：" + e;
    out.className = "output err";
  }
});

// ============================================================================
// 更新
// ============================================================================
function updateMsg(text, isErr) {
  const m = $("update-msg");
  m.style.display = "block";
  m.textContent = text;
  m.className = "output " + (isErr ? "err" : "ok");
}
$("btn-check-update").addEventListener("click", async () => {
  updateMsg("正在检查更新…", false);
  $("btn-do-update").style.display = "none";
  try {
    const r = await invoke("check_update");
    if (r.available) {
      updateMsg(`发现新版本 v${r.latest}（当前 v${r.current}）${r.notes ? "\n\n" + r.notes : ""}`, false);
      $("btn-do-update").style.display = "";
    } else {
      updateMsg(`已是最新版本（v${r.current}）。`, false);
    }
  } catch (e) {
    updateMsg(String(e), true);
  }
});
$("btn-do-update").addEventListener("click", async () => {
  updateMsg("正在下载并安装，完成后将自动重启…", false);
  $("btn-do-update").style.display = "none";
  try {
    updateMsg(await invoke("download_and_install_update"), false);
  } catch (e) {
    updateMsg(String(e), true);
  }
});
getVersion().then((v) => { $("cur-version").textContent = "v" + v; }).catch(() => {});

// ============================================================================
// 隧道事件
// ============================================================================
await listen("tunnel-event", (ev) => {
  const { tunnel, kind, message, dir, n } = ev.payload;

  if (kind === "bytes") {
    // 字节流不进日志，只做累计统计（对齐 WPF 老版 StatsCollector 语义）。
    const p = projects.find((x) => x.name === tunnel);
    if (p && typeof n === "number") {
      if (dir === "in") p.bytesIn += n;
      else if (dir === "out") p.bytesOut += n;
      throttledRender();
    }
    return;
  }

  appendLog(tunnel, kind, kind === "state" ? "状态：" + message : message);

  const p = projects.find((x) => x.name === tunnel);
  if (!p) return;

  if (kind === "state") {
    p.state = message;
    if (message === "已停止" || message === "错误") {
      p.running = false;
      if (message === "已停止") p.connections = 0;
      if (message === "错误") p.alert = "隧道错误";
    } else {
      p.running = true;
      if (message === "WS 已连接" || message === "HTTP 已连接") {
        p.connections++;
        p.alert = "";
      }
    }
    renderProjects();
  } else if (kind === "fatal") {
    p.alert = message;
    p.state = "错误";
    p.running = false;
    renderProjects();
  } else if (kind === "error") {
    p.alert = message;
    renderProjects();
  }
});

// bytes 事件每帧一条，全量重渲染太贵：限流到每 500ms 一次。
let renderPending = false;
function throttledRender() {
  if (renderPending) return;
  renderPending = true;
  setTimeout(() => {
    renderPending = false;
    renderProjects();
  }, 500);
}

// ============================================================================
// 系统
// ============================================================================
$("btn-hide").addEventListener("click", () => getCurrentWindow().hide());

// ============================================================================
// 启动
// ============================================================================
refreshProjects();
appendLog("", "info", "Cryptunnel 就绪。");
