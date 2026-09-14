using System;
using System.Security.Cryptography;
using System.Text;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// SM4 分组密码核心实现（GB/T 32907-2016）：128 位分组、128 位密钥、32 轮。
///
/// <b>为什么自己实现而不用 BouncyCastle</b>：.NET 侧引入 BouncyCastle.Cryptography 会给
/// 一个单文件自包含 WPF 客户端增加约 4MB 体积和一个第三方供应链依赖，
/// 而这里只需要 SM4 的分组函数——算法本身是公开国家标准、约 100 行、
/// 且可用 GB/T 32907-2016 附录 A 的官方测试向量守护正确性（见 Sm4EngineTests）。
///
/// 本类只做<b>单个分组</b>的加解密；CBC 链接与 PKCS7 填充由 <see cref="Sm4Cipher"/> 负责。
/// </summary>
internal sealed class Sm4Engine
{
    /// <summary>分组长度（字节）。</summary>
    public const int BlockSize = 16;

    /// <summary>S 盒，来自 GB/T 32907-2016 表 1。</summary>
    private static readonly byte[] Sbox =
    {
        0xd6, 0x90, 0xe9, 0xfe, 0xcc, 0xe1, 0x3d, 0xb7, 0x16, 0xb6, 0x14, 0xc2, 0x28, 0xfb, 0x2c, 0x05,
        0x2b, 0x67, 0x9a, 0x76, 0x2a, 0xbe, 0x04, 0xc3, 0xaa, 0x44, 0x13, 0x26, 0x49, 0x86, 0x06, 0x99,
        0x9c, 0x42, 0x50, 0xf4, 0x91, 0xef, 0x98, 0x7a, 0x33, 0x54, 0x0b, 0x43, 0xed, 0xcf, 0xac, 0x62,
        0xe4, 0xb3, 0x1c, 0xa9, 0xc9, 0x08, 0xe8, 0x95, 0x80, 0xdf, 0x94, 0xfa, 0x75, 0x8f, 0x3f, 0xa6,
        0x47, 0x07, 0xa7, 0xfc, 0xf3, 0x73, 0x17, 0xba, 0x83, 0x59, 0x3c, 0x19, 0xe6, 0x85, 0x4f, 0xa8,
        0x68, 0x6b, 0x81, 0xb2, 0x71, 0x64, 0xda, 0x8b, 0xf8, 0xeb, 0x0f, 0x4b, 0x70, 0x56, 0x9d, 0x35,
        0x1e, 0x24, 0x0e, 0x5e, 0x63, 0x58, 0xd1, 0xa2, 0x25, 0x22, 0x7c, 0x3b, 0x01, 0x21, 0x78, 0x87,
        0xd4, 0x00, 0x46, 0x57, 0x9f, 0xd3, 0x27, 0x52, 0x4c, 0x36, 0x02, 0xe7, 0xa0, 0xc4, 0xc8, 0x9e,
        0xea, 0xbf, 0x8a, 0xd2, 0x40, 0xc7, 0x38, 0xb5, 0xa3, 0xf7, 0xf2, 0xce, 0xf9, 0x61, 0x15, 0xa1,
        0xe0, 0xae, 0x5d, 0xa4, 0x9b, 0x34, 0x1a, 0x55, 0xad, 0x93, 0x32, 0x30, 0xf5, 0x8c, 0xb1, 0xe3,
        0x1d, 0xf6, 0xe2, 0x2e, 0x82, 0x66, 0xca, 0x60, 0xc0, 0x29, 0x23, 0xab, 0x0d, 0x53, 0x4e, 0x6f,
        0xd5, 0xdb, 0x37, 0x45, 0xde, 0xfd, 0x8e, 0x2f, 0x03, 0xff, 0x6a, 0x72, 0x6d, 0x6c, 0x5b, 0x51,
        0x8d, 0x1b, 0xaf, 0x92, 0xbb, 0xdd, 0xbc, 0x7f, 0x11, 0xd9, 0x5c, 0x41, 0x1f, 0x10, 0x5a, 0xd8,
        0x0a, 0xc1, 0x31, 0x88, 0xa5, 0xcd, 0x7b, 0xbd, 0x2d, 0x74, 0xd0, 0x12, 0xb8, 0xe5, 0xb4, 0xb0,
        0x89, 0x69, 0x97, 0x4a, 0x0c, 0x96, 0x77, 0x7e, 0x65, 0xb9, 0xf1, 0x09, 0xc5, 0x6e, 0xc6, 0x84,
        0x18, 0xf0, 0x7d, 0xec, 0x3a, 0xdc, 0x4d, 0x20, 0x79, 0xee, 0x5f, 0x3e, 0xd7, 0xcb, 0x39, 0x48,
    };

    /// <summary>系统参数 FK，用于密钥扩展的初始异或。</summary>
    private static readonly uint[] Fk = { 0xa3b1bac6, 0x56aa3350, 0x677d9197, 0xb27022dc };

