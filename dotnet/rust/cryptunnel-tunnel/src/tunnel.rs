//! 隧道网络核心（对齐 .NET `Tunnel.cs`）。
//!
//! 全生命周期：监听本地端口 → accept → 每连接选路（WS 主 / HTTP 降级）→ 认证 → 双向泵 → 清理。
//!
//! 协议要点（与服务端 `CryptunnelWebSocketHandler` 对齐）：
//! - **一个 WS 连接 = 一个 MySQL 连接**（非多路复用），每个本地 TCP 连接开独立 WS。
//! - 首帧必须是**加密后**的认证报文，服务端对明文 `AUTH:` 前缀直接拒绝。
//! - 认证成功**不发任何 ACK**，直接开 MySQL 连接、由 reader 线程把握手包推过来。
//!   所以收到数据 = 认证已过，首帧（MySQL 握手包）必须回传给 DBeaver。
//! - 只用 **Text** 帧（服务端 `handleBinaryMessage` 仅 warn 丢弃）。
//! - 认证失败表现为服务端 close 帧，reason 文本经 [`crate::close_reason`] 映射为中文。
//!
//! 架构红线：隧道层只通过 [`TunnelEvent`] 回调上报状态与日志，绝不触碰任何 UI。

use std::sync::Arc;

use futures_util::{SinkExt, StreamExt};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::watch;
use tokio_tungstenite::{connect_async, tungstenite::Message, MaybeTlsStream, WebSocketStream};

use cryptunnel_crypto::TunnelCipher;

use crate::auth;
use crate::close_reason;
use crate::config::{HttpEndpoint, TunnelConfig};
use crate::error::TunnelError;
use crate::framing;

type WsStream = WebSocketStream<MaybeTlsStream<TcpStream>>;

/// 隧道状态（对齐 .NET `TunnelState`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TunnelState {
    Stopped,
    Listening,
    WsConnecting,
    WsConnected,
    HttpConnecting,
    HttpConnected,
    Error,
}

/// 数据方向（用于观测/统计）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    /// 服务端 → 本地（下行）。
    In,
    /// 本地 → 服务端（上行）。
    Out,
}

