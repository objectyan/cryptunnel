//! 多项目配置文件的原始结构与 YAML 回写（对齐 .NET `ConfigModels` + `AppController.BuildProjectYaml`）。
//!
//! 单文件可同时容纳 `defaults` 段和项目字段，避免为区分两种文件建两套模型。
//! 回写只写非默认字段：老配置里没有 `cipher` 键，若无条件写回，用户只是改个端口号
//! 也会让文件多出一行——对会把 config.d 纳入版本管理、会 diff 配置的用户是凭空的噪声。
//! 省略与显式写默认值在语义上等价（缺省链回落到同一个算法）。
//!
//! `health` 段必须原样保留：编辑器界面可能不暴露这一项，但它可能已存在于用户手写的
//! yaml 里——回写时不写就等于「改了个端口号，顺手把周期性健康检查关掉了」，且没有任何提示。
//! 配置回写吞掉界面未覆盖的字段是最隐蔽的一类数据丢失。

use serde::{Deserialize, Serialize};

/// 支持的 schemaVersion（拒绝其它版本以免误兼容）。
pub const SUPPORTED_SCHEMA: i64 = 1;

/// 健康检查最小间隔（秒），对齐 .NET `TunnelConfig.HealthIntervalMinSec`。
pub const HEALTH_INTERVAL_MIN_SEC: i64 = 30;
/// 健康检查默认间隔（秒）。
pub const HEALTH_INTERVAL_DEFAULT_SEC: i64 = 300;

