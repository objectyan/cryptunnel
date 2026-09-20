// 前端与 Rust 后端桥接：经 Tauri invoke 调用命令，经 listen 接收隧道事件。
// 注意：API 包已本地化到 ./vendor（@tauri-apps/api@2.11.1）——
// 不能依赖 unpkg CDN：应用会部署在无外网/受限内网，CSP 也不放行远程脚本。
import { invoke } from "./vendor/core.js";
import { getCurrentWindow } from "./vendor/window.js";
import { listen } from "./vendor/event.js";
import { getVersion } from "./vendor/app.js";

const $ = (id) => document.getElementById(id);
const win = getCurrentWindow();

// ============================================================================
// 内存态
// ============================================================================
// projects: [{name, display_name, enabled, server_url, local_port, cipher,
//            running, state, transport, connections, bytesIn, bytesOut,
//            errors, warnings, healthText, healthOk}]
let projects = [];
let editingName = null;

// ============================================================================
// 标题栏按钮
// ============================================================================
$("tb-min").addEventListener("click", () => win.minimize());
$("tb-max").addEventListener("click", async () => {
  (await win.isMaximized()) ? win.unmaximize() : win.maximize();
});
// 关闭 = 隐藏到托盘（对齐 WPF 关窗不退出）
$("tb-close").addEventListener("click", () => win.hide());

// ============================================================================
// 日志（单一面板）
// ============================================================================
const LOG_CAP = 1500;
let logEntries = []; // {time, tunnel, level, state, text}

function levelOfKind(kind) {
  if (kind === "warn") return "warn";
  if (kind === "error" || kind === "fatal") return "error";
  return "info";
}
function compileSearch(pattern) {
  if (!pattern) return null;
  try { return new RegExp(pattern, "i"); }
  catch {
    const lower = pattern.toLowerCase();
    return { test: (s) => s.toLowerCase().includes(lower) };
  }
}
function renderLog() {
  const el = $("dash-log");
  const proj = $("log-filter").value;
  const lv = $("dash-log-level").value;
  const re = compileSearch($("dash-log-search").value);
  el.innerHTML = "";
  const frag = document.createDocumentFragment();
  for (const e of logEntries) {
    if (proj && e.tunnel !== proj) continue;
    if (lv && e.level !== lv) continue;
    if (re && !re.test(e.text)) continue;
    const line = document.createElement("div");
    line.className = "log-line log-" + (e.state ? "state" : e.level);
    line.textContent = `[${e.time}] ${e.tunnel ? `[${e.tunnel}] ` : ""}${e.text}`;
    frag.appendChild(line);
  }
  el.appendChild(frag);
  if ($("dash-log-autoscroll").checked) el.scrollTop = el.scrollHeight;
}
function appendLog(tunnel, kind, message) {
  const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  logEntries.push({
    time,
    tunnel: tunnel && tunnel !== "default" ? tunnel : "",
    level: levelOfKind(kind),
    state: kind === "state",
    text: message,
  });
  while (logEntries.length > LOG_CAP) logEntries.shift();
  renderLog();
}
for (const id of ["log-filter", "dash-log-level", "dash-log-search"])
  $(id).addEventListener("input", renderLog);
$("btn-clearlog").addEventListener("click", () => { logEntries = []; renderLog(); });
$("btn-log-end").addEventListener("click", () => {
  $("dash-log").scrollTop = $("dash-log").scrollHeight;
});

// ============================================================================
// 工具函数
// ============================================================================
function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function fmtBytes(n) {
  if (!n) return "0";
  const u = ["B", "KB", "MB", "GB", "TB"];
  let i = 0, v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v : v.toFixed(1)) + " " + u[i];
}
// 流量比例条宽度：对齐 .NET BytesToBarWidthConverter
// w = 64 * (1 - 1/(1+log10(b+1)))，夹在 [3, 64]
function barWidth(b) {
  if (!b || b <= 0) return 0;
  const w = 64 * (1 - 1 / (1 + Math.log10(b + 1)));
  return Math.max(3, Math.min(64, w));
}

