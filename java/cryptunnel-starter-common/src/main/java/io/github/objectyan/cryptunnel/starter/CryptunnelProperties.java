package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.crypto.AesCbcHmacSha256Cipher;
import java.util.LinkedHashMap;
import java.util.Map;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.boot.context.properties.NestedConfigurationProperty;

/**
 * cryptunnel-starter 顶层配置。
 *
 * <p>支持两种形态：</p>
 * <ol>
 *   <li><b>新形态（推荐）</b>：{@code targets.<name>.{...}} 多 target 配置</li>
 *   <li><b>老平铺（兼容）</b>：{@code mysql-host / aes-key / auth-key} 等顶层字段
 *       启动期自动包装为名为 {@code default} 的 target</li>
 * </ol>
 */
@ConfigurationProperties(prefix = "cryptunnel")
public class CryptunnelProperties {

    private boolean enabled = false;
    private String wsPath = "/ws-cryptunnel";
    private String defaultTarget;
    private int authTimeWindow = 300;

    /**
     * 隧道加密算法标识。
     *
     * <p>服务端同一时刻只允许启用一个算法（ADR-0003）。该标识<b>不会</b>进入传输报文，
     * 仅用于本地配置匹配——客户端与服务端需配置成相同值。</p>
     */
    private String cipher = AesCbcHmacSha256Cipher.ID;

    @NestedConfigurationProperty
    private Map<String, TargetConfig> targets = new LinkedHashMap<String, TargetConfig>();

    // 老平铺字段
    private String mysqlHost = "127.0.0.1";
    private int mysqlPort = 3306;
    private String aesKey = "default-key-please-change";
    private String authKey = "default-auth-key-please-change";
    private int maxConnections = 5;
    private int connectTimeout = 10000;
    private int readTimeout = 0;

    public boolean isEnabled() { return enabled; }
    public void setEnabled(boolean enabled) { this.enabled = enabled; }

    public String getWsPath() { return wsPath; }
    public void setWsPath(String wsPath) { this.wsPath = wsPath; }

    public String getDefaultTarget() { return defaultTarget; }
    public void setDefaultTarget(String defaultTarget) { this.defaultTarget = defaultTarget; }

    public int getAuthTimeWindow() { return authTimeWindow; }
    public void setAuthTimeWindow(int authTimeWindow) { this.authTimeWindow = authTimeWindow; }

    public String getCipher() { return cipher; }
    public void setCipher(String cipher) { this.cipher = cipher; }

    public Map<String, TargetConfig> getTargets() { return targets; }
    public void setTargets(Map<String, TargetConfig> targets) { this.targets = targets; }

    public String getMysqlHost() { return mysqlHost; }
    public void setMysqlHost(String mysqlHost) { this.mysqlHost = mysqlHost; }

    public int getMysqlPort() { return mysqlPort; }
    public void setMysqlPort(int mysqlPort) { this.mysqlPort = mysqlPort; }

    public String getAesKey() { return aesKey; }
    public void setAesKey(String aesKey) { this.aesKey = aesKey; }

    public String getAuthKey() { return authKey; }
    public void setAuthKey(String authKey) { this.authKey = authKey; }

    public int getMaxConnections() { return maxConnections; }
    public void setMaxConnections(int maxConnections) { this.maxConnections = maxConnections; }

    public int getConnectTimeout() { return connectTimeout; }
    public void setConnectTimeout(int connectTimeout) { this.connectTimeout = connectTimeout; }

    public int getReadTimeout() { return readTimeout; }
    public void setReadTimeout(int readTimeout) { this.readTimeout = readTimeout; }

    /**
     * 单个 target 的配置项。
     */
    public static class TargetConfig {
        private String displayName;
        private String mysqlHost;
        private int mysqlPort = 3306;
        private String aesKey;
        private String authKey;
        private int maxConnections = 5;
        private int connectTimeout = 10000;
        private int readTimeout = 300000;

        public String getDisplayName() { return displayName; }
        public void setDisplayName(String displayName) { this.displayName = displayName; }

        public String getMysqlHost() { return mysqlHost; }
        public void setMysqlHost(String mysqlHost) { this.mysqlHost = mysqlHost; }

        public int getMysqlPort() { return mysqlPort; }
        public void setMysqlPort(int mysqlPort) { this.mysqlPort = mysqlPort; }

        public String getAesKey() { return aesKey; }
        public void setAesKey(String aesKey) { this.aesKey = aesKey; }

        public String getAuthKey() { return authKey; }
        public void setAuthKey(String authKey) { this.authKey = authKey; }

        public int getMaxConnections() { return maxConnections; }
        public void setMaxConnections(int maxConnections) { this.maxConnections = maxConnections; }

        public int getConnectTimeout() { return connectTimeout; }
        public void setConnectTimeout(int connectTimeout) { this.connectTimeout = connectTimeout; }

        public int getReadTimeout() { return readTimeout; }
        public void setReadTimeout(int readTimeout) { this.readTimeout = readTimeout; }
    }
}
