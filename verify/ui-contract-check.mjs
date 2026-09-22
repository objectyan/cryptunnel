#!/usr/bin/env node
/**
 * =============================================================================
 * UI ↔ Rust 字段契约检查（Cryptunnel）
 * =============================================================================
 *
 * ## 为什么需要它
 *
 * Rust 侧有两套结构体、两套命名，前端都从 `invoke()` 的返回值里读：
 *
 *   - `ProjectFile`     （cryptunnel-tunnel/src/project_config.rs）
 *     `#[serde(rename_all = "camelCase")]` → 线上 JSON 是 **camelCase**
 *     （`serverUrl` / `aesKey` / `wsPath` / `displayName` …），
 *     与 config.d 里的 YAML 落盘命名一致。
 *   - `ProjectSummary`  （cryptunnel-app/src-tauri/src/lib.rs）
 *     无 rename_all → 线上 JSON 是 **snake_case**
 *     （`server_url` / `display_name` / `local_port` / `config_dir`）。
 *
 * 两套命名搞混**不会报错**：serde 默认忽略未知字段、JS 取不存在的属性得到
 * `undefined`。后果是静默的——
 *   - 读错：编辑界面里服务端地址/密钥等字段**空白**（"数据不全"）；
 *   - 写错：`save_project` 反序列化时把这些 key **整段丢弃**，落盘配置缺字段。
 *
 * 本脚本按 `project_config.rs` 的真实定义推导 camelCase 线上名，再扫
 * `ui/main.js`，把"用 snake_case 访问 camelCase 字段"的地方揪出来。
 *
 * ## 用法
 *
 *   node verify/ui-contract-check.mjs
 *
 * 退出码 0 = 契约一致；1 = 存在命名错配（违规清单打印到 stderr）。
 * =============================================================================
 */

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "..");

const SRC = {
  projectConfig: path.join(repo, "dotnet/rust/cryptunnel-tunnel/src/project_config.rs"),
  mainJs: path.join(repo, "dotnet/rust/cryptunnel-app/ui/main.js"),
};

const toCamel = (s) => s.replace(/_([a-z0-9])/g, (_, c) => c.toUpperCase());
const toSnake = (s) => s.replace(/[A-Z]/g, (c) => "_" + c.toLowerCase());

/** 解析 `pub struct X { pub a: ..., pub b: ... }`，并记录其 serde rename_all。 */
function parseStructs(src) {
  const structs = new Map();
  const re = /pub struct (\w+)\s*\{([^}]*)\}/g;
  let m;
  while ((m = re.exec(src)) !== null) {
    const [, name, body] = m;
    const preceding = src.slice(Math.max(0, m.index - 400), m.index);
    const camel = /rename_all\s*=\s*"camelCase"/.test(preceding);
    const fields = [...body.matchAll(/pub\s+(\w+)\s*:/g)].map((x) => x[1]);
    structs.set(name, { camel, fields });
  }
  return structs;
}

/** 在文本里定位子串所在行号（1 起）。 */
function lineOf(text, needle) {
  const idx = text.indexOf(needle);
  if (idx < 0) return 0;
  return text.slice(0, idx).split("\n").length;
}

const projectConfigSrc = readFileSync(SRC.projectConfig, "utf8");
const mainJs = readFileSync(SRC.mainJs, "utf8");

const structs = parseStructs(projectConfigSrc);
const projectFile = structs.get("ProjectFile");
if (!projectFile) {
  console.error("✗ 未能从 project_config.rs 解析出 ProjectFile，脚本需同步更新。");
  process.exit(2);
}

// 收集"线上是 camelCase、源码是 snake_case"的字段：这些是唯一会因命名
// 写法不同而静默出错的字段（name/enabled/cipher/port 等同名的不在此列）。
/** @type {{src: string, wire: string, owner: string}[]} */
const snakeCaseTraps = [];
for (const [structName, def] of structs) {
  if (!def.camel) continue; // 非 camelCase 结构不构成陷阱
  for (const field of def.fields) {
    const wire = toCamel(field);
    if (wire === field) continue; // 两种命名同形，写不错
    snakeCaseTraps.push({ src: field, wire, owner: structName });
  }
}
// 去重（多个结构体可能有同名 snake 字段）
const seen = new Set();
const traps = snakeCaseTraps.filter((t) => {
  const k = `${t.src}→${t.wire}`;
  if (seen.has(k)) return false;
  seen.add(k);
  return true;
});

const violations = [];

// 检查 ①：读取 —— `pf.<snake>` 这类属性访问
for (const t of traps) {
  const needle = `pf.${t.src}`;
  if (mainJs.includes(needle)) {
    violations.push({
      kind: "读取",
      found: needle,
      expect: `pf.${t.wire}`,
      owner: t.owner,
      line: lineOf(mainJs, needle),
    });
  }
}

// 检查 ②：写入 —— 对象字面量里的 `<snake>:` 键（save_project 的 payload）
for (const t of traps) {
  const needle = `${t.src}:`;
  if (mainJs.includes(needle)) {
    violations.push({
      kind: "写入",
      found: needle,
      expect: `${t.wire}:`,
      owner: t.owner,
      line: lineOf(mainJs, needle),
    });
  }
}

// 检查 ③：嵌套读取 —— `pf.local.allow_non_loopback` 这类跨段访问。
// 正则要求 pf 与末尾字段之间至少有一级（+ 而非 *），避免与检查 ① 重复计。
for (const t of traps) {
  const re = new RegExp(`\\bpf(?:\\.[A-Za-z_$][\\w$]*)+\\.${t.src}\\b`);
  const m = re.exec(mainJs);
  if (!m) continue;
  violations.push({
    kind: "读取(嵌套)",
    found: m[0],
    expect: m[0].replace(/\.[^.]+$/, `.${t.wire}`),
    owner: t.owner,
    line: lineOf(mainJs, m[0]),
  });
}

if (violations.length === 0) {
  console.log(`✓ UI ↔ Rust 字段契约一致（扫描 ${traps.length} 个 camelCase 字段，无命名错配）。`);
  process.exit(0);
}

console.error(`✗ 发现 ${violations.length} 处字段命名错配（Rust 线上是 camelCase，前端用了 snake_case）：\n`);
for (const v of violations) {
  console.error(
    `  [${v.kind}] ui/main.js:${v.line}  ${v.found}  →  应为  ${v.expect}` +
      `   （${v.owner} 声明为 #[serde(rename_all = "camelCase")]）`,
  );
}
console.error(
  "\n这类错配不会抛错：读错得到 undefined（界面空白），写错被 serde 静默丢弃（落盘缺字段）。",
);
process.exit(1);
