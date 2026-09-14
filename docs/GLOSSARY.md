# 术语表(Cryptunnel 隧道)

本表统一 `cryptunnel-client`(客户端)与 starter 服务端
`io.github.objectyan.cryptunnel` 的领域用语,避免歧义。

> 历史注：starter 化之前,服务端代码内嵌在 sunrise 项目的 `com.crm.sunrise.jdbcproxy` 包下;
> v1.2 抽成独立 Maven artifact 后迁到 `io.github.objectyan.cryptunnel`。

| 术语 | 定义 | 备注 |
|---|---|---|
| 隧道 (Tunnel) | DBeaver ↔ 本地代理 ↔ 远端 ↔ MySQL 之间的一条双向字节流通道 | 与具体传输(WS/HTTP)解耦 |
| 字节流透传 (Byte-stream passthrough) | 代理不解析 MySQL 协议,只按序原样转发字节 | MySQL 协议层负责重组 |
| 块 / 分片 (Chunk) | 一次 `read()` 读到的字节(≤ chunk 读缓冲),独立编码为一条消息 | 逐块流式,不累积整条结果集 |
| 帧 (Frame) | 一条 WebSocket 消息;当前为 `TextMessage`(Base64) | 与 chunk 一一对应 |
| 帧大小契约 (Frame-size contract) | 不变式 `base64(chunk)+开销 < 单帧上限` | 见 ADR-0001 |
| 单帧上限 (maxTextMessageBufferSize) | 接收端允许的单条 WS 消息最大字节数 | Tomcat 默认 8192,本项目显式设 256KB |
| 主模式 (WebSocket primary) | 首选传输:WebSocket 全双工隧道 | `/ws-cryptunnel` |
| 降级模式 (HTTP fallback) | WS 被拦截时的 HTTP 长轮询隧道 | 半双工,存在独立问题,本次未修复 |
| 加密认证 (Encrypted auth) | 首条消息为 AES 加密的 `AUTH:{authKey}:{ts}:{nonce}` | 防过期/重放,authKey 独立于 aesKey |
| CLOSE_TOO_BIG (1009) | WS 关闭码:收到的单条消息超过接收端单帧上限 | 本次故障的直接信号 |
| Base64 膨胀 | Base64 编码使字节数增大约 4/3(≈33%) | 二进制帧可消除,列为后续优化 |
