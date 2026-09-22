#!/usr/bin/env node
/**
 * =============================================================================
 * 编辑界面回填验证（Cryptunnel）
 * =============================================================================
 *
 * 静态契约检查（ui-contract-check.mjs）只能证明"命名一致"，不能证明"确实填上了"。
 * 本脚本从 `ui/main.js` 中抽出 `openEditor` 里 read_project 的回填回调**真实执行**，
 * 喂入按 Rust `ProjectFile`（camelCase 契约）构造的数据，再断言每个 UI 控件都拿到了值。
 *
 * 数据构造独立于前端实现（字段名取自 rust …/project_config.rs 的契约），
 * 因此前端一旦把 key 写错（读成 undefined），断言就会失败。
 *
 * ## 用法
 *
 *   node verify/ui-editor-prefill-check.mjs
 *
 * 退出码 0 = 回填完整；1 = 有控件没拿到值（打印差异表）。
 * =============================================================================
 */

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "..");
const MAIN_JS = path.join(repo, "dotnet/rust/cryptunnel-app/ui/main.js");

const mainJs = readFileSync(MAIN_JS, "utf8");

// ── 1. 从 main.js 抽出 read_project 的回填回调体并真实执行 ────────────────────
function extractPrefillBody(js) {
  const marker = 'invoke("read_project"';
  const i = js.indexOf(marker);
  if (i < 0) throw new Error("main.js 里找不到 read_project 调用（回填逻辑被重写了？）");
  const braceStart = js.indexOf("{", js.indexOf("=>", i));
  let depth = 0;
  for (let j = braceStart; j < js.length; j++) {
    const c = js[j];
    if (c === "{") depth++;
    else if (c === "}") {
      depth--;
      if (depth === 0) return js.slice(braceStart + 1, j);
    }
  }
  throw new Error("回填回调的花括号不匹配");
}

const body = extractPrefillBody(mainJs);

/** 极简 DOM stub：记录每个控件被写入的 value / checked。 */
const els = new Map();
const $ = (id) => {
  if (!els.has(id)) els.set(id, { value: "", checked: false, disabled: false, style: {} });
  return els.get(id);
};

// ── 2. 按 Rust ProjectFile 契约（camelCase）构造数据 ─────────────────────────
const pf = {
  schemaVersion: 1,
  name: "crm-prod",
  displayName: "CRM 生产库",
  enabled: true,
  serverUrl: "http://db.internal:8080",
  aesKey: "AES-KEY-1",
  authKey: "AUTH-KEY-1",
  cipher: "aes-256-gcm",
  targetId: "tgt-1",
  wsPath: "/ws-cryptunnel",
  httpBasePath: "/cryptunnel",
  local: { port: 13006, address: "127.0.0.1", allowNonLoopback: true },
  transport: { mode: "websocket", allowFallback: true },
  health: { enabled: true, intervalSec: 300 },
};

new Function("pf", "$", body)(pf, $);

// ── 3. 断言：控件 ← 契约字段 ────────────────────────────────────────────────
const EXPECT = [
  ["e-name", "value", "crm-prod", "ProjectFile.name"],
  ["e-display", "value", "CRM 生产库", "ProjectFile.displayName"],
  ["e-server", "value", "http://db.internal:8080", "ProjectFile.serverUrl"],
  ["e-aes", "value", "AES-KEY-1", "ProjectFile.aesKey"],
  ["e-auth", "value", "AUTH-KEY-1", "ProjectFile.authKey"],
  ["e-target", "value", "tgt-1", "ProjectFile.targetId"],
  ["e-wspath", "value", "/ws-cryptunnel", "ProjectFile.wsPath"],
  ["e-httpbase", "value", "/cryptunnel", "ProjectFile.httpBasePath"],
  ["e-cipher", "value", "aes-256-gcm", "ProjectFile.cipher"],
  ["e-port", "value", 13006, "ProjectFile.local.port"],
  ["e-address", "value", "127.0.0.1", "ProjectFile.local.address"],
  ["e-mode", "value", "websocket", "ProjectFile.transport.mode"],
  ["e-enabled", "checked", true, "ProjectFile.enabled"],
  ["e-allownlb", "checked", true, "ProjectFile.local.allowNonLoopback"],
  ["e-health", "checked", true, "ProjectFile.health.enabled"],
];

const bad = [];
for (const [id, prop, want, from] of EXPECT) {
  const got = $(id)[prop];
  const ok = prop === "checked" ? got === want : String(got) === String(want);
  if (!ok) bad.push(`  ✗ #${id}.${prop}  期望 ${JSON.stringify(want)}  实际 ${JSON.stringify(got)}   ← ${from}`);
}

if (bad.length === 0) {
  console.log(`✓ 编辑界面回填完整：${EXPECT.length}/${EXPECT.length} 个控件都拿到了 ProjectFile 的值。`);
  process.exit(0);
}

console.error(`✗ 编辑界面回填缺失：${bad.length}/${EXPECT.length} 个控件为空或取到默认值。`);
console.error("  （控件的 value 是默认值 = 回填代码读到了 undefined = 字段名与 Rust 契约不符）\n");
console.error(bad.join("\n"));
process.exit(1);
