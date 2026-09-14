import io.github.objectyan.proxy.AesUtil;

import javax.crypto.spec.SecretKeySpec;
import java.nio.charset.StandardCharsets;

/**
 * 用「原始 Java 版 AesUtil」生成基准向量，供 C# 版做字节级对齐校验。
 *
 * 因为 AES-CBC 带随机 IV，密文每次都不同，所以不能直接比密文字符串。
 * 校验分三步：
 *   1. 密钥派生是确定性的 —— AES_KEY / HMAC_KEY 的十六进制必须完全一致（最强校验）
 *   2. Java 加密 -> C# 能解开，且明文一致
 *   3. C# 加密 -> Java 能解开，且明文一致
 *
 * 编译运行：
 *   javac -encoding UTF-8 -cp <老Java源码目录> -d out ParityCheck.java
 *   java -Dfile.encoding=UTF-8 -cp <老Java源码目录>;out ParityCheck
 */
public class ParityCheck {

    private static final String RAW_KEY = "SxRise2026EncryptSecret";
    private static final String PLAIN =
            "AUTH:TestAuthKey:1700000000:0123456789abcdef0123456789abcdef";

    public static void main(String[] args) {
        SecretKeySpec aesKey = AesUtil.deriveAesKey(RAW_KEY);
        SecretKeySpec hmacKey = AesUtil.deriveHmacKey(RAW_KEY);

        System.out.println("RAW_KEY=" + RAW_KEY);
        System.out.println("PLAIN=" + PLAIN);
        System.out.println("AES_KEY=" + toHex(aesKey.getEncoded()));
        System.out.println("HMAC_KEY=" + toHex(hmacKey.getEncoded()));
        System.out.println("PLAIN_B64=" +
                java.util.Base64.getEncoder().encodeToString(PLAIN.getBytes(StandardCharsets.UTF_8)));

        // 固定在文件里的密文：C# 侧用它验证「Java 加密 -> C# 解密」
        String enc = AesUtil.encrypt(PLAIN.getBytes(StandardCharsets.UTF_8), aesKey, hmacKey);
        System.out.println("JAVA_CIPHER=" + enc);

        byte[] back = AesUtil.decrypt(enc, aesKey, hmacKey);
        System.out.println("JAVA_ROUNDTRIP=" + new String(back, StandardCharsets.UTF_8));

        // 篡改检测：翻转密文最后一个字节，应当抛异常
        try {
            byte[] raw = java.util.Base64.getDecoder().decode(enc);
            raw[raw.length - 1] ^= 0x01;
            AesUtil.decrypt(java.util.Base64.getEncoder().encodeToString(raw), aesKey, hmacKey);
            System.out.println("TAMPER=NOT_DETECTED");
        } catch (Exception e) {
            System.out.println("TAMPER=DETECTED");
        }
    }

    private static String toHex(byte[] b) {
        StringBuilder sb = new StringBuilder();
        for (byte x : b) sb.append(String.format("%02x", x));
        return sb.toString();
    }
}