    /// <summary>固定参数 CK，32 轮各一个。</summary>
    private static readonly uint[] Ck =
    {
        0x00070e15, 0x1c232a31, 0x383f464d, 0x545b6269,
        0x70777e85, 0x8c939aa1, 0xa8afb6bd, 0xc4cbd2d9,
        0xe0e7eef5, 0xfc030a11, 0x181f262d, 0x343b4249,
        0x50575e65, 0x6c737a81, 0x888f969d, 0xa4abb2b9,
        0xc0c7ced5, 0xdce3eaf1, 0xf8ff060d, 0x141b2229,
        0x30373e45, 0x4c535a61, 0x686f767d, 0x848b9299,
        0xa0a7aeb5, 0xbcc3cad1, 0xd8dfe6ed, 0xf4fb0209,
        0x10171e25, 0x2c333a41, 0x484f565d, 0x646b7279,
    };

    private readonly uint[] _roundKeys = new uint[32];

    /// <param name="key">128 位密钥（16 字节）。</param>
    public Sm4Engine(ReadOnlySpan<byte> key)
    {
        if (key.Length != BlockSize)
            throw new ArgumentException($"SM4 密钥必须是 {BlockSize} 字节，实际 {key.Length}", nameof(key));
        ExpandKey(key);
    }

    /// <summary>加密单个分组。</summary>
    public void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output) =>
        ProcessBlock(input, output, encrypt: true);

    /// <summary>解密单个分组。SM4 解密即把轮密钥逆序使用。</summary>
    public void DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output) =>
        ProcessBlock(input, output, encrypt: false);

    private void ProcessBlock(ReadOnlySpan<byte> input, Span<byte> output, bool encrypt)
    {
        if (input.Length != BlockSize || output.Length != BlockSize)
            throw new ArgumentException($"SM4 分组必须是 {BlockSize} 字节");

        var x0 = ReadBigEndian(input, 0);
        var x1 = ReadBigEndian(input, 4);
        var x2 = ReadBigEndian(input, 8);
        var x3 = ReadBigEndian(input, 12);

        for (var i = 0; i < 32; i++)
        {
            var rk = _roundKeys[encrypt ? i : 31 - i];
            var next = x0 ^ TransformT(x1 ^ x2 ^ x3 ^ rk);
            x0 = x1;
            x1 = x2;
            x2 = x3;
            x3 = next;
        }

        // 反序变换 R
        WriteBigEndian(output, 0, x3);
        WriteBigEndian(output, 4, x2);
        WriteBigEndian(output, 8, x1);
        WriteBigEndian(output, 12, x0);
    }

    private void ExpandKey(ReadOnlySpan<byte> key)
    {
        var k0 = ReadBigEndian(key, 0) ^ Fk[0];
        var k1 = ReadBigEndian(key, 4) ^ Fk[1];
        var k2 = ReadBigEndian(key, 8) ^ Fk[2];
        var k3 = ReadBigEndian(key, 12) ^ Fk[3];

        for (var i = 0; i < 32; i++)
        {
            var next = k0 ^ TransformTPrime(k1 ^ k2 ^ k3 ^ Ck[i]);
            _roundKeys[i] = next;
            k0 = k1;
            k1 = k2;
            k2 = k3;
            k3 = next;
        }
    }

    /// <summary>合成置换 T = L(τ(·))，用于轮函数。</summary>
    private static uint TransformT(uint a)
    {
        var b = SubstituteBytes(a);
        return b ^ RotateLeft(b, 2) ^ RotateLeft(b, 10) ^ RotateLeft(b, 18) ^ RotateLeft(b, 24);
    }

    /// <summary>合成置换 T' = L'(τ(·))，用于密钥扩展，线性部分与 T 不同。</summary>
    private static uint TransformTPrime(uint a)
    {
        var b = SubstituteBytes(a);
        return b ^ RotateLeft(b, 13) ^ RotateLeft(b, 23);
    }

    /// <summary>非线性变换 τ：逐字节过 S 盒。</summary>
    private static uint SubstituteBytes(uint a) =>
        ((uint)Sbox[(a >> 24) & 0xFF] << 24)
        | ((uint)Sbox[(a >> 16) & 0xFF] << 16)
        | ((uint)Sbox[(a >> 8) & 0xFF] << 8)
        | Sbox[a & 0xFF];

    private static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));

    private static uint ReadBigEndian(ReadOnlySpan<byte> src, int offset) =>
        ((uint)src[offset] << 24) | ((uint)src[offset + 1] << 16)
        | ((uint)src[offset + 2] << 8) | src[offset + 3];

    private static void WriteBigEndian(Span<byte> dst, int offset, uint value)
    {
        dst[offset] = (byte)(value >> 24);
        dst[offset + 1] = (byte)(value >> 16);
        dst[offset + 2] = (byte)(value >> 8);
        dst[offset + 3] = (byte)value;
    }
}
