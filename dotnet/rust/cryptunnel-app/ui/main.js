// 前端与 Rust 后端桥接：经 Tauri invoke 调用命令，经 listen 接收隧道事件。
import { invoke } from "https://unpkg.com/@tauri-apps/api@2/core.js";
import { getCurrentWindow } from "https://unpkg.com/@tauri-apps/api@2/window.js";
import { listen } from "https://unpkg.com/@tauri-apps/api@2/event.js";
import { getVersion } from "https://unpkg.com/@tauri-apps/api@2/app.js";

const $ = (id) => document.getElementById(id);

// 内存态：项目列表（含运行状态），以 name 为键。
let projects = []; // [{name, display_name, enabled, server_url, local_port, cipher, running}]
let editingName = null; // 编辑器当前编辑的项目名（null = 新建）

// ---------- 视图切换 ----------
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

// ---------- 日志 ----------
const dashLog = $("dash-log");
const fullLog = $("full-log");
const logFilter = $("log-filter");

function appendLog(tunnel, kind, message) {
  const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  const tag = tunnel && tunnel !== "default" ? `[${tunnel}] ` : "";
  const html = `[${time}] ${tag}${message}`;
  for (const el of [dashLog, fullLog]) {
    const line = document.createElement("div");
    line.className = "log-line log-" + kind;
    line.dataset.tunnel = tunnel || "";
    line.textContent = html;
    if (el === fullLog && logFilter.value && tunnel !== logFilter.value) {
      line.style.display = "none";
    }
    el.appendChild(line);
    while (el.children.length > 800) el.removeChild(el.firstChild);
    el.scrollTop = el.scrollHeight;
  }
}

logFilter.addEventListener("change", () => {
  const v = logFilter.value;
  fullLog.querySelectorAll(".log-line").forEach((l) => {
    l.style.display = !v || l.dataset.tunnel === v ? "" : "none";
  });
});
$("btn-clearlog").addEventListener("click", () => { dashLog.innerHTML = ""; fullLog.innerHTML = ""; });

// ---------- 项目列表渲染 ----------
function renderProjects() {
  const wrap = $("project-list");
  wrap.querySelectorAll(".proj").forEach((n) => n.remove());
  $("empty-hint").style.display = projects.length ? "none" : "";

  // 更新日志过滤器选项
  const cur = logFilter.value;
  logFilter.innerHTML = '<option value="">全部项目</option>';
  for (const p of projects) {
    const o = document.createElement("option");
    o.value = p.name; o.textContent = p.display_name || p.name;
    logFilter.appendChild(o);
  }
  logFilter.value = cur;

  for (const p of projects) {
    const el = document.createElement("div");
    el.className = "proj" + (p.running ? " running" : "") + (p.enabled ? "" : " disabled");
    el.innerHTML = `
      <div class="proj-main">
        <div class="proj-title">
          <span class="proj-dot"></span>
          <strong>${esc(p.display_name || p.name)}</strong>
          ${p.enabled ? "" : '<span class="tag tag-off">已停用</span>'}
        </div>
        <div class="proj-sub">
          <code>localhost:${p.local_port}</code>
          <span>→</span><span class="proj-url">${esc(p.server_url)}</span>
          <span class="tag">${esc(p.cipher)}</span>
        </div>
      </div>
      <div class="proj-actions">
        <button class="p-toggle ${p.running ? "" : "primary"}">${p.running ? "停止" : "启动"}</button>
        <button class="p-edit">编辑</button>
      </div>`;
    el.querySelector(".p-toggle").addEventListener("click", () => toggleProject(p));
    el.querySelector(".p-edit").addEventListener("click", () => openEditor(p.name));
    wrap.appendChild(el);
  }
}

function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

// ---------- 加载项目列表 ----------
async function refreshProjects() {
  try {
    const r = await invoke("list_projects");
    projects = r.projects || [];
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

// ---------- 启停 ----------
async function toggleProject(p) {
  try {
    if (p.running) {
      appendLog(p.name, "info", await invoke("stop_project", { name: p.name }));
      p.running = false;
    } else {
      appendLog(p.name, "info", await invoke("start_project", { name: p.name }));
      p.running = true;
    }
  } catch (e) {
    appendLog(p.name, "error", String(e));
  }
  renderProjects();
}

$("btn-start-all").addEventListener("click", async () => {
  for (const p of projects) {
    if (p.enabled && !p.running) {
      try { await invoke("start_project", { name: p.name }); p.running = true; }
      catch (e) { appendLog(p.name, "error", String(e)); }
    }
  }
  renderProjects();
  appendLog("", "info", "已尝试启动所有已启用项目。");
});
$("btn-stop-all").addEventListener("click", async () => {
  try { await invoke("stop_all_projects"); } catch {}
  for (const p of projects) p.running = false;
  renderProjects();
  appendLog("", "info", "已停止所有运行中项目。");
});
$("btn-refresh").addEventListener("click", refreshProjects);

// ---------- 编辑器 ----------
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
  if (!confirm(`确定删除项目「${editingName}」？会先停止其隧道。`)) return;
  try {
    await invoke("delete_project", { name: editingName });
    appendLog(editingName, "info", "项目已删除。");
    editingName = null;
    await refreshProjects();
    showView("dashboard");
  } catch (e) {
    editorMsg("删除失败：" + e, true);
  }
});

// ---------- 设置 ----------
async function loadSettings() {
  try { $("autostart").checked = await invoke("get_autostart"); } catch {}
  try { $("config-dir").textContent = await invoke("get_config_dir"); } catch {}
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

// ---------- 更新 ----------
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

// ---------- 隧道事件 ----------
await listen("tunnel-event", (ev) => {
  const { tunnel, kind, message } = ev.payload;
  if (kind === "bytes") return; // 字节流太密不进日志
  appendLog(tunnel, kind, kind === "state" ? "状态：" + message : message);
  if (kind === "state") {
    const p = projects.find((x) => x.name === tunnel);
    if (p) {
      if (message === "已停止" || message === "错误") p.running = false;
      else if (message.includes("已连接") || message === "监听中") p.running = true;
      renderProjects();
    }
  }
});

// ---------- 系统 ----------
$("btn-hide").addEventListener("click", () => getCurrentWindow().hide());

// ---------- 启动 ----------
refreshProjects();
appendLog("", "info", "Cryptunnel 就绪。");
