//! 隧道健康检查（探活）（对齐 .NET `HealthProbe`）。
//!
//! **它验的是什么**：按真实隧道的同一条路径走一遍 ——
//! 连 WebSocket → 发加密认证报文 → 等服务端推来的 MySQL 握手包 → 解析版本 → 主动关闭。
//! 一次拿全三层结论：网络通不通、密钥/算法配得对不对、服务端能不能连上数据库。
//!
//! **为什么必须是真实链路而不是「连一下端口」**：连本地监听端口的探活，
//! authKey 配错、cipher 配错、服务端宕机、MySQL 挂掉这四种故障**全部报成功** ——
//! 一个在所有真实故障下都亮绿灯的指示灯，比没有指示灯更危险。
//!
//! **与 [`crate::tunnel::Tunnel`] 的关系**：本模块**不复用** Tunnel 实例，而是开一条
//! 独立的短连接 —— Tunnel 的生命周期绑在 DBeaver 连接上，借用会互相干扰。
//! 但认证报文的构造**必须复用** [`crate::auth::build_sealed`]：探活自己拼一套认证格式，
//! 它验过的就不是真实链路，而是探活自己那一套。
//!
//! **成本必须让调用方知道**：每次探活都会让服务端真实建立并断开一条 MySQL 连接，
//! 占用服务端 `maxConnections` 的一个名额。这就是周期性探活默认关闭的原因，
//! 也是本模块刻意只做「手动单次探活」、不内置调度器的原因。

use std::time::{Duration, Instant};

use futures_util::{SinkExt, StreamExt};
use serde::Serialize;
use tokio_tungstenite::tungstenite::Message;

use crate::auth;
use crate::close_reason;
use crate::config::TunnelConfig;

type WsStream = tokio_tungstenite::WebSocketStream<
    tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
>;

/// 探活层级。Skipped 不是「跳过不测」，是「上一层失败导致本层无法执行」——
/// 三态（Pass/Fail/Skipped）必须区分，Fail 与 Skipped 混淆会误导排查方向。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum HealthStatus {
    Pass,
    Fail,
    Skipped,
}

/// 单层结果：名称、结论、耗时（毫秒）、细节、（失败时的）排查建议。
#[derive(Debug, Clone, Serialize)]
pub struct HealthStage {
    pub name: String,
    pub status: HealthStatus,
    pub elapsed_ms: u64,
    pub detail: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub advice: Option<String>,
}

/// 完整探活报告。`healthy` 仅当所有执行过的层都 Pass 才为 true。
#[derive(Debug, Clone, Serialize)]
pub struct HealthReport {
    pub healthy: bool,
    pub stages: Vec<HealthStage>,
    pub total_ms: u64,
    /// 探到的 MySQL 版本号（未探到为 None）。
    pub mysql_version: Option<String>,
    /// 实际使用的算法标识（本地配置值，非机密）。
    pub cipher_id: String,
}

impl HealthReport {
    /// 渲染为面向用户的多行中文文本（UI 直接展示；与 .NET `HealthReportWindow` 同级信息）。
    pub fn to_display_text(&self) -> String {
        let mut out = String::new();
        out.push_str(if self.healthy {
            "结论：健康 ✓\n"
        } else {
            "结论：异常 ✗\n"
        });
        out.push_str(&format!(
            "算法：{}    总耗时：{}ms\n",
            self.cipher_id, self.total_ms
        ));
        if let Some(v) = &self.mysql_version {
            out.push_str(&format!("MySQL 版本：{v}\n"));
        }
        out.push('\n');
        for s in &self.stages {
            let mark = match s.status {
                HealthStatus::Pass => "✓",
                HealthStatus::Fail => "✗",
                HealthStatus::Skipped => "○",
            };
            let label = match s.status {
                HealthStatus::Pass => "通过",
                HealthStatus::Fail => "失败",
                HealthStatus::Skipped => "跳过",
            };
            out.push_str(&format!(
                "{mark} [{}] {}（{}ms）\n    {}\n",
                label, s.name, s.elapsed_ms, s.detail
            ));
            if let Some(a) = &s.advice {
                out.push_str(&format!("    建议：{a}\n"));
            }
        }
        out
    }
}

fn stage(name: &str, status: HealthStatus, elapsed_ms: u64, detail: String, advice: Option<String>) -> HealthStage {
    HealthStage {
        name: name.to_string(),
        status,
        elapsed_ms,
        detail,
        advice,
    }
}

