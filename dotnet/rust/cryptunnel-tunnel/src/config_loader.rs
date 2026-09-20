//! 多项目配置目录加载（对齐 .NET `ConfigLoader.Load`）。
//!
//! 目录里放任意多个 `*.yaml` / `*.yml`，两种文件：
//! - 纯 `defaults` 文件（有 `defaults` 段、无 `name`）：为整目录提供缺省基线。
//! - 项目文件（有 `name`）：定义一条隧道。
//!
//! 两遍法：第一遍按文件名顺序叠加 defaults，第二遍把 defaults 与项目字段合并成
//! 最终 [`TunnelConfig`]。任何一条规则不满足 → 该项目进 errors 并被拒绝加载，
//! 绝不影响其它项目（一个坏文件不能拖死全部）。
//!
//! 校验规则与 .NET 逐条对齐（理由见各分支注释）：
//! schemaVersion=1 / name=[a-z0-9-]+ 且唯一 / serverUrl http(s) 前缀 / aesKey+authKey 必填 /
//! local.port 1024-65535 且全局唯一 / 非回环监听闸门 / chunkSize 上限 / cipher 归一 /
//! health.intervalSec 下限 / httpBasePath 归一。

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::time::Duration;

use crate::config::{TransportMode, TunnelConfig};
use crate::project_config::{
    DefaultsSection, ProjectFile, HEALTH_INTERVAL_DEFAULT_SEC, HEALTH_INTERVAL_MIN_SEC,
    SUPPORTED_SCHEMA,
};

/// 单条配置错误（带文件名，便于界面列出哪个文件坏了）。
#[derive(Debug, Clone)]
pub struct ConfigError {
    pub file: String,
    pub message: String,
}

/// 加载结果：有效配置 + 错误列表（两者可同时非空）。
#[derive(Debug, Default)]
pub struct LoadResult {
    pub configs: Vec<TunnelConfig>,
    pub errors: Vec<ConfigError>,
}

/// 扫描目录并加载全部项目配置。
pub fn load(config_dir: &Path) -> LoadResult {
    let mut result = LoadResult::default();
    if !config_dir.is_dir() {
        result.errors.push(ConfigError {
            file: config_dir.display().to_string(),
            message: "配置目录不存在".into(),
        });
        return result;
    }

    // 收集 *.yaml / *.yml，按文件名（小写序）排序——叠加 defaults 的顺序敏感。
    let mut files: Vec<PathBuf> = Vec::new();
    if let Ok(rd) = std::fs::read_dir(config_dir) {
        for entry in rd.flatten() {
            let p = entry.path();
            if !p.is_file() {
                continue;
            }
            let ext = p
                .extension()
                .and_then(|e| e.to_str())
                .map(|e| e.to_ascii_lowercase());
            if matches!(ext.as_deref(), Some("yaml") | Some("yml")) {
                files.push(p);
            }
        }
    }
    files.sort_by_key(|p| p.file_name().map(|n| n.to_string_lossy().to_lowercase()));

    // 第一遍：合并 defaults。
    let mut defaults = DefaultsSection::default();
    let mut parsed: Vec<(PathBuf, Option<ProjectFile>)> = Vec::new();
    for f in &files {
        let root = try_parse(f, &mut result.errors);
        if let Some(pf) = &root {
            if let Some(d) = &pf.defaults {
                defaults.merge_from(d);
            }
        }
        parsed.push((f.clone(), root));
    }

    // 第二遍：解析项目。
    let mut used_names: HashSet<String> = HashSet::new();
    let mut used_ports: HashSet<u16> = HashSet::new();

    for (f, root) in parsed {
        let root = match root {
            Some(r) => r,
            None => continue,
        };
        if root.defaults.is_some() && root.name.is_none() {
            continue; // 纯 defaults 文件
        }
        if root.name.is_none() {
            result.errors.push(ConfigError {
                file: file_name(&f),
                message: "缺少 name 字段，且不是 defaults 文件".into(),
            });
            continue;
        };

        match resolve(&root, &defaults, &mut used_names, &mut used_ports) {
            Ok(cfg) => result.configs.push(cfg),
            Err(message) => result.errors.push(ConfigError {
                file: file_name(&f),
                message,
            }),
        }
    }

    result
}

fn file_name(p: &Path) -> String {
    p.file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_else(|| p.display().to_string())
}