/// 隧道事件：状态变更、日志、字节流观测。隧道层只发事件，不碰 UI。
#[derive(Debug, Clone)]
pub enum TunnelEvent {
    State(TunnelState),
    Log(LogLevel, String),
    Bytes(Direction, usize),
    /// 致命错误（端口占用等无法启动）。
    Fatal(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LogLevel {
    Info,
    Warn,
    Error,
}

/// 事件回调。`Send + Sync`，便于跨线程（Tauri command 在独立线程触发）。
pub type EventSink = Arc<dyn Fn(TunnelEvent) + Send + Sync>;

/// 单条隧道（一个本地监听端口 → 一个服务端）。
pub struct Tunnel {
    cfg: TunnelConfig,
    sink: EventSink,
    shutdown: watch::Sender<bool>,
}

impl Tunnel {
    pub fn new(cfg: TunnelConfig, sink: EventSink) -> (Self, watch::Receiver<bool>) {
        let (tx, rx) = watch::channel(false);
        (
            Tunnel {
                cfg,
                sink,
                shutdown: tx,
            },
            rx,
        )
    }

    pub fn config(&self) -> &TunnelConfig {
        &self.cfg
    }

    /// 请求停止整个隧道（监听循环退出）。
    pub fn shutdown(&self) {
        let _ = self.shutdown.send(true);
    }

    fn emit(&self, e: TunnelEvent) {
        (self.sink)(e);
    }

    fn log(&self, level: LogLevel, msg: impl Into<String>) {
        self.emit(TunnelEvent::Log(level, msg.into()));
    }

    fn state(&self, s: TunnelState) {
        self.emit(TunnelEvent::State(s));
    }

    /// 运行监听循环（阻塞直到 shutdown 或致命错误）。在 tokio runtime 上调用。
    pub async fn run(&self, mut shutdown: watch::Receiver<bool>) -> Result<(), TunnelError> {
        // 启动闸门：非回环监听未被显式许可时拒绝启动（契约 §5：绝不"告警后继续"）。
        crate::addr_policy::validate(
            Some(&self.cfg.listen_address),
            self.cfg.allow_non_loopback,
            self.cfg.local_port,
        )
        .map_err(TunnelError::Listen)?;

        // 分片大小启动期校验：把帧超限从「传大结果集时随机断线」提前成「启动即报错」。
        framing::validate_chunk_size(self.cfg.chunk_size).map_err(TunnelError::Config)?;

        let cipher = self.cfg.resolve_cipher()?;
        let cipher: Arc<dyn TunnelCipher> = Arc::from(cipher);

        let listen_addr = format!("{}:{}", self.cfg.listen_address, self.cfg.local_port);
        let listener = match TcpListener::bind(&listen_addr).await {
            Ok(l) => l,
            Err(e) => {
                let msg = format!("监听 {listen_addr} 失败：{e}");
                self.log(LogLevel::Error, &msg);
                self.state(TunnelState::Error);
                self.emit(TunnelEvent::Fatal(msg.clone()));
                return Err(TunnelError::Listen(msg));
            }
        };

        self.log(
            LogLevel::Info,
            format!(
                "监听 {}，DBeaver 请连接 localhost:{}（算法 {}）",
                listen_addr,
                self.cfg.local_port,
                cipher.id()
            ),
        );
        self.state(TunnelState::Listening);

        loop {
            tokio::select! {
                _ = shutdown.changed() => {
                    if *shutdown.borrow() { break; }
                }
                accept = listener.accept() => {
                    match accept {
                        Ok((stream, peer)) => {
                            self.log(LogLevel::Info, format!("DBeaver 已连接（{peer}），建立隧道…"));
                            let cfg = self.cfg.clone();
                            let cipher = Arc::clone(&cipher);
                            let sink = Arc::clone(&self.sink);
                            tokio::spawn(async move {
                                handle_connection(stream, cfg, cipher, sink).await;
                            });
                        }
                        Err(e) => {
                            self.log(LogLevel::Error, format!("接受连接失败：{e}"));
                            break;
                        }
                    }
                }
            }
        }

        self.state(TunnelState::Stopped);
        Ok(())
    }
}

/// 处理单条本地连接：按模式选路，WS 优先、可降级 HTTP。
async fn handle_connection(
    mut client: TcpStream,
    cfg: TunnelConfig,
    cipher: Arc<dyn TunnelCipher>,
    sink: EventSink,
) {
    let emit = |e: TunnelEvent| (sink)(e);
    let log = |level: LogLevel, msg: String| emit(TunnelEvent::Log(level, msg));

    let mut ok = false;
    if cfg.mode != crate::config::TransportMode::Http {
        match try_websocket(&mut client, &cfg, &cipher, &sink).await {
            Ok(true) => ok = true,
            Ok(false) => {}
            Err(e) => log(LogLevel::Warn, format!("WebSocket 模式失败：{e}")),
        }
    }
    if !ok && cfg.mode != crate::config::TransportMode::WebSocket && cfg.allow_fallback {
        match try_http(&mut client, &cfg, &cipher, &sink).await {
            Ok(true) => ok = true,
            Ok(false) => {}
            Err(e) => log(LogLevel::Error, format!("HTTP 模式失败：{e}")),
        }
    }
    if !ok {
        log(LogLevel::Error, "隧道建立失败，请检查服务器地址与网络".to_string());
        emit(TunnelEvent::State(TunnelState::Error));
    } else {
        emit(TunnelEvent::State(TunnelState::Listening));
    }
    // client 在此 drop，本地连接关闭。
}

// ============================================================================
// WebSocket 主模式
// ============================================================================

async fn try_websocket(
    client: &mut TcpStream,
    cfg: &TunnelConfig,
    cipher: &Arc<dyn TunnelCipher>,
    sink: &EventSink,
) -> Result<bool, TunnelError> {
    let emit = |e: TunnelEvent| (sink)(e);
    let log = |level: LogLevel, msg: String| emit(TunnelEvent::Log(level, msg));

    let ws_url = cfg.websocket_url();
    emit(TunnelEvent::State(TunnelState::WsConnecting));

    // 连接（带超时）。失败可能被 WAF 拦截，返回 false 以便降级。
    let connect = connect_async(&ws_url);
    let ws = match tokio::time::timeout(cfg.ws_connect, connect).await {
        Ok(Ok((ws, _resp))) => ws,
        Ok(Err(e)) => {
            log(LogLevel::Warn, format!("WebSocket 连接失败（可能被 WAF 拦截）：{e}"));
            return Ok(false);
        }
        Err(_) => {
            log(LogLevel::Warn, format!("WebSocket 连接超时（{}ms）", cfg.ws_connect.as_millis()));
            return Ok(false);
        }
    };

    let (mut write, mut read) = ws.split();

    // 加密认证。首帧必须是密文——服务端对明文 "AUTH:" 前缀直接拒绝。
    let auth = auth::build_sealed(cipher.as_ref(), &cfg.auth_key, &cfg.aes_key, cfg.target_id.as_deref())?;
    if !send_frame(&mut write, &auth, "认证报文", cipher.id(), 0, sink).await {
        return Ok(false);
    }

    // 等待并判定认证结果。成功的信号是服务端推来的首个数据帧（MySQL 握手包），
    // 失败表现为 close 帧（reason 映射为中文）。不能 sleep 后查状态——那样会丢 reason。
    let first_frame = match await_auth_result(&mut read, cfg, cipher, sink).await? {
        AuthOutcome::Success(frame) => frame,
        AuthOutcome::Failed => return Ok(false),
    };

    log(LogLevel::Info, "WebSocket 加密认证成功".to_string());
    emit(TunnelEvent::State(TunnelState::WsConnected));

    // 首帧（MySQL 握手包）必须回传给 DBeaver，丢掉它 DBeaver 会一直等握手而超时。
    if !first_frame.is_empty() {
        emit(TunnelEvent::Bytes(Direction::In, first_frame.len()));
        if let Err(e) = client.write_all(&first_frame).await {
            log(LogLevel::Warn, format!("写入握手包失败：{e}"));
            return Ok(true);
        }
    }

    // 双向泵：WS→本地、本地→WS，任一结束即收尾。
    let (mut rd, mut wr) = tokio::io::split(client);
    let ws_to_local = pump_ws_to_local(&mut read, &mut wr, cipher, &cfg.aes_key, sink);
    let local_to_ws = pump_local_to_ws(&mut rd, &mut write, cfg, cipher, sink);
    tokio::select! {
        _ = ws_to_local => {}
        _ = local_to_ws => {}
    }
    // 优雅关闭 WS（忽略错误）。
    let _ = write.send(Message::Close(None)).await;
    Ok(true)
}

enum AuthOutcome {
    /// 认证成功，附带首个明文数据帧（MySQL 握手包）。
    Success(Vec<u8>),
    Failed,
}

/// 等待并判定认证结果（对齐 `AwaitAuthResultAsync`）。
///
/// 成功的信号是第一个数据帧：服务端认证通过后不发任何 ACK，而是直接打开 MySQL 连接、
/// 由 reader 线程把握手包推过来。这个首帧是 MySQL 协议的一部分，必须回传给调用方。
async fn await_auth_result(
    read: &mut futures_util::stream::SplitStream<WsStream>,
    cfg: &TunnelConfig,
    cipher: &Arc<dyn TunnelCipher>,
    sink: &EventSink,
) -> Result<AuthOutcome, TunnelError> {
    let emit = |e: TunnelEvent| (sink)(e);
    let log = |level: LogLevel, msg: String| emit(TunnelEvent::Log(level, msg));

    let wait = async {
        while let Some(item) = read.next().await {
            let msg = match item {
                Ok(m) => m,
                Err(e) => {
                    log(LogLevel::Error, format!("认证过程中连接异常：{e}"));
                    return AuthOutcome::Failed;
                }
            };
            match msg {
                Message::Close(frame) => {
                    let reason = frame.as_ref().map(|f| f.reason.as_str());
                    let failure = close_reason::map(reason);
                    log(LogLevel::Error, format!("认证失败：{}", failure.message));
                    if !failure.retryable {
                        log(LogLevel::Error, "该错误重连也不会成功，请先按上述提示修改配置。".to_string());
                    }
                    return AuthOutcome::Failed;
                }
                Message::Binary(_) => {
                    // 服务端只发 TextMessage。收到 Binary 说明对端不是预期的服务端，
                    // 可能是中间设备伪造响应。
                    log(LogLevel::Error, "认证阶段收到二进制帧，但服务端只使用文本帧。\
                        可能连接到了非预期的服务，或中间有设备改写了流量。".to_string());
                    return AuthOutcome::Failed;
                }
                Message::Text(text) => {
                    match cipher.open(text.as_str(), &cfg.aes_key) {
                        Ok(plain) => return AuthOutcome::Success(plain),
                        Err(e) => {
                            log(LogLevel::Error, format!(
                                "认证后首个数据帧解密失败：{e}。\
                                 请核对加密算法(cipher={})与密钥(aesKey)是否与服务端一致。",
                                cipher.id()
                            ));
                            return AuthOutcome::Failed;
                        }
                    }
                }
                // Ping/Pong/Pong 等控制帧忽略，继续等。
                _ => continue,
            }
        }
        // 流结束且没拿到数据帧也没拿到 close：对端静默断开。
        log(LogLevel::Error, "认证阶段连接被对端关闭，未收到握手包或关闭原因。".to_string());
        AuthOutcome::Failed
    };

    match tokio::time::timeout(cfg.auth_response, wait).await {
        Ok(outcome) => Ok(outcome),
        Err(_) => {
            log(LogLevel::Error, format!(
                "认证超时（{}ms 内未收到服务端响应）。服务端可能无法连接到目标数据库，或网络存在中间设备缓冲。",
                cfg.auth_response.as_millis()
            ));
            Ok(AuthOutcome::Failed)
        }
    }
}

/// WebSocket 发送的**唯一入口**。所有出站帧都必须经过这里（帧上限检查与发送在物理上不可分）。
///
/// 超限时不发送、不重试：这属于客户端 bug（chunkSize 配置校验本应在加载阶段拦住），
/// 发出去的结果是对端 WS 1009 CLOSE_TOO_BIG。
async fn send_frame(
    write: &mut futures_util::stream::SplitSink<WsStream, Message>,
    payload: &str,
    purpose: &str,
    cipher_id: &str,
    plaintext_length: usize,
    sink: &EventSink,
) -> bool {
    let log = |level: LogLevel, msg: String| (sink)(TunnelEvent::Log(level, msg));

    if let Err(e) = framing::ensure_within_frame_limit(payload, plaintext_length, cipher_id) {
        log(LogLevel::Error, format!("{purpose}超出帧上限，已中止发送：{e}"));
        return false;
    }
    match write.send(Message::Text(payload.into())).await {
        Ok(_) => true,
        Err(e) => {
            log(LogLevel::Warn, format!("发送{purpose}失败：{e}"));
            false
        }
    }
}

/// 下行泵：WS → 解密 → 本地（DBeaver）。
async fn pump_ws_to_local(
    read: &mut futures_util::stream::SplitStream<WsStream>,
    local: &mut tokio::io::WriteHalf<&mut TcpStream>,
    cipher: &Arc<dyn TunnelCipher>,
    aes_key: &str,
    sink: &EventSink,
) {
    let emit = |e: TunnelEvent| (sink)(e);
    let log = |level: LogLevel, msg: String| emit(TunnelEvent::Log(level, msg));

    while let Some(item) = read.next().await {
        let msg = match item {
            Ok(m) => m,
            Err(_) => break,
        };
        match msg {
            Message::Close(frame) => {
                let reason = frame.as_ref().map(|f| f.reason.as_str());
                let failure = close_reason::map(reason);
                let level = if failure.retryable { LogLevel::Warn } else { LogLevel::Error };
                log(level, format!("连接已关闭：{}", failure.message));
                break;
            }
            Message::Binary(_) => {
                log(LogLevel::Error, "收到二进制帧，但本协议只使用文本帧。\
                    连接可能被中间设备改写，已断开。".to_string());
                break;
            }
            Message::Text(text) => {
                let decrypted = match cipher.open(text.as_str(), aes_key) {
                    Ok(d) => d,
                    Err(e) => {
                        // 必须断开而不是 continue：MySQL 协议是有状态的字节流，
                        // 丢掉一帧之后所有后续包都会错位。断开让重连逻辑接手。
                        log(LogLevel::Error, format!("数据帧解密失败，已断开连接：{e}。\
                            继续传输会导致 MySQL 协议流错位。"));
                        break;
                    }
                };
                emit(TunnelEvent::Bytes(Direction::In, decrypted.len()));
                if local.write_all(&decrypted).await.is_err() {
                    break;
                }
            }
            _ => continue,
        }
    }
}

/// 上行泵：本地（DBeaver）→ 加密 → WS。
async fn pump_local_to_ws(
    local: &mut tokio::io::ReadHalf<&mut TcpStream>,
    write: &mut futures_util::stream::SplitSink<WsStream, Message>,
    cfg: &TunnelConfig,
    cipher: &Arc<dyn TunnelCipher>,
    sink: &EventSink,
) {
    let mut buf = vec![0u8; cfg.chunk_size];

    loop {
        let n = match local.read(&mut buf).await {
            Ok(0) => break, // EOF
            Ok(n) => n,
            Err(_) => break,
        };
        let chunk = &buf[..n];
        (sink)(TunnelEvent::Bytes(Direction::Out, n));

        let enc = cipher.seal(chunk, &cfg.aes_key);
        if !send_frame(write, &enc, "数据帧", cipher.id(), n, sink).await {
            break;
        }
    }
}

// ============================================================================
// HTTP 长轮询降级模式
// ============================================================================

async fn try_http(
    client: &mut TcpStream,
    cfg: &TunnelConfig,
    cipher: &Arc<dyn TunnelCipher>,
    sink: &EventSink,
) -> Result<bool, TunnelError> {
    let emit = |e: TunnelEvent| (sink)(e);
    let log = |level: LogLevel, msg: String| emit(TunnelEvent::Log(level, msg));

    emit(TunnelEvent::State(TunnelState::HttpConnecting));
    log(LogLevel::Info, "降级到 HTTP 长轮询模式：该通道为请求-响应模型，大结果集可能卡顿，\
        长时间空闲可能延迟感知服务端断开。建议排查 WebSocket 为何不可用。".to_string());

    let http = reqwest::Client::builder()
        .timeout(cfg.http_read)
        .build()
        .map_err(|e| TunnelError::Http(format!("构造 HTTP 客户端失败：{e}")))?;

    let auth = auth::build_sealed(cipher.as_ref(), &cfg.auth_key, &cfg.aes_key, cfg.target_id.as_deref())?;

    // /connect → "connId:encHandshake"
    let connect_resp = match post(&http, &cfg.http_endpoint(HttpEndpoint::Connect), &auth, None).await {
        Some(r) => r,
        None => {
            log(LogLevel::Error, "HTTP 连接建立失败（网络错误）".to_string());
            return Ok(false);
        }
    };
    if connect_resp.is_empty() || connect_resp.starts_with('4') {
        log(LogLevel::Error, format!("HTTP 连接建立失败：{connect_resp}"));
        return Ok(false);
    }
    let mut parts = connect_resp.splitn(2, ':');
    let conn_id = parts.next().unwrap_or("").to_string();
    let enc_handshake = parts.next().unwrap_or("").to_string();
    if conn_id.is_empty() || enc_handshake.is_empty() {
        log(LogLevel::Error, "HTTP 连接响应格式错误".to_string());
        return Ok(false);
    }

    let handshake = cipher
        .open(&enc_handshake, &cfg.aes_key)
        .map_err(|e| TunnelError::Crypto(format!("HTTP 握手包解密失败：{e}")))?;
    client
        .write_all(&handshake)
        .await
        .map_err(|e| TunnelError::Io(e))?;

    log(LogLevel::Info, format!("HTTP 握手包已发送，连接ID={conn_id}"));
    emit(TunnelEvent::State(TunnelState::HttpConnected));

    let mut buf = vec![0u8; 1 << 13];
    loop {
        let n = match client.read(&mut buf).await {
            Ok(0) => break,
            Ok(n) => n,
            Err(_) => break,
        };
        let chunk = &buf[..n];
        emit(TunnelEvent::Bytes(Direction::Out, n));

        let enc = cipher.seal(chunk, &cfg.aes_key);
        let tunnel_resp = match post(&http, &cfg.http_endpoint(HttpEndpoint::Tunnel), &enc, Some(&conn_id)).await {
            Some(r) => r,
            None => {
                log(LogLevel::Error, "HTTP 隧道传输失败（网络错误）".to_string());
                break;
            }
        };
        if tunnel_resp.is_empty() || tunnel_resp.starts_with('4') || tunnel_resp.starts_with('5') {
            log(LogLevel::Error, format!("HTTP 隧道传输失败：{tunnel_resp}"));
            break;
        }
        let decrypted = match cipher.open(&tunnel_resp, &cfg.aes_key) {
            Ok(d) => d,
            Err(e) => {
                log(LogLevel::Error, format!("HTTP 隧道响应解密失败：{e}"));
                break;
            }
        };
        emit(TunnelEvent::Bytes(Direction::In, decrypted.len()));
        if client.write_all(&decrypted).await.is_err() {
            break;
        }
    }

    let _ = post(&http, &cfg.http_endpoint(HttpEndpoint::Disconnect), &auth, Some(&conn_id)).await;
    Ok(true)
}

/// HTTP POST，返回响应体文本；失败/非 2xx 返回 None 或状态码字符串（对齐 .NET 语义：
/// 非成功状态码返回数字字符串，供上层用 starts_with('4'/'5') 判定）。
async fn post(
    http: &reqwest::Client,
    url: &str,
    body: &str,
    conn_id: Option<&str>,
) -> Option<String> {
    let mut req = http.post(url).body(body.to_string());
    if let Some(id) = conn_id {
        req = req.header("X-Conn-Id", id);
    }
    match req.send().await {
        Ok(resp) => {
            if resp.status().is_success() {
                resp.text().await.ok()
            } else {
                Some(resp.status().as_u16().to_string())
            }
        }
        Err(_) => None,
    }
}