fn skipped(name: &str, why: &str) -> HealthStage {
    stage(name, HealthStatus::Skipped, 0, why.to_string(), None)
}

fn build(stages: Vec<HealthStage>, total_ms: u64, mysql_version: Option<String>, cipher_id: String) -> HealthReport {
    // 有 Fail → 不健康；全 Skipped（不可能出现，第一层总会执行）或存在 Fail 都算异常。
    let healthy = stages.iter().all(|s| s.status != HealthStatus::Fail)
        && stages.iter().any(|s| s.status == HealthStatus::Pass);
    HealthReport {
        healthy,
        stages,
        total_ms,
        mysql_version,
        cipher_id,
    }
}

/// 执行一次完整探活。**本函数不返回 Err** —— 任何失败都表达为报告里的 Fail，
/// 诊断工具自己崩掉就失去了全部意义。
pub async fn probe(cfg: &TunnelConfig) -> HealthReport {
    let total = Instant::now();
    let mut stages: Vec<HealthStage> = Vec::with_capacity(3);

    // ---------- 第 0 步：算法解析 ----------
    // 算法名非法在配置加载期本应已被拦下。走到这里说明配置绕过了校验，
    // 三层全部无法执行 —— 报成「传输失败」会误导用户去查网络。
    let cipher = match cfg.resolve_cipher() {
        Ok(c) => c,
        Err(e) => {
            stages.push(stage(
                "配置",
                HealthStatus::Fail,
                0,
                format!("加密算法配置非法：{e}"),
                Some("请修改项目配置中的 cipher 项，取值必须是服务端也支持的算法标识。".into()),
            ));
            stages.push(skipped("传输", "上一层失败，未执行"));
            stages.push(skipped("认证 + 数据库", "上一层失败，未执行"));
            return build(stages, total.elapsed().as_millis() as u64, None, cfg.cipher.clone());
        }
    };
    let cipher_id = cipher.id().to_string();

    // ---------- 第 1 层：传输 ----------
    let ws_url = cfg.websocket_url();
    let sw = Instant::now();
    let connect = tokio_tungstenite::connect_async(&ws_url);
    let ws = match tokio::time::timeout(cfg.ws_connect, connect).await {
        Ok(Ok((ws, _resp))) => ws,
        Ok(Err(e)) => {
            stages.push(stage(
                "传输",
                HealthStatus::Fail,
                sw.elapsed().as_millis() as u64,
                format!("无法建立 WebSocket 连接：{e}"),
                Some(
                    "请确认服务端地址(serverUrl)与路径(wsPath)正确、服务端已启动，\
                     且中间的反向代理 / WAF 允许 WebSocket 升级。"
                        .into(),
                ),
            ));
            stages.push(skipped("认证 + 数据库", "网络未连通，未执行"));
            return build(stages, total.elapsed().as_millis() as u64, None, cipher_id);
        }
        Err(_) => {
            stages.push(stage(
                "传输",
                HealthStatus::Fail,
                sw.elapsed().as_millis() as u64,
                format!("WebSocket 连接超时（{}ms）", cfg.ws_connect.as_millis()),
                Some("服务端不可达或被中间设备丢弃了连接，请检查网络与防火墙。".into()),
            ));
            stages.push(skipped("认证 + 数据库", "网络未连通，未执行"));
            return build(stages, total.elapsed().as_millis() as u64, None, cipher_id);
        }
    };
    stages.push(stage(
        "传输",
        HealthStatus::Pass,
        sw.elapsed().as_millis() as u64,
        format!("WebSocket 已连接：{ws_url}"),
        None,
    ));

    // ---------- 第 2、3 层：认证 + 数据库 ----------
    // 这两层无法拆开单独计时：服务端认证通过后不发 ACK，而是直接去连 MySQL、
    // 再把 MySQL 握手包推过来（CryptunnelWebSocketHandler）。客户端能观察到的
    // 只有「收到了第一个数据帧」这一个事件 —— 认证成功的时刻在线路上没有任何
    // 可观测的信号。所以认证层记为通过且耗时记 0，把这段等待全部计入数据库层，
    // 宁可让一层的耗时偏大，也不能凭空编一个认证耗时（那个数字会被用来做性能判断）。
    let (mut auth_db_stages, version) = probe_auth_and_database(cfg, cipher.as_ref(), ws).await;
    stages.append(&mut auth_db_stages);

    build(
        stages,
        total.elapsed().as_millis() as u64,
        version,
        cipher_id,
    )
}

