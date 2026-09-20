# Rust + Tauri 客户端发布 README

> 适用：本仓库的 Rust + Tauri 客户端（`dotnet/rust/cryptunnel-app`）打 `v*` 标签出三平台安装包。
> 流水线：`.github/workflows/release-tauri.yml`（`tauri-action@v0`，Windows / macOS 通用 / Ubuntu 22.04）。
> 与 Java 服务端的 [central-publish.md](central-publish.md)（Maven Central）是两条独立发布线。

## 0. 一句话流程

配好更新签名 secret（§2，**一次性**）→ 对齐版本号（§3）→ 打 tag 推送（§4）→ 等草稿 Release 出来检查后发版（§5）。

## 1. 产物与更新机制

| 平台 | 产物（Release 资产） | 更新签名 |
|---|---|---|
| Windows | `Cryptunnel_<ver>_x64-setup.exe` / `.msi` | `.sig` |
| macOS | `Cryptunnel_<ver>_universal.dmg` / `.app.tar.gz` | `.sig` |
| Linux | `Cryptunnel_<ver>_amd64.AppImage` / `.deb` | `.sig` |
| 全平台 | `latest.json` | — |

`latest.json` 是客户端 `tauri-plugin-updater` 的更新清单：客户端「设置 → 更新 → 检查更新」会拉
`https://github.com/objectyan/cryptunnel/releases/latest/download/latest.json`，比对版本、下载对应平台包、
用 `tauri.conf.json` 里内嵌的公钥校验 `.sig` 后安装并自动重启。

**`releaseDraft: true`**：每次出的是**草稿** Release，不会立刻对用户可见，必须在 GitHub 网页手动 Publish。
但注意：`latest.json` 在草稿里就已存在，`releases/latest/download/...` 链接只对**已发布**的 Release 生效——
所以客户端检查更新永远拿到的是上一个**已发布**版本，草稿不影响线上。

## 2. 更新签名密钥（一次性配置，必做）

不配的后果：`createUpdaterArtifacts: true` 要求签名，CI 打包阶段直接失败。

### 2.1 密钥对（已生成，勿重复生成）

- 私钥：本机 `~/.tauri/cryptunnel-updater.key`（Windows 即 `C:\Users\OY\.tauri\cryptunnel-updater.key`）
- 公钥：已内嵌 `dotnet/rust/cryptunnel-app/src-tauri/tauri.conf.json` 的 `plugins.updater.pubkey`
- **当前密钥无密码**（2026-09-20 重新生成；旧密钥密码不可考已作废）。
  ⚠ rsign 私钥头部固定写着 `encrypted secret key`——那是格式字段名，**不代表设了密码**，别被误导。

如需重新生成（换了机器 / 私钥丢失）：

```bash
cargo tauri signer generate -w ~/.tauri/cryptunnel-updater.key -p "" --ci --force
# 然后把新 .key.pub 内容替换进 tauri.conf.json 的 plugins.updater.pubkey 并提交
```

⚠ 换公钥 = 老版本客户端将无法校验新版本签名（更新会失败），只有没发布过或接受全员重装时才换。

### 2.2 存 GitHub Secrets（每次换密钥后做一次）

1. 打开 `https://github.com/objectyan/cryptunnel` → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**
2. Name：`TAURI_SIGNING_PRIVATE_KEY`
3. Secret：用记事本打开 `~/.tauri/cryptunnel-updater.key`，**整段复制**（单行 base64）粘贴，保存
4. 当前密钥无密码 → **不需要**建 `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`（workflow 里引用了它，secret 不存在时展开为空串，正好匹配无密码密钥）

## 3. 发版前：对齐版本号

tag 里的数字必须和两个文件一致（tauri-action 以 tag 为准，但安装包元数据来自配置文件，不一致会导致更新器版本判断错乱）：

- `dotnet/rust/cryptunnel-app/src-tauri/tauri.conf.json` 的 `version`
- `dotnet/rust/cryptunnel-app/src-tauri/Cargo.toml` 的 `version`

## 4. 打 tag 触发

```bash
git tag v0.2.0
git push origin v0.2.0
```

推送后到仓库 **Actions** 页看 `Release Tauri` 工作流：三个平台 job 并行（macOS 通用包最慢，约 15–25 分钟）。
任一平台失败不影响其他平台已传的资产，但 `latest.json` 由最后完成的 job 写出，**有平台失败时先别 Publish**。

## 5. 发布草稿

1. 仓库 **Releases** 页找到草稿 `Cryptunnel vX.Y.Z`，核对 9~10 个资产齐全（三平台包 + 各自 `.sig` + `latest.json`）
2. 编辑写更新说明（`latest.json` 的 `notes` 字段来自 Release 正文，客户端「检查更新」会展示）
3. **Publish release** → 客户端下一次检查更新即可发现新版本

## 6. 常见坑

- **CI 报签名失败 / `createUpdaterArtifacts` 错**：九成是 secret 没配或私钥内容复制时多了换行/空格。重新整段复制。
- **客户端检查更新一直"已是最新"**：线上最新 **已发布** Release 里没有 `latest.json`（比如上次发布失败只传了部分资产）。重发或补传。
- **改了 Rust 代码但忘了升版本号**：打同名 tag 重复发布会被 GitHub 拒绝（已发布 Release 的资产不可覆盖），只能删 Release + tag 重来，或升版本。
- **tag 前缀**：`v*` 专属于 Tauri 客户端。Java 服务端发 Maven Central 不走 tag（见 central-publish.md），不会冲突。
