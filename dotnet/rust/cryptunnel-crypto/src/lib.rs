//! Cryptunnel 隧道加密核心（Rust 重写）。
//!
//! 与 Java `cryptunnel-core` 的 TunnelCipher、.NET `Cryptunnel.Core.Crypto` 字节级兼容。
//! 三种算法共用一套密钥派生约定，帧结构如下：
//!
//! - 密钥派生：`SHA256("AES:"+k)`(32B) / `SHA256("HMAC:"+k)`(32B) / `SHA256("SM4:"+k)[..16]`
//! - AES-CBC / SM4-CBC 帧：`Base64(IV[16] ‖ HMAC[32] ‖ 密文)`，HMAC 覆盖 `IV‖密文`
//! - AES-GCM 帧：`Base64(nonce[12] ‖ 密文 ‖ tag[16])`，AEAD 自带认证、无外挂 HMAC
//!
//! 字节级对齐由 `dotnet/build/parity/*.txt` 基准向量守护（见 `src/bin/parity.rs`）。

pub mod cipher;
pub mod kdf;
pub mod sm4;

pub use cipher::{AesCbcHmacSha256, AesGcm, Sm4CbcHmacSha256, TunnelCipher};