/// 认证 + 数据库两层（在上层已建立的 WS 上执行）。
///
/// 返回两层结果与探到的 MySQL 版本号。无论成败都会**主动关闭** WS ——
/// 探活留下的连接会一直占着服务端的连接名额和一条 MySQL 连接。
async fn probe_auth_and_database(
    cfg: &TunnelConfig,
    cipher: &dyn cryptunnel_crypto::TunnelCipher,
    ws: WsStream,
) -> (Vec<HealthStage>, Option<String>) {
    let mut stages: Vec<HealthStage> = Vec::with_capacity(2);
    let (mut write, mut read) = ws.split();

    // 认证报文必须由 auth::build_sealed 构造 —— 与真实隧道同一条代码路径。
    let sw = Instant::now();
    let sealed = match auth::build_sealed(cipher, &cfg.auth_key, &cfg.aes_key, cfg.target_id.as_deref()) {
        Ok(s) => s,
        Err(e) => {
            stages.push(stage(
                "认证",
                HealthStatus::Fail,
                0,
                format!("认证报文构造失败：{e}"),
                Some("请核对 aesKey / authKey 配置。".into()),
            ));
            stages.push(skipped("数据库", "上一层失败，未执行"));
            let _ = write.send(Message::Close(None)).await;
            return (stages, None);
        }
    };
    if let Err(e) = write.send(Message::Text(sealed.into())).await {
        stages.push(stage(
            "认证",
            HealthStatus::Fail,
            0,
            format!("认证报文发送失败：{e}"),
            None,
        ));
        stages.push(skipped("数据库", "上一层失败，未执行"));
        let _ = write.send(Message::Close(None)).await;
        return (stages, None);
    }

    // 等服务端首帧（成功=MySQL 握手包；失败=close 帧，reason 映射为中文）。
    // 探活用比 auth_response 更长的窗口：真实隧道里服务端建 MySQL 连接的耗时
    // 也由这个帧暴露，3 秒的认证窗对慢库太紧（.NET 版用 readTimeout，对齐取 http_read）。
    let wait_budget = cfg.auth_response.max(Duration::from_secs(10)).min(cfg.http_read);
    let wait = async {
        while let Some(item) = read.next().await {
            match item {
                Ok(Message::Close(frame)) => {
                    let reason = frame.as_ref().map(|f| f.reason.as_str());
                    let failure = close_reason::map(reason);
                    return Err(failure.message);
                }
                Ok(Message::Binary(_)) => {
                    return Err(
                        "收到二进制帧，但服务端只使用文本帧。可能连接到了非预期的服务。"
                            .to_string(),
                    );
                }
                Ok(Message::Text(text)) => {
                    return cipher
                        .open(text.as_str(), &cfg.aes_key)
                        .map_err(|e| {
                            format!(
                                "首帧解密失败：{e}。请核对加密算法(cipher)与密钥(aesKey)是否与服务端一致。"
                            )
                        });
                }
                Err(e) => return Err(format!("连接异常：{e}")),
                _ => continue, // Ping/Pong 等控制帧忽略
            }
        }
        Err("连接被对端关闭，未收到握手包或关闭原因。".to_string())
    };

    match tokio::time::timeout(wait_budget, wait).await {
        Ok(Ok(handshake)) => {
            let elapsed = sw.elapsed().as_millis() as u64;
            // 认证通过的时刻线上无信号：收到首帧 = 认证已过。认证层记 0，不编造。
            stages.push(stage(
                "认证",
                HealthStatus::Pass,
                0,
                "加密认证通过（认证与建库线上不可分，耗时全部计入数据库层）".into(),
                None,
            ));
            let version = parse_mysql_version(&handshake);
            let detail = match &version {
                Some(v) => format!("服务端已连接 MySQL（版本 {v}），握手包 {} 字节", handshake.len()),
                None => format!("服务端已连接 MySQL，握手包 {} 字节（版本号未解析出）", handshake.len()),
            };
            stages.push(stage("数据库", HealthStatus::Pass, elapsed, detail, None));
            let _ = write.send(Message::Close(None)).await;
            (stages, version)
        }
        Ok(Err(msg)) => {
            stages.push(stage(
                "认证",
                HealthStatus::Fail,
                sw.elapsed().as_millis() as u64,
                msg,
                Some("按上述提示核对配置；配置无误时再怀疑网络。".into()),
            ));
            stages.push(skipped("数据库", "上一层失败，未执行"));
            let _ = write.send(Message::Close(None)).await;
            (stages, None)
        }
        Err(_) => {
            stages.push(stage(
                "认证",
                HealthStatus::Fail,
                sw.elapsed().as_millis() as u64,
                format!(
                    "等待服务端响应超时（{}ms）。服务端可能无法连接到目标数据库，或网络存在中间设备缓冲。",
                    wait_budget.as_millis()
                ),
                Some("请确认服务端与 MySQL 之间的连通性，以及白名单中的目标地址。".into()),
            ));
            stages.push(skipped("数据库", "上一层失败，未执行"));
            let _ = write.send(Message::Close(None)).await;
            (stages, None)
        }
    }
}

