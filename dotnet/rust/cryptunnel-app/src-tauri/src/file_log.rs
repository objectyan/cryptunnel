//! 会话日志落盘：滚动文件日志（对齐 .NET `FileLogger`）。
//!
//! 老版规则逐条对齐：
//! - 目录：便携模式 exe 旁 `logs/`；安装模式平台本地数据目录 `Cryptunnel/logs/`
//!   （.NET 用 `LocalApplicationData`，Tauri 用 `app_local_data_dir`，Windows 上同为
//!   `%LOCALAPPDATA%`，但 .NET 是 `%LOCALAPPDATA%\Cryptunnel\logs`，Tauri 的
//!   `app_local_data_dir` 带 identifier 子目录，因此这里手动拼 `Cryptunnel/logs` 保证
//!   与老版完全同路径）。
//! - 文件：`proxy.log` 超过 `maxFileSizeMb`（默认 10MB）时滚动为 `proxy.1.log` …
//!   最多保留 `retainDays`（默认 7）份；启动时清理超龄文件。
//! - 行格式：`yyyy-MM-dd HH:mm:ss.fff [LEVEL] [project] message`。
//! - 写日志失败绝不影响隧道运行（全部吞错，与 .NET 一致）。

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use cryptunnel_tunnel::LogLevel;
use tauri::Manager;

/// 默认单文件上限（MB），对齐 .NET `maxFileSizeMb = 10`。
pub const DEFAULT_MAX_FILE_SIZE_MB: u64 = 10;
/// 默认保留份数，对齐 .NET `retainDays = 7`。
pub const DEFAULT_RETAIN_DAYS: u64 = 7;

/// 滚动文件日志器（线程安全，可被多条隧道的事件回调共享）。
pub struct FileLogger {
    dir: PathBuf,
    max_bytes: u64,
    retain: u64,
    inner: Mutex<()>,
}

impl FileLogger {
    pub fn new(dir: PathBuf, max_file_size_mb: u64, retain_days: u64) -> Self {
        let logger = FileLogger {
            dir,
            max_bytes: max_file_size_mb.max(1) * 1024 * 1024,
            retain: retain_days.max(1),
            inner: Mutex::new(()),
        };
        let _ = fs::create_dir_all(&logger.dir);
        logger.cleanup_old();
        logger
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    /// 写一行日志（level/project/message），失败静默吞掉。
    pub fn log(&self, level: LogLevel, project: Option<&str>, message: &str) {
        let line = format!(
            "{} [{}]{} {}",
            timestamp_now(),
            level_text(level),
            project.map(|p| format!(" [{p}]")).unwrap_or_default(),
            message
        );
        let _guard = self.inner.lock().unwrap();
        let path = self.dir.join("proxy.log");
        if let Ok(meta) = fs::metadata(&path) {
            if meta.len() > self.max_bytes {
                self.roll(&path);
            }
        }
        if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(&path) {
            let _ = writeln!(f, "{line}");
        }
    }

    /// proxy.log → proxy.1.log，proxy.1.log → proxy.2.log …（倒序挪动，保留 retain 份）。
    ///
    /// 注意这里**有意修正了 .NET 老版的一个 off-by-one**：老版 `Roll` 里 i=1 时
    /// `dst = proxy.{i+1}.log`，导致 `proxy.log` 直接变成 `proxy.2.log`，
    /// `proxy.1.log` 永远不会出现——日志凭空跳号，排查时会误以为丢了一代。
    /// 修正后编号连续。除此之外的滚动方向（倒序挪动、保留份数、超龄清理）与老版一致。
    fn roll(&self, path: &Path) {
        // 最老的一份直接丢弃（被挤出窗口）。
        let oldest = self.dir.join(format!("proxy.{}.log", self.retain - 1));
        if oldest.exists() {
            let _ = fs::remove_file(&oldest);
        }
        // i 从大到小：proxy.{i-1}.log → proxy.{i}.log
        for i in (2..self.retain).rev() {
            let src = self.dir.join(format!("proxy.{}.log", i - 1));
            let dst = self.dir.join(format!("proxy.{i}.log"));
            if src.exists() {
                let _ = fs::rename(&src, &dst);
            }
        }
        // 当前文件 → proxy.1.log
        if path.exists() {
            let _ = fs::rename(path, self.dir.join("proxy.1.log"));
        }
    }

    /// 删除超过 retain 天未修改的 proxy*.log。
    fn cleanup_old(&self) {
        let cutoff = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0)
            .saturating_sub(self.retain * 86_400);
        if let Ok(rd) = fs::read_dir(&self.dir) {
            for entry in rd.flatten() {
                let p = entry.path();
                let name = p.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
                if !(name.starts_with("proxy") && name.ends_with(".log")) {
                    continue;
                }
                if let Ok(meta) = p.metadata() {
                    if let Ok(mtime) = meta.modified() {
                        let secs = mtime
                            .duration_since(UNIX_EPOCH)
                            .map(|d| d.as_secs())
                            .unwrap_or(0);
                        if secs < cutoff {
                            let _ = fs::remove_file(&p);
                        }
                    }
                }
            }
        }
    }
}

