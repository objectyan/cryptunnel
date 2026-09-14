import io.github.objectyan.cryptunnel.core.crypto.AesCbcHmacSha256Cipher;
import io.github.objectyan.cryptunnel.core.crypto.AesGcmCipher;
import io.github.objectyan.cryptunnel.core.crypto.Sm4Cipher;
import io.github.objectyan.cryptunnel.core.crypto.TunnelCipher;
import io.github.objectyan.cryptunnel.core.crypto.TunnelCiphers;

import java.io.BufferedReader;
import java.io.FileReader;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

/**
 * 反方向跨语言对齐校验：解开 <b>C# 侧</b>加密的载荷。
 *
 * <p>输入是 CryptoParity --export 的输出（KEY=VALUE 文本）。
 * 与 CryptoParity 的「[3] 解开 Java 侧真实载荷」合起来，
 * 才构成 Java ↔ .NET 双向字节级对齐的完整证明。</p>
 *
 * <p>用法：{@code java -cp <fatjar>;. ReverseParityCheck <export.txt>}</p>
 */
public final class ReverseParityCheck {

    private static int passed = 0;
    private static int failed = 0;

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("用法: ReverseParityCheck <dotnet-export.txt>");
            System.exit(2);
        }

        Map<String, String> v = load(args[0]);
        String rawKey = v.get("RAW_KEY");
        String expected = v.get("PLAIN");

        System.out.println("=== 反方向对齐校验（C# 加密 -> Java 解密）===");

        check("aes-256-cbc-hmac-sha256", new AesCbcHmacSha256Cipher(),
                v.get("DOTNET_CBC_PAYLOAD"), rawKey, expected);
        check("aes-256-gcm", new AesGcmCipher(),
                v.get("DOTNET_GCM_PAYLOAD"), rawKey, expected);
        check("sm4-cbc-hmac-sha256", TunnelCiphers.get(Sm4Cipher.ID),
                v.get("DOTNET_SM4_PAYLOAD"), rawKey, expected);

        System.out.println();
        System.out.println("=== 通过 " + passed + " 项，失败 " + failed + " 项 ===");
        System.exit(failed == 0 ? 0 : 1);
    }

    private static void check(String name, TunnelCipher cipher,
                              String payload, String rawKey, String expected) {
        if (payload == null) {
            failed++;
            System.out.println("  [FAIL] " + name + " -> 导出文件缺少对应载荷");
            return;
        }
        try {
            String actual = new String(cipher.open(payload, rawKey), StandardCharsets.UTF_8);
            if (expected.equals(actual)) {
                passed++;
                System.out.println("  [PASS] " + name);
            } else {
                failed++;
                System.out.println("  [FAIL] " + name + " -> 明文不匹配: " + actual);
            }
        } catch (Exception e) {
            failed++;
            System.out.println("  [FAIL] " + name + " -> " + e.getClass().getSimpleName()
                    + ": " + e.getMessage());
        }
    }

    private static Map<String, String> load(String path) throws Exception {
        Map<String, String> map = new HashMap<String, String>();
        BufferedReader reader = new BufferedReader(new FileReader(path));
        try {
            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) {
                    continue;
                }
                int idx = line.indexOf('=');
                if (idx > 0) {
                    map.put(line.substring(0, idx).trim(), line.substring(idx + 1).trim());
                }
            }
        } finally {
            reader.close();
        }
        return map;
    }
}
