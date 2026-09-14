# 配置文件 Schema（schemaVersion: 1）

当 `schemaVersion` 与程序支持的值不一致时，程序**拒绝加载**该文件并在界面上报错，不做猜测式兼容。

## 目录与模式

| 模式 | 触发条件 | 配置目录 | 日志目录 |
|---|---|---|---|
| 便携 | exe 同目录存在 `portable.txt` | `<exe目录>\config.d\` | `<exe目录>\logs\` |
| 安装 | 无 `portable.txt` | `%APPDATA%\Cryptunnel\config.d\` | `%LOCALAPPDATA%\Cryptunnel\logs\` |

两者都可被 `--config <dir>` 覆盖（最高优先级）。

`%APPDATA%` 只放配置和小体积统计（会随域账号漫游，换电脑跟着走）；日志放 `%LOCALAPPDATA%`（不同步，避免大文件污染漫游 profile）。

## 文件命名与合并

- `config.d\*.yaml` 全部加载，按文件名升序
- `00-defaults.yaml` 里的 `defaults:` 段是公共基线，其余文件的字段按**深度合并**覆盖它
- 只有 `defaults:` 段的文件不产生项目；有 `name` 的文件产生一个项目

## defaults 文件（`00-defaults.yaml`）

```yaml
schemaVersion: 1

defaults:
  local:
    address: 127.0.0.1       # 非回环地址需配 allowNonLoopback: true，否则拒绝启动
    # allowNonLoopback: false  # 允许 0.0.0.0 等非回环监听（默认 false，详见下文「监听地址」）

  wsPath: /ws-cryptunnel

  transport:
    mode: auto               # auto | websocket | http
    allowFallback: true      # auto 模式下 WS 失败是否降级 HTTP

  timeouts:
    wsConnectMs: 10000       # WebSocket 连接超时
    httpConnectMs: 10000     # HTTP 建连超时
    httpReadMs: 30000        # HTTP 读取超时
    authResponseMs: 3000     # 认证响应等待（原实现写死 sleep 500ms，网络慢时误判失败）
    reconnectDelayMs: 3000   # 断线重连间隔

  reconnect:
    enabled: true
    maxAttempts: 0           # 0 = 无限

  logging:
    level: info              # debug | info | warn | error
    maxFileSizeMb: 10
    retainDays: 7

  chunkSize: 4096            # ADR-0001 帧大小契约，见下方警告
```

> **`chunkSize` 不要随便改。** ADR-0001 的帧大小契约要求 `base64(chunk) + 开销 < 服务端单帧上限`。4096 经 AES+Base64 后约 5.5KB，稳稳低于 Tomcat 默认 8192。改大可能触发 `1009 CLOSE_TOO_BIG`。

## 项目文件（如 `crm.yaml`）

```yaml
schemaVersion: 1

name: crm                    # 必填，唯一，[a-z0-9-]+；用作日志前缀与界面标识
displayName: CRM 生产库       # 可选，界面显示名
enabled: true                # 可选，默认 true；false 则不启动

serverUrl: https://crm.example.com:8081   # 必填，http/https，末尾不带斜杠
aesKey: "..."                # 必填，非空
authKey: "..."               # 必填，非空

local:
  port: 13306                # 必填，1024-65535，且在项目间唯一

# 以下段落全部可选，深度覆盖 defaults 的同名字段
# local:
#   address: 127.0.0.1
# wsPath: /ws-cryptunnel
# transport: { mode: auto, allowFallback: true }
# timeouts: { wsConnectMs: 20000 }
# reconnect: { enabled: true, maxAttempts: 0 }
# logging: { level: debug }
```

## 校验规则

| 字段 | 规则 | 失败行为 |
|---|---|---|
| `schemaVersion` | 必须等于 1 | 该文件不加载，界面报错 |
| `name` | 必填、`[a-z0-9-]+`、跨文件唯一 | 该文件不加载，界面报错 |
| `serverUrl` | 必填、http/https 开头、末尾无斜杠 | 该文件不加载，界面报错 |
| `aesKey` / `authKey` | 必填、非空 | 该文件不加载，界面报错 |
| `local.port` | 必填、1024–65535、跨项目唯一 | 该项目不启动，其余照常 |
| `local.address` | 合法 IP | 回落 defaults |
| `local.address` 非回环 + 未授权 | 需 `local.allowNonLoopback: true` | **该项目不启动**，界面列出原因与改法 |
| `local.allowNonLoopback` | 布尔，默认 `false` | 回落 defaults，再回落 `false` |

端口冲突、URL 非法这类**单项目故障会被隔离**：出问题的项目跳过并告警，其余项目正常服务，不会因为一个项目挂掉导致全部不可用。

## 监听地址（`local.address` / `local.allowNonLoopback`）

默认 `127.0.0.1`，只有本机能连，这是绝大多数人该用的值 —— 用 DBeaver 连本机不需要改动这两项。

若把 `address` 改成 `0.0.0.0`、`::` 或某块网卡的 IP，就必须同时写 `allowNonLoopback: true`，
否则该项目在**配置加载阶段**就被拒绝，不会启动。

**为什么要多这么一道手续。** 监听非回环地址意味着局域网内任意机器都能连上本机的 `local.port`，
并经由本隧道访问内网数据库。关键在于：连过来的人**不需要知道 `aesKey`，也不需要知道 `authKey`** ——
本客户端会用你配置的密钥替对方完成认证。这是本客户端唯一一个能让本机之外的人穿透防火墙的配置项，
所以填地址与承担风险被拆成两件事，后果必须被显式承认一次。

`127.0.0.0/8` 整段都算回环（`127.0.0.5` 同样只有本机可达），无需授权。
`0.0.0.0` / `::` / `*` / `+` / `[::]` 一律视为非回环。

开启后每次启动仍会打印一条风险提示 —— 开关是一次性动作，风险是持续存在的。
真要跨机共享时，请同时用系统防火墙限定允许连接的来源 IP。

### 从旧版本升级

旧版本对非回环监听只告警不拦截。若你的配置里写了 `0.0.0.0` 或具体网卡 IP，
升级后该项目会启动失败，在对应项目的 `local:` 段补一行即可：

```yaml
local:
  port: 3307
  address: 0.0.0.0
  allowNonLoopback: true    # 新增这一行
```

## 密钥安全

`aesKey` / `authKey` 是明文写在文件里的。配置目录按上面的规则放在用户目录或便携目录，**不要把 `config.d\` 提交到公共仓库**。
