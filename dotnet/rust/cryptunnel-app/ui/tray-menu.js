// 自绘托盘菜单的前端逻辑。
// 弹出时机、位置、失焦收起都由 Rust 侧负责（lib.rs show_tray_menu / WindowEvent::Focused），
// 这里只做三件事：刷新第一项文案、转发菜单动作、Esc 收起。
import { invoke } from "./vendor/core.js";
import { listen } from "./vendor/event.js";

const toggleItem = document.getElementById("tm-toggle");

/** 按主面板当前可见状态刷新第一项文案（可见 → 隐藏主面板）。 */
async function syncToggleLabel() {
  try {
    const visible = await invoke("tray_menu_state");
    toggleItem.textContent = visible ? "隐藏主面板" : "显示主面板";
  } catch (e) {
    console.error("读取主面板状态失败：", e);
  }
}

// Rust 每次弹出菜单前会发 tray-menu-show；窗口获得焦点再兜一次（防首帧丢事件）。
listen("tray-menu-show", syncToggleLabel);
window.addEventListener("focus", syncToggleLabel);
syncToggleLabel();

// 菜单动作统一交给 Rust 执行（命令内部会先收起本窗口）
for (const item of document.querySelectorAll("[data-action]")) {
  item.addEventListener("click", () => {
    invoke("tray_menu_action", { action: item.dataset.action }).catch((e) =>
      console.error("托盘菜单动作失败：", e));
  });
}

// Esc 收起菜单（等价于系统菜单的行为）
window.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    invoke("tray_menu_action", { action: "hide" }).catch(() => {});
  }
});

// 菜单窗口内禁用右键系统菜单，避免叠出浏览器默认菜单
document.addEventListener("contextmenu", (e) => e.preventDefault());