// ============================================================================
// 项目表格渲染 + 总览卡
// ============================================================================
function stateInfo(p) {
  if (!p.enabled) return { cls: "st-disabled", text: "已停用", color: "var(--tx3)" };
  switch (p.state) {
    case "错误": return { cls: "st-error", text: "错误", color: "var(--danger)" };
    case "WS 已连接": case "HTTP 已连接":
      return { cls: "st-connected", text: "已连接", color: "var(--success)" };
    case "监听中": case "WS 连接中": case "HTTP 连接中":
      return { cls: "st-running", text: p.state, color: "var(--accent)" };
    default: return { cls: "st-stopped", text: "已停止", color: "var(--tx3)" };
  }
}
function transportOf(p) {
  if (p.state === "WS 已连接") return "WebSocket";
  if (p.state === "HTTP 已连接") return "HTTP 降级";
  return p.cipher || "";
}

function renderProjects() {
  const tbody = $("project-rows");
  tbody.innerHTML = "";
  $("empty-hint").style.display = projects.length ? "none" : "flex";

  // 项目过滤器选项
  const lf = $("log-filter");
  const cur = lf.value;
  lf.innerHTML = '<option value="">全部</option>';
  for (const p of projects) {
    const o = document.createElement("option");
    o.value = p.name; o.textContent = p.display_name || p.name;
    lf.appendChild(o);
  }
  lf.value = cur;

  for (const p of projects) {
    const st = stateInfo(p);
    const tr = document.createElement("tr");
    if (!p.enabled) tr.className = "row-disabled";
    const errCls = p.errors > 0 ? "al-err" : "al-zero";
    const warnCls = p.warnings > 0 ? "al-warn" : "al-zero";
    const healthRow = p.healthText
      ? `<div class="st-health" style="color:${p.healthOk ? "var(--success)" : "var(--danger)"}">${p.healthOk ? "✓" : "✗"} ${esc(p.healthText)}</div>`
      : "";
    tr.innerHTML = `
      <td><div class="p-name">${esc(p.name)}</div><div class="p-display">${esc(p.display_name || "")}</div></td>
      <td><span class="p-port">${p.local_port}</span></td>
      <td><div class="p-url" title="${esc(p.server_url)}">${esc(p.server_url)}</div></td>
      <td><div class="st-cell">
        <div class="st-row">
          <span class="st-dot-wrap">
            <span class="st-dot-glow" style="background:${st.color}"></span>
            <span class="st-dot" style="background:${st.color}"></span>
          </span>
          <span class="st-text" style="color:${st.color}">${st.text}</span>
        </div>
        <div class="st-transport" title="${esc(transportOf(p))}">${esc(transportOf(p))}</div>
        ${healthRow}
      </div></td>
      <td class="num">${p.connections}</td>
      <td><div class="tr-cell">
        <div class="tr-row tr-down"><span class="tr-arrow">↓</span><span class="tr-val">${fmtBytes(p.bytesIn)}</span><span class="tr-bar" style="width:${barWidth(p.bytesIn)}px"></span></div>
        <div class="tr-row tr-up"><span class="tr-arrow">↑</span><span class="tr-val">${fmtBytes(p.bytesOut)}</span><span class="tr-bar" style="width:${barWidth(p.bytesOut)}px"></span></div>
      </div></td>
      <td><div class="al-cell">
        <span class="al-badge ${errCls}">${p.errors}</span>
        <span class="al-badge ${warnCls}">${p.warnings}</span>
      </div></td>
      <td class="col-actions">
        <button class="p-toggle ${p.running ? "stop" : "start"}" ${p.enabled ? "" : "disabled"}>${p.running ? "停止" : "启动"}</button>
        <button class="p-health" title="健康检查：走真实链路验证网络、加密认证与数据库可达性">检查</button>
        <button class="p-del danger">删除</button>
      </td>`;
    tr.querySelector(".p-toggle").addEventListener("click", () => toggleProject(p));
    tr.querySelector(".p-health").addEventListener("click", () => healthCheck(p));
    tr.querySelector(".p-del").addEventListener("click", () => deleteProject(p.name));
    tr.addEventListener("dblclick", (e) => { if (!e.target.closest("button")) openEditor(p.name); });
    tbody.appendChild(tr);
  }

  // 总览卡
  const running = projects.filter((p) => p.running).length;
  const connected = projects.filter((p) => p.state === "WS 已连接" || p.state === "HTTP 已连接").length;
  const errors = projects.filter((p) => p.state === "错误" || p.errors > 0).length;
  $("st-total").textContent = projects.length;
  $("st-total-sub").textContent = `运行中 ${running}`;
  $("st-conn").textContent = connected;
  $("st-conn-sub").textContent = `共 ${projects.length} 项`;
  $("st-error").textContent = errors;
  const totalIn = projects.reduce((a, p) => a + p.bytesIn, 0);
  const totalOut = projects.reduce((a, p) => a + p.bytesOut, 0);
  // 对齐老版：白字单行「↓ x   ↑ y」，箭头不着色
  $("st-traffic").innerHTML =
    `<span class="t-down">↓ ${fmtBytes(totalIn)}</span>&nbsp;&nbsp;&nbsp;<span class="t-up">↑ ${fmtBytes(totalOut)}</span>`;
}