/// 解析单个 yaml 文件；schemaVersion 不符只记错误、仍返回结构（与 .NET 一致——
/// 错误已列出，该文件后续在第二遍按规则处理）。
fn try_parse(file: &Path, errors: &mut Vec<ConfigError>) -> Option<ProjectFile> {
    let text = match std::fs::read_to_string(file) {
        Ok(t) => t,
        Err(e) => {
            errors.push(ConfigError {
                file: file_name(file),
                message: format!("读取失败：{e}"),
            });
            return None;
        }
    };
    match serde_yaml::from_str::<ProjectFile>(&text) {
        Ok(root) => {
            // 纯 defaults 文件（无 name）不写 schemaVersion 属正常，不记错误；
            // 项目文件必须显式声明 schemaVersion=1，拒绝以免误兼容。
            let is_pure_defaults = root.defaults.is_some() && root.name.is_none();
            if !is_pure_defaults && root.schema_version != SUPPORTED_SCHEMA {
                errors.push(ConfigError {
                    file: file_name(file),
                    message: format!(
                        "schemaVersion={} 不被支持（要求 {SUPPORTED_SCHEMA}，拒绝加载以免误兼容）",
                        root.schema_version
                    ),
                });
                return None;
            }
            Some(root)
        }
        Err(e) => {
            errors.push(ConfigError {
                file: file_name(file),
                message: format!("解析失败：{e}"),
            });
            None
        }
    }
}

/// 把 defaults + 项目文件合并成最终 TunnelConfig。任一校验不过返回中文原因。
fn resolve(
    r: &ProjectFile,
    d: &DefaultsSection,
    used_names: &mut HashSet<String>,
    used_ports: &mut HashSet<u16>,
) -> Result<TunnelConfig, String> {
    let name = r.name.clone().unwrap_or_default();
    if !name
        .chars()
        .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
        || name.is_empty()
    {
        return Err("name 只能是小写字母/数字/连字符 [a-z0-9-]".into());
    }
    if !used_names.insert(name.to_ascii_lowercase()) {
        return Err("name 重复".into());
    }

    let server_url = r
        .server_url
        .clone()
        .unwrap_or_default()
        .trim_end_matches('/')
        .to_string();
    if server_url.is_empty()
        || !(server_url.starts_with("http://") || server_url.starts_with("https://"))
    {
        return Err("serverUrl 必填且以 http:// 或 https:// 开头".into());
    }
    let aes_key = r.aes_key.clone().unwrap_or_default();
    let auth_key = r.auth_key.clone().unwrap_or_default();
    if aes_key.trim().is_empty() || auth_key.trim().is_empty() {
        return Err("aesKey 与 authKey 必填且非空".into());
    }

    let port_i64 = r
        .local
        .as_ref()
        .and_then(|l| l.port)
        .or_else(|| d.local.as_ref().and_then(|l| l.port))
        .unwrap_or(0);
    if !(1024..=65535).contains(&port_i64) {
        return Err(format!("local.port={port_i64} 超出范围（1024-65535）"));
    }
    let port = port_i64 as u16;
    if !used_ports.insert(port) {
        return Err(format!("local.port={port} 与其他项目冲突"));
    }

    // 监听地址闸门（唯一能让本机之外的人穿透防火墙的配置，必须显式承认）。
    let listen_address = r
        .local
        .as_ref()
        .and_then(|l| l.address.clone())
        .or_else(|| d.local.as_ref().and_then(|l| l.address.clone()))
        .unwrap_or_else(|| "127.0.0.1".into());
    let allow_non_loopback = r
        .local
        .as_ref()
        .and_then(|l| l.allow_non_loopback)
        .or_else(|| d.local.as_ref().and_then(|l| l.allow_non_loopback))
        .unwrap_or(false);
    crate::addr_policy::validate(Some(&listen_address), allow_non_loopback, port)?;

    let mode = parse_mode(
        r.transport
            .as_ref()
            .and_then(|t| t.mode.as_deref())
            .or_else(|| d.transport.as_ref().and_then(|t| t.mode.as_deref())),
    )?;

    // chunkSize：配大了不会在启动时出异常，而是传大结果集时对端 WS 1009 断线、
    // 用户只看到 DBeaver 的 08S01——必须提前成「加载即拒绝」。
    let chunk_size_i64 = r.chunk_size.or(d.chunk_size).unwrap_or(4096);
    if chunk_size_i64 <= 0 {
        return Err(format!("chunkSize={chunk_size_i64} 非法（必须为正数）"));
    }
    crate::framing::validate_chunk_size(chunk_size_i64 as usize)?;

    // cipher：写错没有任何协商余地，服务端回「Auth decrypt failed」，
    // 与 aesKey 配错完全无法区分——必须本地归一并拒绝未知值。
    let cipher_raw = r.cipher.as_deref().or(d.cipher.as_deref()).unwrap_or("");
    let cipher = crate::registry::normalize(cipher_raw).map_err(|e| e.to_string())?;

    // health 间隔：写小了不会有任何本地异常，只是安静消耗服务端连接名额——启动即拒绝。
    let health_enabled = r
        .health
        .as_ref()
        .and_then(|h| h.enabled)
        .or_else(|| d.health.as_ref().and_then(|h| h.enabled))
        .unwrap_or(false);
    let health_interval = r
        .health
        .as_ref()
        .and_then(|h| h.interval_sec)
        .or_else(|| d.health.as_ref().and_then(|h| h.interval_sec))
        .unwrap_or(HEALTH_INTERVAL_DEFAULT_SEC);
    if health_enabled && health_interval < HEALTH_INTERVAL_MIN_SEC {
        return Err(format!(
            "health.intervalSec={health_interval} 小于允许的最小值 {HEALTH_INTERVAL_MIN_SEC} 秒。\
             每次健康检查都会在服务端真实建立并断开一条数据库连接，过于频繁会占满服务端连接数。"
        ));
    }

    let allow_fallback = r
        .transport
        .as_ref()
        .and_then(|t| t.allow_fallback)
        .or_else(|| d.transport.as_ref().and_then(|t| t.allow_fallback))
        .unwrap_or(true);
    let t_ms = |sel: fn(&crate::project_config::TimeoutsSection) -> Option<i64>, def: u64| -> Duration {
        let v = r
            .timeouts
            .as_ref()
            .and_then(&sel)
            .or_else(|| d.timeouts.as_ref().and_then(sel))
            .unwrap_or(def as i64);
        Duration::from_millis(v.max(0) as u64)
    };
    let reconnect_enabled = r
        .reconnect
        .as_ref()
        .and_then(|x| x.enabled)
        .or_else(|| d.reconnect.as_ref().and_then(|x| x.enabled))
        .unwrap_or(true);
    let reconnect_max = r
        .reconnect
        .as_ref()
        .and_then(|x| x.max_attempts)
        .or_else(|| d.reconnect.as_ref().and_then(|x| x.max_attempts))
        .unwrap_or(0)
        .max(0) as u32;

    let target_id = r
        .target_id
        .clone()
        .map(|t| t.trim().to_string())
        .filter(|t| !t.is_empty());

    Ok(TunnelConfig {
        name: name.clone(),
        display_name: r.display_name.clone().unwrap_or_else(|| name.clone()),
        enabled: r.enabled.unwrap_or(true),
        server_url,
        aes_key,
        auth_key,
        listen_address,
        allow_non_loopback,
        local_port: port,
        ws_path: r
            .ws_path
            .clone()
            .or_else(|| d.ws_path.clone())
            .unwrap_or_else(|| "/ws-cryptunnel".into()),
        http_base_path: normalize_base_path(
            r.http_base_path
                .as_deref()
                .or(d.http_base_path.as_deref())
                .unwrap_or("/cryptunnel"),
        ),
        mode,
        allow_fallback,
        ws_connect: t_ms(|t| t.ws_connect_ms, 10_000),
        http_connect: t_ms(|t| t.http_connect_ms, 10_000),
        http_read: t_ms(|t| t.http_read_ms, 30_000),
        auth_response: t_ms(|t| t.auth_response_ms, 3_000),
        reconnect_delay: t_ms(|t| t.reconnect_delay_ms, 3_000),
        reconnect_enabled,
        reconnect_max_attempts: reconnect_max,
        chunk_size: chunk_size_i64 as usize,
        cipher,
        target_id,
    })
}

