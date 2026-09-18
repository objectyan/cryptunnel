// 前端与 Rust 后端桥接：经 Tauri invoke 调用 #[tauri::command]
import { invoke } from "https://unpkg.com/@tauri-apps/api@2/core.js";
import { getCurrentWindow } from "https://unpkg.com/@tauri-apps/api@2/window.js";

const out = document.getElementById("output");
const badge = document.getElementById("conn-badge");

function print(msg, kind = "") {
  out.textContent = msg;
  out.className = "output " + kind;
}

async function doGreet() {
  try {
    const r = await invoke("greet", { name: "Cryptunnel" });
    print(r, "ok");
    setBadge(true);
  } catch (e) {
    print("greet 调用失败：\n" + e, "err");
    setBadge(false);
  }
}

async function doCrypto() {
  try {
    const r = await invoke("crypto_self_check");
    print(r, "ok");
    setBadge(true);
  } catch (e) {
    print("加密自检失败：\n" + e, "err");
    setBadge(false);
  }
}

async function doHide() {
  await getCurrentWindow().hide();
}

function setBadge(ok) {
  if (ok) {
    badge.textContent = "后端已通";
    badge.className = "badge badge-ok";
  } else {
    badge.textContent = "调用失败";
    badge.className = "badge badge-idle";
  }
}

async function loadAutostart() {
  try {
    const enabled = await invoke("get_autostart");
    document.getElementById("autostart").checked = enabled;
    document.getElementById("autostart-state").textContent =
      enabled ? "当前已开启" : "当前已关闭";
  } catch (e) {
    document.getElementById("autostart-state").textContent = "读取失败：" + e;
  }
}

async function toggleAutostart(e) {
  try {
    const r = await invoke("set_autostart", { enabled: e.target.checked });
    document.getElementById("autostart-state").textContent = r;
  } catch (err) {
    document.getElementById("autostart-state").textContent = "设置失败：" + err;
    e.target.checked = !e.target.checked; // 回滚
  }
}

document.getElementById("btn-greet").addEventListener("click", doGreet);
document.getElementById("btn-crypto").addEventListener("click", doCrypto);
document.getElementById("btn-hide").addEventListener("click", doHide);
document.getElementById("autostart").addEventListener("change", toggleAutostart);

loadAutostart();