/// YAML 里单个项目文件的原始结构（含可选覆盖段）。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProjectFile {
    pub schema_version: i64,
    pub name: Option<String>,
    pub display_name: Option<String>,
    pub enabled: Option<bool>,
    pub server_url: Option<String>,
    pub aes_key: Option<String>,
    pub auth_key: Option<String>,
    pub local: Option<LocalSection>,
    pub ws_path: Option<String>,
    pub http_base_path: Option<String>,
    pub transport: Option<TransportSection>,
    pub timeouts: Option<TimeoutsSection>,
    pub reconnect: Option<ReconnectSection>,
    pub logging: Option<LoggingSection>,
    pub chunk_size: Option<i64>,
    pub health: Option<HealthSection>,
    pub cipher: Option<String>,
    pub defaults: Option<Box<DefaultsSection>>,
    pub target_id: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct LocalSection {
    pub address: Option<String>,
    pub port: Option<i64>,
    pub allow_non_loopback: Option<bool>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TransportSection {
    pub mode: Option<String>,
    pub allow_fallback: Option<bool>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TimeoutsSection {
    pub ws_connect_ms: Option<i64>,
    pub http_connect_ms: Option<i64>,
    pub http_read_ms: Option<i64>,
    pub auth_response_ms: Option<i64>,
    pub reconnect_delay_ms: Option<i64>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ReconnectSection {
    pub enabled: Option<bool>,
    pub max_attempts: Option<i64>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct LoggingSection {
    pub level: Option<String>,
    pub max_file_size_mb: Option<i64>,
    pub retain_days: Option<i64>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HealthSection {
    pub enabled: Option<bool>,
    pub interval_sec: Option<i64>,
}

/// defaults 文件：整段作为基线被各项目覆盖。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DefaultsSection {
    pub local: Option<LocalSection>,
    pub ws_path: Option<String>,
    pub http_base_path: Option<String>,
    pub transport: Option<TransportSection>,
    pub timeouts: Option<TimeoutsSection>,
    pub reconnect: Option<ReconnectSection>,
    pub logging: Option<LoggingSection>,
    pub chunk_size: Option<i64>,
    pub health: Option<HealthSection>,
    pub cipher: Option<String>,
}

impl DefaultsSection {
    /// 把 `src` 合入 `self`（只填空位，不覆盖已有值），对齐 `MergeInto`。
    pub fn merge_from(&mut self, src: &DefaultsSection) {
        if self.local.is_none() {
            self.local = src.local.clone();
        }
        if self.ws_path.is_none() {
            self.ws_path = src.ws_path.clone();
        }
        if self.http_base_path.is_none() {
            self.http_base_path = src.http_base_path.clone();
        }
        if self.transport.is_none() {
            self.transport = src.transport.clone();
        }
        if self.timeouts.is_none() {
            self.timeouts = src.timeouts.clone();
        }
        if self.reconnect.is_none() {
            self.reconnect = src.reconnect.clone();
        }
        if self.logging.is_none() {
            self.logging = src.logging.clone();
        }
        if self.chunk_size.is_none() {
            self.chunk_size = src.chunk_size;
        }
        if self.health.is_none() {
            self.health = src.health.clone();
        }
        if self.cipher.is_none() {
            self.cipher = src.cipher.clone();
        }
    }
}

/// YAML 字符串安全包装（等价 .NET `YamlStr`）：双引号包裹并转义反斜杠与双引号。
fn yaml_str(s: &str) -> String {
    format!("\"{}\"", s.replace('\\', "\\\\").replace('"', "\\\""))
}

/// 把 ProjectFile 序列化为 YAML 文本（等价 .NET `BuildProjectYaml`）。
///
/// 只写非默认字段：
/// - `displayName` 空则不写。
/// - `wsPath` 为默认 `/ws-cryptunnel` 则不写。
/// - `cipher` 归一后为默认 `aes-256-cbc-hmac-sha256` 则不写。
/// - `local.address` 空则不写；`allowNonLoopback` 仅在为 true 时写（默认 false，省略等价）。
/// - `health` 段在任一有值时原样保留（即使界面未暴露该配置）。
pub fn build_project_yaml(pf: &ProjectFile) -> String {
    use std::fmt::Write as _;
    let mut sb = String::new();
    let _ = writeln!(sb, "schemaVersion: {}", pf.schema_version.max(1));
    let _ = writeln!(sb);
    if let Some(name) = &pf.name {
        let _ = writeln!(sb, "name: {name}");
    }
    if let Some(dn) = pf.display_name.as_deref().filter(|s| !s.trim().is_empty()) {
        let _ = writeln!(sb, "displayName: {}", yaml_str(dn));
    }
    let _ = writeln!(sb, "enabled: {}", pf.enabled.unwrap_or(true));
    let _ = writeln!(sb);
    if let Some(url) = &pf.server_url {
        let _ = writeln!(sb, "serverUrl: {}", yaml_str(url));
    }
    const DEFAULT_WS_PATH: &str = "/ws-cryptunnel";
    if let Some(wp) = pf.ws_path.as_deref().filter(|s| !s.trim().is_empty()) {
        if wp != DEFAULT_WS_PATH {
            let _ = writeln!(sb, "wsPath: {}", yaml_str(wp));
        }
    }
    if let Some(k) = &pf.aes_key {
        let _ = writeln!(sb, "aesKey: {}", yaml_str(k));
    }
    if let Some(k) = &pf.auth_key {
        let _ = writeln!(sb, "authKey: {}", yaml_str(k));
    }

    // cipher：只在非默认时写。归一化失败（未知算法）时原样保留，交给加载期校验拒绝。
    let cipher_id = pf
        .cipher
        .as_deref()
        .map(|c| crate::registry::normalize(c).unwrap_or_else(|_| c.to_string()));
    if let Some(id) = cipher_id {
        if id != crate::config::DEFAULT_CIPHER {
            let _ = writeln!(sb, "cipher: {id}");
        }
    }

    if let Some(tid) = pf.target_id.as_deref().filter(|s| !s.trim().is_empty()) {
        let _ = writeln!(sb, "targetId: {}", yaml_str(tid));
    }

    let _ = writeln!(sb);
    let _ = writeln!(sb, "local:");
    let port = pf.local.as_ref().and_then(|l| l.port).unwrap_or(0);
    let _ = writeln!(sb, "  port: {port}");
    if let Some(addr) = pf
        .local
        .as_ref()
        .and_then(|l| l.address.as_deref())
        .filter(|s| !s.trim().is_empty())
    {
        let _ = writeln!(sb, "  address: {addr}");
    }
    // allowNonLoopback 不能因「地址是非回环」就自动补上——那等于让程序替用户做风险承诺。
    // 它只反映用户在编辑器里勾选的结果。
    if pf.local.as_ref().and_then(|l| l.allow_non_loopback) == Some(true) {
        let _ = writeln!(sb, "  allowNonLoopback: true");
    }

    // health 段必须原样保留（见模块头注释）。
    if let Some(h) = &pf.health {
        if h.enabled.is_some() || h.interval_sec.is_some() {
            let _ = writeln!(sb);
            let _ = writeln!(sb, "health:");
            if let Some(en) = h.enabled {
                let _ = writeln!(sb, "  enabled: {en}");
            }
            if let Some(sec) = h.interval_sec {
                let _ = writeln!(sb, "  intervalSec: {sec}");
            }
        }
    }

    sb
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn yaml_roundtrip_minimal() {
        let pf = ProjectFile {
            schema_version: 1,
            name: Some("crm-prod".into()),
            server_url: Some("http://db.internal:8080".into()),
            aes_key: Some("ak".into()),
            auth_key: Some("hk".into()),
            local: Some(LocalSection {
                port: Some(3307),
                ..Default::default()
            }),
            ..Default::default()
        };
        let y = build_project_yaml(&pf);
        assert!(y.contains("name: crm-prod"));
        assert!(y.contains("serverUrl: \"http://db.internal:8080\""));
        assert!(y.contains("aesKey: \"ak\""));
        assert!(y.contains("  port: 3307"));
        // 默认 cipher / wsPath 不写
        assert!(!y.contains("cipher:"));
        assert!(!y.contains("wsPath:"));
        assert!(!y.contains("allowNonLoopback"));
        assert!(!y.contains("health:"));
    }

    #[test]
    fn yaml_writes_non_default_cipher_and_health() {
        let pf = ProjectFile {
            schema_version: 1,
            name: Some("n".into()),
            cipher: Some("sm4".into()), // 别名 → 归一后写正式 id
            health: Some(HealthSection {
                enabled: Some(true),
                interval_sec: Some(300),
            }),
            ..Default::default()
        };
        let y = build_project_yaml(&pf);
        assert!(y.contains("cipher: sm4-cbc-hmac-sha256"));
        assert!(y.contains("health:"));
        assert!(y.contains("  enabled: true"));
        assert!(y.contains("  intervalSec: 300"));
    }

    #[test]
    fn yaml_allow_non_loopback_only_when_true() {
        let mut pf = ProjectFile {
            schema_version: 1,
            name: Some("n".into()),
            local: Some(LocalSection {
                port: Some(3307),
                address: Some("0.0.0.0".into()),
                allow_non_loopback: Some(false),
            }),
            ..Default::default()
        };
        assert!(!build_project_yaml(&pf).contains("allowNonLoopback"));
        pf.local.as_mut().unwrap().allow_non_loopback = Some(true);
        assert!(build_project_yaml(&pf).contains("allowNonLoopback: true"));
    }
}
