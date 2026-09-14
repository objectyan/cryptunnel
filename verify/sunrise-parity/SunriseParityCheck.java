/*
 * SunriseParityCheck —— 改名后 core 与老服务端 AesUtil 的字节级互通验证（本地，不联网、不发布）。
 *
 * 目的：在动 sunrise 依赖 / 动 Maven Central 之前，先证明新包 cryptunnel-core 与
 * 老服务端 com.crm.sunrise.jdbcproxy.AesUtil 字节级兼容：
 *   - 老服务端加密的密文，新 core 能解开；新 core 加密的密文，老服务端能解开（CBC 双向）。
 *   - 新 core 能解开真实 .NET 客户端的固化密文向量（CBC/GCM/SM4 三算法）。
 *   - 老服务端也能直接解开真实 .NET 客户端的 CBC 密文（证明 sunrise 不改动也能连 .NET 客户端）。
 *
 * 红线：本程序只读引用 sunrize 的 AesUtil.class（见 ./lib 下的字节级副本），
 *      绝不修改 D:\Sunrise\Coding\CRM\sunrise\src\main\java\com\crm\sunrise\jdbcproxy\ 下任何源文件。
 *
 * 运行（JDK 8）：见本目录 README 注释或 run.sh。
 */

import com.crm.sunrise.jdbcproxy.AesUtil; // 老服务端基准（只读副本，未改动）
import io.github.objectyan.cryptunnel.core.crypto.AesCbcHmacSha256Cipher;
import io.github.objectyan.cryptunnel.core.crypto.AesGcmCipher;
import io.github.objectyan.cryptunnel.core.crypto.Sm4Cipher;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

public final class SunriseParityCheck {

    static final java.nio.charset.Charset UTF8 = StandardCharsets.UTF_8;

    // ---- .NET 客户端固化向量（来自 sunrise/target/test-classes/jdbcproxy/dotnet-parity-vectors.txt）----
    static final String RAW_KEY = "SxRise2026EncryptSecret";
    static final String PLAIN = "AUTH:TestAuthKey:1700000000:0123456789abcdef0123456789abcdef";
    static final String DOTNET_CBC = "K7GWnXPuIYe8xqTQmFt0grRzZ+HZlcLPVp2DhRLYlE8VYUHAJiNWkiUyQDszpqSOas3TbBzsrXsVeRY97kN+A203L5035AaFTMA0u6DMmQk39sXg9sMzv3UjfsMuj6pq8Pio5m7eWqiAuyM9f06pfQ==";
    static final String DOTNET_GCM = "UZ4VAbX16tr3IXCVSbik726yyYN2GHTDhmzT7BP9q0x11PXcjBhjrx5k7B2IHtuXMzC4jww6t+bj6dk6gGqTyUYwPwsZP+AjQCE7KGsw2C8TXP2uJGhN4A==";
    static final String DOTNET_SM4 = "RNSiQQ3HKXgsKiVGrAXW6kjhgjjkghvn1EnCrG82bDNxuZ+JLqV+IrH1Ru79wF0A+KBbhvaxrFHPyubKwMSZzEDUqJgObwh2VlU2WSR0R9K3MOIpQgKkjPYk5mHBbgyO6O5qWj5bFs9Ix/hmSmgS3g==";

    // ---- 被测实现（新包）----
    static final AesCbcHmacSha256Cipher CBC = new AesCbcHmacSha256Cipher();
    static final AesGcmCipher GCM = new AesGcmCipher();
    static final Sm4Cipher SM4 = new Sm4Cipher();

    static int pass = 0;
    static int fail = 0;
    static final List<String> failures = new ArrayList<>();