// ============================================================================
// 加载项目列表
// ============================================================================
async function refreshProjects() {
  try {
    const r = await invoke("list_projects");
    const fresh = r.projects || [];
    for (const p of fresh) {
      const old = projects.find((x) => x.name === p.name);
      if (old) Object.assign(p, {
        state: old.state, connections: old.connections,
        bytesIn: old.bytesIn, bytesOut: old.bytesOut,
        errors: old.errors, warnings: old.warnings,
        healthText: old.healthText, healthOk: old.healthOk,
      });
    }
    projects = fresh;
    for (const p of projects) {
      p.state = p.state || (p.running ? "监听中" : "已停止");
      p.connections = p.connections || 0;
      p.bytesIn = p.bytesIn || 0;
      p.bytesOut = p.bytesOut || 0;
      p.errors = p.errors || 0;
      p.warnings = p.warnings || 0;
    }
    $("config-dir").textContent = r.config_dir || "—";
    $("config-dir").title = r.config_dir || "";
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
      p.running = false; p.state = "已停止";
    } else {
      appendLog(p.name, "info", await invoke("start_project", { name: p.name }));
      p.running = true; p.state = "监听中"; p.errors = 0;
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
    const ok = !report.split("\n")[0].includes("异常");
    p.healthOk = ok;
    p.healthText = ok ? "链路健康" : "链路异常";
    appendLog(p.name, ok ? "info" : "warn", "健康检查 " + report.split("\n")[0]);
    renderProjects();
    $("health-title").textContent = `健康检查 — ${p.display_name || p.name}`;
    $("health-report").textContent = report;
    $("health-mask").style.display = "flex";
  } catch (e) {
    appendLog(p.name, "error", "健康检查失败：" + e);
  }
}
$("health-close").addEventListener("click", () => { $("health-mask").style.display = "none"; });

async function deleteProject(name) {
  if (!confirm(`确定删除项目「${name}」？会先停止其隧道。`)) return;
  try {
    await invoke("delete_project", { name });
    appendLog(name, "info", "项目已删除。");
    closeEditor();
    await refreshProjects();
  } catch (e) {
    appendLog(name, "error", "删除失败：" + e);
  }
}

$("btn-refresh").addEventListener("click", refreshProjects);

// 打开目录
async function reveal(cmd, label) {
  try { await invoke(cmd); }
  catch (e) { appendLog("", "error", `打开${label}失败：` + e); }
}
$("btn-open-logdir").addEventListener("click", () => reveal("reveal_log_file", "日志目录"));
$("btn-open-config").addEventListener("click", async () => {
  try {
    const dir = await invoke("get_config_dir");
    await invoke("reveal_config_dir").catch(() => appendLog("", "info", "配置目录：" + dir));
  } catch (e) { appendLog("", "error", "打开配置目录失败：" + e); }
});

// 退出（真退出，非隐藏）
$("btn-exit").addEventListener("click", async () => {
  if (!confirm("确定退出 Cryptunnel？所有隧道将停止。")) return;
  try { await invoke("stop_all_projects"); } catch {}
  win.close();
});