/// 从 MySQL 握手包（Protocol::HandshakeV10）解析服务端版本号。
///
/// 布局（小端）：`payload_len(3) seq(1) protocol(1=0x0a) version(NUL 结尾字符串) ...`。
/// 输入可能是解密后的完整帧字节（含 4 字节包头）。解析不出返回 None ——
/// 版本号只是展示信息，解析失败不影响「数据库层通过」的结论。
pub fn parse_mysql_version(frame: &[u8]) -> Option<String> {
    if frame.len() < 6 {
        return None;
    }
    // 跳过 4 字节包头；协议号必须是 0x0a（HandshakeV10）。
    if frame[4] != 0x0a {
        return None;
    }
    let rest = &frame[5..];
    let end = rest.iter().position(|&b| b == 0)?;
    let v = std::str::from_utf8(&rest[..end]).ok()?;
    if v.is_empty() {
        return None;
    }
    Some(v.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 造一个最小 HandshakeV10 帧：len(3) seq(1) 0x0a "8.0.36\0" + 填充。
    fn handshake_frame(version: &str) -> Vec<u8> {
        let payload_len = 1 + version.len() + 1 + 24; // protocol + version + nul + 后续字段填充
        let mut f = Vec::new();
        f.extend_from_slice(&(payload_len as u32).to_le_bytes()[..3]);
        f.push(0); // seq
        f.push(0x0a);
        f.extend_from_slice(version.as_bytes());
        f.push(0);
        f.extend_from_slice(&[0u8; 24]);
        f
    }

    #[test]
    fn parses_standard_handshake() {
        let f = handshake_frame("8.0.36");
        assert_eq!(parse_mysql_version(&f).as_deref(), Some("8.0.36"));
    }

    #[test]
    fn parses_mariadb_style_version() {
        let f = handshake_frame("5.5.5-10.6.18-MariaDB");
        assert_eq!(
            parse_mysql_version(&f).as_deref(),
            Some("5.5.5-10.6.18-MariaDB")
        );
    }

    #[test]
    fn rejects_too_short_and_wrong_protocol() {
        assert_eq!(parse_mysql_version(&[]), None);
        assert_eq!(parse_mysql_version(&[0, 0, 0, 0]), None);
        // 协议号不是 0x0a（比如 ERR 包首字节 0xff）
        let mut f = handshake_frame("8.0.36");
        f[4] = 0xff;
        assert_eq!(parse_mysql_version(&f), None);
        // 没有 NUL 结尾
        let f2 = vec![0, 0, 0, 0, 0x0a, b'8', b'.', b'0'];
        assert_eq!(parse_mysql_version(&f2), None);
    }

    #[test]
    fn report_healthy_requires_pass_and_no_fail() {
        let pass = stage("a", HealthStatus::Pass, 1, "x".into(), None);
        let skip = stage("b", HealthStatus::Skipped, 0, "x".into(), None);
        let fail = stage("c", HealthStatus::Fail, 1, "x".into(), None);
        assert!(build(vec![pass.clone(), skip.clone()], 1, None, "c".into()).healthy);
        assert!(!build(vec![pass.clone(), fail], 1, None, "c".into()).healthy);
        assert!(!build(vec![skip], 1, None, "c".into()).healthy);
    }

    #[test]
    fn display_text_contains_conclusion_and_stages() {
        let r = build(
            vec![
                stage("传输", HealthStatus::Pass, 12, "ok".into(), None),
                stage("认证", HealthStatus::Fail, 3, "bad".into(), Some("改配置".into())),
            ],
            15,
            None,
            "aes-256-cbc-hmac-sha256".into(),
        );
        let t = r.to_display_text();
        assert!(t.contains("结论：异常"));
        assert!(t.contains("传输"));
        assert!(t.contains("认证"));
        assert!(t.contains("建议：改配置"));
    }
}