    public static void main(String[] args) {
        byte[][] plaintexts = new byte[][]{
                PLAIN.getBytes(UTF8),
                new byte[]{0x00},                                            // 1 字节
                randomBytes(100),                                           // 多块二进制
                "中文混合汉字与English mixed 内容，再加一段长文本凑成多块。".getBytes(UTF8),
                new byte[0]                                                  // 空明文
        };
        String[] keys = new String[]{
                RAW_KEY,
                "short",
                "a-very-long-shared-secret-key-0123456789ABCDEF"
        };

        // ============ Group 1: CBC 双向互通（sunrise 基准 <-> core 新实现）============
        for (byte[] p : plaintexts) {
            for (String k : keys) {
                // 1a: 老服务端加密 -> 新 core 解密
                String ct = AesUtil.encrypt(p, AesUtil.deriveAesKey(k), AesUtil.deriveHmacKey(k));
                check("sunrise.encrypt -> core.open  [" + plen(p) + "/" + klen(k) + "]",
                        Arrays.equals(p, CBC.open(ct, k)));
                // 1b: 新 core 加密 -> 老服务端解密
                String ct2 = CBC.seal(p, k);
                check("core.seal -> sunrise.decrypt [" + plen(p) + "/" + klen(k) + "]",
                        Arrays.equals(p, AesUtil.decrypt(ct2, AesUtil.deriveAesKey(k), AesUtil.deriveHmacKey(k))));
            }
        }

        // ============ Group 2: .NET 真实客户端固化向量（第三方基准）============
        check(".NET CBC -> core.open（新包解真实客户端）",
                Arrays.equals(PLAIN.getBytes(UTF8), CBC.open(DOTNET_CBC, RAW_KEY)));
        check(".NET CBC -> sunrise.decrypt（老服务端解真实客户端，证明 sunrise 不改动也能连）",
                Arrays.equals(PLAIN.getBytes(UTF8),
                        AesUtil.decrypt(DOTNET_CBC, AesUtil.deriveAesKey(RAW_KEY), AesUtil.deriveHmacKey(RAW_KEY))));
        check(".NET GCM -> core.open",
                Arrays.equals(PLAIN.getBytes(UTF8), GCM.open(DOTNET_GCM, RAW_KEY)));
        check(".NET SM4 -> core.open",
                Arrays.equals(PLAIN.getBytes(UTF8), SM4.open(DOTNET_SM4, RAW_KEY)));

        // ============ Group 3: core 自身 round-trip 一致性（健全性）============
        for (byte[] p : plaintexts) {
            check("core CBC round-trip [" + plen(p) + "]",
                    Arrays.equals(p, CBC.open(CBC.seal(p, RAW_KEY), RAW_KEY)));
        }

        // ============ Group 4: 认证必须生效（错误密钥 / 篡改必须抛异常，而非静默解出错值）============
        check("错误密钥解 .NET CBC 必须失败（不静默解出）",
                throwsOn(() -> CBC.open(DOTNET_CBC, "totally-wrong-key")));
        check("篡改 CBC 载荷必须 HMAC 失败",
                throwsOn(() -> {
                    StringBuilder sb = new StringBuilder(DOTNET_CBC);
                    int idx = 20;                       // 改写 body 中一个 base64 字符
                    char c = sb.charAt(idx);
                    sb.setCharAt(idx, c == 'A' ? 'B' : 'A');
                    CBC.open(sb.toString(), RAW_KEY);
                }));
        check("SM4 错误密钥必须失败",
                throwsOn(() -> SM4.open(DOTNET_SM4, "wrong-key")));

        // ============ 报告（具体项数，不 SKIP）============
        System.out.println();
        System.out.println("==================================================");
        System.out.println("  PASS = " + pass + "    FAIL = " + fail + "    TOTAL = " + (pass + fail));
        System.out.println("==================================================");
        if (fail > 0) {
            System.out.println("FAILURES:");
            for (String f : failures) System.out.println("  - " + f);
            System.exit(1);
        }
        System.out.println("ALL ASSERTIONS PASSED —— 新 cryptunnel-core 与老 sunrise.AesUtil 字节级兼容");
    }

    static void check(String name, boolean ok) {
        if (ok) {
            pass++;
            System.out.println("  [PASS] " + name);
        } else {
            fail++;
            failures.add(name);
            System.out.println("  [FAIL] " + name);
        }
    }

    static boolean throwsOn(Throwing r) {
        try {
            r.run();
            return false; // 没抛异常 = 认证失效 = 失败
        } catch (Exception e) {
            return true;  // 抛异常 = 认证生效 = 通过
        }
    }

    interface Throwing {
        void run() throws Exception;
    }

    static byte[] randomBytes(int n) {
        byte[] b = new byte[n];
        new java.security.SecureRandom().nextBytes(b);
        return b;
    }

    static String plen(byte[] b) {
        return b.length + "B";
    }

    static String klen(String s) {
        return s.length() + "ch";
    }
}