// ============================================================================
// 编辑器（模态对话框）
// ============================================================================
function openEditor(name) {
  editingName = name;
  $("editor-title").textContent = name ? `编辑项目「${name}」` : "新建项目";
  $("e-delete").style.display = name ? "" : "none";
  $("e-msg").style.display = "none";
  $("editor-mask").style.display = "flex";
  if (!name) {
    for (const id of ["e-name","e-display","e-server","e-aes","e-auth","e-target","e-wspath","e-httpbase"])
      $(id).value = "";
    $("e-name").disabled = false;
    $("e-port").value = 3307; $("e-address").value = "127.0.0.1";
    $("e-cipher").value = "aes-256-cbc-hmac-sha256"; $("e-mode").value = "";
    $("e-enabled").checked = true; $("e-allownlb").checked = false; $("e-health").checked = false;
    return;
  }
  invoke("read_project", { name }).then((pf) => {
    $("e-name").value = pf.name || name;
    $("e-name").disabled = true;
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
function closeEditor() { $("editor-mask").style.display = "none"; editingName = null; }
function editorMsg(text, isErr) {
  const m = $("e-msg");
  m.style.display = "block";
  m.textContent = text;
  m.className = "output " + (isErr ? "err" : "ok");
}
$("btn-new-project").addEventListener("click", () => openEditor(null));
$("btn-new-project-2").addEventListener("click", () => openEditor(null));
$("e-cancel").addEventListener("click", closeEditor);
$("e-cancel-2").addEventListener("click", closeEditor);
$("e-delete").addEventListener("click", () => { if (editingName) deleteProject(editingName); });

$("e-save").addEventListener("click", async () => {
  const name = $("e-name").value.trim();
  const pf = {
    schema_version: 1, name,
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
    await refreshProjects();
    setTimeout(closeEditor, 500);
  } catch (e) {
    editorMsg("保存失败：" + e, true);
  }
});

// ============================================================================
// 开机启动
// ============================================================================
async function loadAutostart() {
  try { $("autostart").checked = await invoke("get_autostart"); } catch {}
}
$("autostart").addEventListener("change", async (e) => {
  try { await invoke("set_autostart", { enabled: e.target.checked }); }
  catch { e.target.checked = !e.target.checked; }
});

// ============================================================================
// 模式显示
// ============================================================================
function updateModeText() {
  const n = projects.length;
  $("mode-text").textContent = n > 0 ? `多隧道（${n} 项）` : "未配置";
}

// ============================================================================
// 隧道事件
// ============================================================================
await listen("tunnel-event", (ev) => {
  const { tunnel, kind, message, dir, n } = ev.payload;
  if (kind === "bytes") {
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
      if (message === "错误") p.errors++;
    } else {
      p.running = true;
      if (message === "WS 已连接" || message === "HTTP 已连接") p.connections++;
    }
    renderProjects(); updateModeText();
  } else if (kind === "fatal") {
    p.state = "错误"; p.running = false; p.errors++;
    renderProjects(); updateModeText();
  } else if (kind === "error") {
    p.errors++; renderProjects();
  } else if (kind === "warn") {
    p.warnings++; renderProjects();
  }
});

// bytes 事件每帧一条，限流 500ms 聚合刷新。
let renderPending = false;
function throttledRender() {
  if (renderPending) return;
  renderPending = true;
  setTimeout(() => { renderPending = false; renderProjects(); }, 500);
}

// ============================================================================
// 拖拽导入 YAML
// ============================================================================
let dragDepth = 0;
window.addEventListener("dragenter", (e) => {
  e.preventDefault();
  dragDepth++;
  $("drop-overlay").style.display = "flex";
});
window.addEventListener("dragover", (e) => e.preventDefault());
window.addEventListener("dragleave", (e) => {
  e.preventDefault();
  if (--dragDepth <= 0) { dragDepth = 0; $("drop-overlay").style.display = "none"; }
});
window.addEventListener("drop", async (e) => {
  e.preventDefault();
  dragDepth = 0;
  $("drop-overlay").style.display = "none";
  const files = [...(e.dataTransfer?.files || [])].filter((f) => /\.ya?ml$/i.test(f.name));
  if (!files.length) { appendLog("", "warn", "未检测到 .yaml / .yml 文件。"); return; }
  for (const f of files) {
    try {
      const text = await f.text();
      const msg = await invoke("import_project_yaml", { filename: f.name, content: text });
      appendLog("", "info", msg);
    } catch (err) {
      appendLog("", "error", `导入 ${f.name} 失败：` + err);
    }
  }
  await refreshProjects();
});

// ============================================================================
// 启动
// ============================================================================
getVersion().then((v) => { $("cur-version").textContent = "v" + v; }).catch(() => {});
loadAutostart();
refreshProjects().then(updateModeText);
appendLog("", "info", "Cryptunnel 就绪。");
