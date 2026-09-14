using System;
using System.Collections.Generic;
using System.Linq;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// <see cref="ITunnelCipher"/> 注册表，与 Java 侧 <c>TunnelCiphers</c> 对应。
///
/// <b>注意</b>：本注册表只做「标识 -> 实现」的本地映射，与线上协商无关——
/// 按 ADR-0003，报文内不含任何算法标识，两端靠配置约定保持一致。
/// 因此配置写错算法时<b>无法自动纠正</b>，只会表现为服务端
/// <c>Auth decrypt failed</c>，诊断提示必须同时提醒 cipher 与 aesKey 两种可能。
/// </summary>
public static class CipherRegistry
{
    private static readonly Dictionary<string, ITunnelCipher> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缺省算法，与历史版本完全兼容。</summary>
    public static ITunnelCipher Default { get; } = new AesCbcHmacSha256Cipher();

    static CipherRegistry()
    {
        // 别名严格对齐 Java 侧 TunnelCiphers：只有 SM4 声明了别名（sm4），
        // AES-CBC 与 AES-GCM 的 aliases() 返回空集合。
        //
        // 这里刻意不给 CBC/GCM 补 "aes-cbc"/"aes-gcm" 之类的顺手别名——
        // 按 ADR-0003，报文内不含算法标识，两端完全靠配置字符串约定对齐。
        // 客户端若接受一个服务端不认的写法，结果是服务端解密失败并回
        // close reason "Auth decrypt failed"，而该错误无法区分
        // 「算法配错」与「aesKey 配错」，是全链路最难诊断的一种失败。
        // 客户端的可接受集合必须是服务端的子集，宁可让用户在本地就被拒。
        Register(Default);
        Register(new AesGcmCipher());
        Register(new Sm4Cipher(), Sm4Cipher.Alias);
    }

    private static void Register(ITunnelCipher cipher, string? alias = null)
    {
        Registry[cipher.Id] = cipher;
        if (alias is not null)
            Registry[alias] = cipher;
    }

    /// <summary>已注册的全部标识与别名。</summary>
    public static IReadOnlyCollection<string> Ids => Registry.Keys.ToList();

    /// <summary>是否注册了指定标识（或别名）。</summary>
    public static bool Contains(string? id) => id is not null && Registry.ContainsKey(id);

    /// <summary>
    /// 按标识获取实现。传入空值时返回缺省算法，便于配置项留空。
    /// </summary>
    /// <exception cref="ArgumentException">标识未注册。</exception>
    public static ITunnelCipher Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Default;
        if (Registry.TryGetValue(id, out var cipher))
            return cipher;
        throw new ArgumentException(
            $"未知的加密算法 '{id}'，可用：{string.Join(", ", Ids)}", nameof(id));
    }

    /// <summary>
    /// 把别名归一为正式标识，供配置校验与 YAML 回写使用。
    /// 传入空值返回缺省算法的标识。
    /// </summary>
    /// <exception cref="ArgumentException">标识未注册。</exception>
    public static string Normalize(string? id) => Get(id).Id;
}