/// 归一化 HTTP 基础路径：保证以 '/' 开头、不以 '/' 结尾（对齐 `NormalizeBasePath`）。
pub fn normalize_base_path(raw: &str) -> String {
    let mut s = raw.trim().to_string();
    if s.is_empty() {
        return "/cryptunnel".into();
    }
    if !s.starts_with('/') {
        s = format!("/{s}");
    }
    while s.len() > 1 && s.ends_with('/') {
        s.pop();
    }
    s
}

fn parse_mode(s: Option<&str>) -> Result<TransportMode, String> {
    match s.map(|v| v.to_ascii_lowercase()) {
        None => Ok(TransportMode::Auto),
        Some(ref m) if m.is_empty() || m == "auto" => Ok(TransportMode::Auto),
        Some(ref m) if m == "websocket" || m == "ws" => Ok(TransportMode::WebSocket),
        Some(ref m) if m == "http" => Ok(TransportMode::Http),
        Some(m) => Err(format!("未知 transport.mode: {m}（可选 auto/websocket/http）")),
    }
}

/// 读取单个项目配置文件（不验证），用于编辑器回填（对齐 `TryReadFile`）。
pub fn try_read_file(path: &Path) -> Result<ProjectFile, String> {
    if !path.exists() {
        return Err(format!("文件不存在：{}", path.display()));
    }
    let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
    let root: ProjectFile = serde_yaml::from_str(&text).map_err(|e| e.to_string())?;
    Ok(root)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::DEFAULT_CIPHER;
    use std::fs;

    fn write(dir: &Path, name: &str, content: &str) {
        fs::write(dir.join(name), content).unwrap();
    }

    fn tmpdir(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "cryptunnel-cfgtest-{tag}-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    const GOOD: &str = r#"
schemaVersion: 1
name: crm-prod
serverUrl: "http://db.internal:8080"
aesKey: "ak"
authKey: "hk"
local:
  port: 3307
"#;

    #[test]
    fn loads_valid_project() {
        let dir = tmpdir("valid");
        write(&dir, "a.yaml", GOOD);
        let r = load(&dir);
        assert_eq!(r.errors.len(), 0, "errors: {:?}", r.errors);
        assert_eq!(r.configs.len(), 1);
        let c = &r.configs[0];
        assert_eq!(c.name, "crm-prod");
        assert_eq!(c.local_port, 3307);
        assert_eq!(c.cipher, DEFAULT_CIPHER);
        assert_eq!(c.http_base_path, "/cryptunnel");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn defaults_merge_and_override() {
        let dir = tmpdir("defaults");
        write(
            &dir,
            "00-defaults.yaml",
            "defaults:\n  chunkSize: 2048\n  cipher: sm4\n  local:\n    port: 3400\n",
        );
        write(&dir, "b.yaml", GOOD);
        let r = load(&dir);
        assert_eq!(r.errors.len(), 0, "errors: {:?}", r.errors);
        assert_eq!(r.configs.len(), 1);
        assert_eq!(r.configs[0].chunk_size, 2048); // 继承 defaults
        assert_eq!(r.configs[0].cipher, "sm4-cbc-hmac-sha256"); // 别名归一
        assert_eq!(r.configs[0].local_port, 3307); // 项目覆盖 defaults
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn bad_file_does_not_kill_others() {
        let dir = tmpdir("mixed");
        write(&dir, "a-bad.yaml", "schemaVersion: 2\nname: x\n");
        write(&dir, "b-good.yaml", GOOD);
        let r = load(&dir);
        assert_eq!(r.configs.len(), 1);
        assert_eq!(r.errors.len(), 1);
        assert!(r.errors[0].file.contains("a-bad"));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn rejects_port_conflict_and_bad_name() {
        let dir = tmpdir("conflict");
        write(&dir, "a.yaml", GOOD);
        write(
            &dir,
            "b.yaml",
            &GOOD.replace("crm-prod", "CRM_Prod"), // 非法字符
        );
        write(
            &dir,
            "c.yaml",
            &GOOD.replace("crm-prod", "crm-prod-2"), // 同端口 3307 冲突
        );
        let r = load(&dir);
        assert_eq!(r.configs.len(), 1);
        assert_eq!(r.errors.len(), 2);
        assert!(r.errors.iter().any(|e| e.message.contains("name 只能")));
        assert!(r.errors.iter().any(|e| e.message.contains("冲突")));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn rejects_non_loopback_without_opt_in() {
        let dir = tmpdir("loopback");
        write(
            &dir,
            "a.yaml",
            &(GOOD.to_string() + "  address: 0.0.0.0\n"),
        );
        let r = load(&dir);
        assert_eq!(r.configs.len(), 0);
        assert!(r.errors.iter().any(|e| e.message.contains("不是回环地址")));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn rejects_bad_chunk_and_unknown_cipher_and_low_health() {
        let dir = tmpdir("guards");
        write(
            &dir,
            "a.yaml",
            &(GOOD.to_string() + "chunkSize: 6001\n"),
        );
        write(
            &dir,
            "b.yaml",
            &(GOOD.replace("crm-prod", "p2").replace("3307", "3308")
                + "cipher: aes-cbc\n"),
        );
        write(
            &dir,
            "c.yaml",
            &(GOOD.replace("crm-prod", "p3").replace("3307", "3309")
                + "health:\n  enabled: true\n  intervalSec: 5\n"),
        );
        let r = load(&dir);
        assert_eq!(r.configs.len(), 0);
        assert_eq!(r.errors.len(), 3);
        assert!(r.errors.iter().any(|e| e.message.contains("分片大小")));
        assert!(r.errors.iter().any(|e| e.message.contains("加密算法")));
        assert!(r.errors.iter().any(|e| e.message.contains("intervalSec")));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn normalize_base_path_cases() {
        assert_eq!(normalize_base_path(""), "/cryptunnel");
        assert_eq!(normalize_base_path("cryptunnel"), "/cryptunnel");
        assert_eq!(normalize_base_path("/jdbc-proxy/"), "/jdbc-proxy");
        assert_eq!(normalize_base_path("/"), "/");
    }
}
