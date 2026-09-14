# ADR-0001: WebSocket 隧道的帧大小契约

- 状态: 已接受
- 日期: 2026-07-24
- 相关组件: `cryptunnel-client`(客户端)、`sunrise` 的 `com.crm.sunrise.jdbcproxy`(服务端)

## 背景

DBeaver 通过本地代理连接远端 MySQL 时,连上约 1.2 秒即报
`08S01 Communications link failure`。

根因:隧道两端都以 8192 字节的缓冲读取字节流,每次 `read()` 的结果经
AES 加密 + Base64 编码后膨胀到约 11KB,并作为**一条** WebSocket
`TextMessage` 发送。而接收端(服务端 Tomcat)的
`maxTextMessageBufferSize` 默认仅 8192 字节,于是一旦单块数据超过约 6KB,
接收端就以 WebSocket 关闭码 `1009 (CLOSE_TOO_BIG)` 断开连接
(reason: "The decoded text message was too big for the output buffer..."),
隧道随即被拆毁,DBeaver 判定链路失败。

关键事实:隧道本就是**逐块流式透传**——每次 `read()` 独立成一条消息,
从不把整条结果集攒进内存。因此不存在"整条大消息导致 OOM"的天花板问题;
真正缺失的是"**每帧大小必须小于接收端单帧上限**"这一显式契约。

## 决策

1. 保持现有逐块流式透传架构。字节流原样透传,由 MySQL 协议层自行重组,
   两端每个方向均为单线程发送,天然有序,不引入序号/重组逻辑。
2. 传输格式暂留 `TextMessage` + Base64。二进制帧(去掉 Base64 的 33%
   膨胀)列为后续独立优化,本次不改动加密输出格式以降低风险。
3. 建立显式的**帧大小契约**并以常量固化:
   - 客户端 chunk 读缓冲 = 4096(经 AES+Base64 约 5.5KB)
   - 不变式: `base64(chunk) + 加密头开销(约 64B) < 接收端单帧上限`
   - **关键:把每帧压到 Tomcat 默认上限 8192 之下**,使正确性不依赖服务端
     调大缓冲即可成立(见下方实战修正)。
4. 服务端 `ServletServerContainerFactoryBean` 仅作为可选的额外余量,不作为
   正确性依赖(原因见实战修正)。
5. HTTP 长轮询降级模式本次不改动,其半双工逻辑存在独立问题,单列后续课题。

## 实战修正(2026-07-24)

首轮方案曾定 client chunk=32KB + 服务端 `maxTextMessageBufferSize`=256KB。
实测:服务端重新发布后**仍报 1009**。结论:

- 抛出该错的是 Tomcat **帧层**的 `maxTextMessageBufferSize`;而
  `ServletServerContainerFactoryBean` 设的是 JSR-356 容器默认值,对 Spring
  **handler 式**(`WebSocketHandlerRegistry` + `AbstractWebSocketHandler`)
  端点**未必被拾取**,故服务端有效上限很可能仍是 8192。
- 与其纠缠服务端容器内部,改为**客户端限帧**:chunk 降到 4096,单帧约
  5.5KB < 8192,连原装未改的服务端也能收,彻底摆脱对服务端配置的依赖。
- 观测到的 1009 为 client→server 方向(Tomcat 报"收到的太大"),故客户端
  限帧即可消除;server→client 方向经 org.java_websocket 客户端接收,默认
  无小上限,现状可用。

## 后果

- 正面: 修复即时报错;正确性完全在客户端可控、不依赖服务端配置;
  结果集大小无上限(流式逐块,不累积);改动面小、风险低。
- 负面/遗留: 单帧变小(4096)使消息条数增多,吞吐略降(JDBC 隧道场景可
  接受);仍保留 Base64 的 33% 带宽开销;HTTP 降级模式未修复。
- 后续可选优化: 若确认服务端能有效放大单帧上限(如改用 per-session
  原生 session 设置或 @ServerEndpoint 路径),可再上调 chunk 以提升吞吐。

## 备选方案(已否决)

- 仅把缓冲从 8192 降到 4096 让其"恰好"低于默认上限:靠魔数与 Base64
  膨胀比的巧合存活,升级或调参会静默复发,无显式护栏。因此否决。
