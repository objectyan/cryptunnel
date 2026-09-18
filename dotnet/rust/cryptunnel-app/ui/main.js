// 前端与 Rust 后端桥接：经 Tauri invoke 调用命令，经 listen 接收隧道事件。
import { invoke } from "https://unpkg.com/@tauri-apps/api@2/core.js";
import { getCurrentWindow } from "https://unpkg.com/@tauri-apps/api@2/window.js";
import { listen } from "https://unpkg.com/@tauri-apps/api@2/event.js";

const badge = document.getElementById("tunnel-badge");
const logEl = document.getElementById("log");
const btnStart = document.getElementById("btn-start");
const btnStop = document.getElementById("btn-stop");
const out = document.getElementById("output");

// ---------- 隧道状态徽标 ----------
function setBadge(state) {
  const map = {
    stopped: ["已停止", "badge badge-idle"],
    running: ["运行中", "badge badge-ok"],
  };
  const [text, cls] = map[state] || map.stopped;
  badge.textContent = text;
  badge.className = cls;
}

function setRunningUI(running) {
  btnStart.disabled = running;
  btnStop.disabled = !running;
  setBadge(running ? "running" : "stopped");
}

// ---------- 日志 ----------
function appendLog(kind, message) {
  const line = document.createElement("div");
  line.className = "log-line log-" + kind;
  const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  line.textContent = `[${time}] ${message}`;
  logEl.appendChild(line);
  logEl.scrollTop = logEl.scrollHeight;
  // 限制行数，避免长时间运行膨胀
  while (logEl.children.length > 500) logEl.removeChild(logEl.firstChild);
}

// ---------- 启动 / 停止 ----------
async function startTunnel() {
  const params = {
    server_url: document.getElementById("f-server").value.trim(),
    local_port: parseInt(document.getElementById("f-port").value, 10),
    aes_key: document.getElementById("f-aes").value,
    auth_key: document.getElementById("f-auth").value,
    cipher: document.getElementById("f-cipher").value,
    target_id: document.getElementById("f-target").value.trim() || null,
  };
  if (!params.server_url || !params.aes_key || !params.auth_key) {
    appendLog("error", "请填完整：服务端地址 / aesKey / authKey");
    return;
  }
  try {
    const r = await invoke("start_tunnel", { params });
    appendLog("info", r);
    setRunningUI(true);
  } catch (e) {
    appendLog("error", "启动失败：" + e);
  }
}

async function stopTunnel() {
  try {
    const r = await invoke("stop_tunnel");
    appendLog("info", r);
    setRunningUI(false);
  } catch (e) {
    appendLog("error", "停止失败：" + e);
  }
}

// ---------- 隧道事件（后端 emit） ----------
await listen("tunnel-event", (ev) => {
  const { kind, message } = ev.payload;
  if (kind === "state") {
    if (message === "已停止" || message === "错误") setRunningUI(false);
    else if (message.includes("已连接") || message === "监听中") setRunningUI(true);
    appendLog("state", "状态：" + message);
  } else if (kind === "bytes") {
    // 字节流太密，不进日志，仅可用于未来的速率统计
  } else {
    appendLog(kind, message);
  }
});

// ---------- 系统 ----------
async function loadAutostart() {
  try {
    const enabled = await invoke("get_autostart");
    document.getElementById("autostart").checked = enabled;
  } catch (e) { /* ignore */ }
}

async function toggleAutostart(e) {
  try {
    await invoke("set_autostart", { enabled: e.target.checked });
  } catch (err) {
    e.target.checked = !e.target.checked;
  }
}

async function doCrypto() {
  out.style.display = "block";
  try {
    out.textContent = await invoke("crypto_self_check");
    out.className = "output ok";
  } catch (e) {
    out.textContent = "加密自检失败：" + e;
    out.className = "output err";
  }
}

async function refreshStatus() {
  try {
    const s = await invoke("tunnel_status");
    setRunningUI(s === "running");
  } catch (e) { /* ignore */ }
}

// ---------- 事件绑定 ----------
btnStart.addEventListener("click", startTunnel);
btnStop.addEventListener("click", stopTunnel);
document.getElementById("btn-hide").addEventListener("click", () => getCurrentWindow().hide());
document.getElementById("btn-crypto").addEventListener("click", doCrypto);
document.getElementById("autostart").addEventListener("change", toggleAutostart);
document.getElementById("f-port").addEventListener("input", (e) => {
  document.getElementById("echo-port").textContent = e.target.value;
});

loadAutostart();
refreshStatus();
appendLog("info", "Cryptunnel 就绪。填好服务端与密钥后点「启动隧道」。");