/// 解析日志目录（对齐 .NET `PathResolver.LogDir`）。
///
/// - 便携模式：exe 旁存在 `portable.txt` → exe 旁 `logs/`。
/// - 安装模式：平台本地数据目录的 **`Cryptunnel/logs/`**——注意不是 Tauri 的
///   `app_local_data_dir()`（那个带 `io.github.objectyan.cryptunnel` 子目录），
///   必须与其父目录拼接 `Cryptunnel` 才能落在和老版相同的 `%LOCALAPPDATA%\Cryptunnel\logs`。
pub fn resolve_log_dir(app: &tauri::AppHandle) -> PathBuf {
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."));
    if exe_dir.join("portable.txt").exists() {
        return exe_dir.join("logs");
    }
    app.path()
        .app_local_data_dir()
        .ok()
        .and_then(|p| p.parent().map(|parent| parent.join("Cryptunnel").join("logs")))
        .unwrap_or_else(|| exe_dir.join("logs"))
}

fn level_text(l: LogLevel) -> &'static str {
    match l {
        LogLevel::Info => "INFO",
        LogLevel::Warn => "WARN",
        LogLevel::Error => "ERROR",
    }
}

/// `yyyy-MM-dd HH:mm:ss.fff`。
///
/// 时区说明：.NET 老版用 `DateTime.Now`（本地时间）。Rust 标准库不带时区数据库，
/// 为不引入 chrono/time 依赖，这里用 UTC+8 固定偏移（项目用户全在国内，
/// 与老版本地时间在所有实际部署场景一致；若未来有其它时区用户再引 tz 库）。
fn timestamp_now() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs() as i64;
    let millis = now.subsec_millis();
    let (y, mo, d, h, mi, s) = utc_to_civil(secs + 8 * 3600);
    format!("{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}:{s:02}.{millis:03}")
}

/// Unix 秒 → UTC 年月日时分秒（Howard Hinnant 算法，无依赖）。
fn utc_to_civil(secs: i64) -> (i64, u32, u32, u32, u32, u32) {
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let h = (rem / 3600) as u32;
    let mi = ((rem % 3600) / 60) as u32;
    let s = (rem % 60) as u32;
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let mo = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if mo <= 2 { y + 1 } else { y }, mo, d, h, mi, s)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roll_keeps_retain_files() {
        let dir = std::env::temp_dir().join(format!("cryptunnel-logtest-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        let logger = FileLogger::new(dir.clone(), 1, 3); // 1MB 上限方便不触发
        logger.log(LogLevel::Info, Some("t"), "hello");
        assert!(dir.join("proxy.log").exists());
        let text = fs::read_to_string(dir.join("proxy.log")).unwrap();
        assert!(text.contains("[INFO] [t] hello"));
        // 手动触发滚动：伪造超大文件
        fs::write(dir.join("proxy.log"), "x".repeat(2 * 1024 * 1024)).unwrap();
        logger.log(LogLevel::Warn, None, "after roll");
        assert!(dir.join("proxy.1.log").exists());
        let cur = fs::read_to_string(dir.join("proxy.log")).unwrap();
        assert!(cur.contains("[WARN] after roll"));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn timestamp_format() {
        let ts = timestamp_now();
        // yyyy-MM-dd HH:mm:ss.fff
        assert_eq!(ts.len(), 23);
        assert_eq!(&ts[4..5], "-");
        assert_eq!(&ts[10..11], " ");
        assert_eq!(&ts[19..20], ".");
    }

    #[test]
    fn civil_conversion_sane() {
        // 2026-09-20 附近转换不炸、字段在合法范围
        let secs = 1_789_000_000i64;
        let (y, mo, d, h, _, _) = utc_to_civil(secs);
        assert_eq!(y, 2026);
        assert!((1..=12).contains(&mo));
        assert!((1..=31).contains(&d));
        assert!(h < 24);
    }
}
