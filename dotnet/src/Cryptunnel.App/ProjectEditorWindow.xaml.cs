using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Models;

namespace Cryptunnel.App;

/// <summary>新建 / 编辑项目配置。返回 Result = 已校验的 ProjectFile。</summary>
public partial class ProjectEditorWindow : Window
{
    private static readonly Regex NameRe = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    /// <summary>默认监听地址：仅本机可连，与 <c>ConfigLoader</c> 的缺省值保持一致。</summary>
    private const string DefaultAddress = "127.0.0.1";

    /// <summary>
    /// 下拉框条目：<see cref="Id"/> 是写进 YAML 的规范标识，<see cref="Label"/> 只给人看。
    ///
    /// <para>不用裸字符串做条目，是为了让界面能显示「AES-256-GCM（需服务端同步配置）」
    /// 这类带说明的文案，同时保证落盘的仍是 <c>CipherRegistry</c> 认的规范 Id ——
    /// 展示文案与协议值必须物理分离，否则改文案就会改坏配置。</para>
    /// </summary>
    private sealed record CipherChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private bool _uiReady;

    /// <summary>
    /// 原文件中界面未覆盖的字段，保存时原样带回。
    ///
    /// <para>编辑器保存时构造的是一个<b>全新</b>的 <see cref="ProjectFile"/>，
    /// 界面上没有的字段一律为 null，回写就会把它们从 yaml 里抹掉。
    /// 用户手写的 <c>health</c> 段会因为「改了个端口号」而消失，且毫无提示 ——
    /// 这类静默的数据丢失比报错难查得多。</para>
    /// </summary>
    private readonly HealthSection? _preservedHealth;

    public ProjectFile? Result { get; private set; }

    /// <param name="existing">编辑时传入；新建传 null。</param>
    public ProjectEditorWindow(ProjectFile? existing)
    {
        InitializeComponent();
        PopulateCiphers();
        PopulateAddresses();
        _preservedHealth = existing?.Health;
        if (existing != null)
        {
            Title = $"编辑项目 — {existing.Name}";
            NameBox.Text = existing.Name;
            NameBox.IsEnabled = false;          // 名称作为主键，编辑不可改名
            DisplayBox.Text = existing.DisplayName ?? "";
            UrlBox.Text = existing.ServerUrl ?? "";
            WsPathBox.Text = existing.WsPath ?? "/ws-cryptunnel";
            PortBox.Text = existing.Local?.Port?.ToString() ?? "";
            AddressBox.Text = existing.Local?.Address ?? DefaultAddress;
            AllowNonLoopbackBox.IsChecked = existing.Local?.AllowNonLoopback == true;
            AesBox.Text = existing.AesKey ?? "";
            AuthBox.Text = existing.AuthKey ?? "";
            SelectCipher(existing.Cipher);
            EnabledBox.IsChecked = existing.Enabled ?? true;
        }
        else
        {
            AddressBox.Text = DefaultAddress;
            AllowNonLoopbackBox.IsChecked = false;
            SelectCipher(null);
            EnabledBox.IsChecked = true;
        }

        // 放在最后：XAML 里的默认值与上面的回填都会触发 SelectionChanged，
        // 而那时 CipherHint 未必已构造完。用一个显式开关代替「事件里到处判空」。
        _uiReady = true;
        UpdateCipherHint();
        UpdateAddressHint();
    }

    /// <summary>
    /// 填充监听地址下拉。可编辑（<c>IsEditable</c>），下拉项只是常用值的快捷方式 ——
    /// 用户仍可手填具体网卡 IP，那同样属于非回环，需要勾选授权。
    /// </summary>
    private void PopulateAddresses()
    {
        AddressBox.ItemsSource = new[] { DefaultAddress, "0.0.0.0" };
    }

    private void AddressBox_Changed(object sender, EventArgs e) => UpdateAddressHint();
    private void AllowNonLoopbackBox_Changed(object sender, RoutedEventArgs e) => UpdateAddressHint();

    /// <summary>
    /// 地址与授权开关的组合提示，三种状态各自不同：
    /// 回环（安全，无需授权）／非回环但未授权（保存后启动会被拒）／非回环且已授权（风险确认）。
    ///
    /// <para><b>未授权时必须写明后果是「启动被拒绝」而不只是「不安全」</b>。
    /// 校验发生在 ConfigLoader，用户在这个窗口里点保存是不会失败的 ——
    /// 如果这里只说「不安全」，用户会以为保存成功即可用，直到隧道起不来才困惑。</para>
    /// </summary>
    private void UpdateAddressHint()
    {
        if (!_uiReady) return;

        var addr = (AddressBox.Text ?? "").Trim();
        var allowed = AllowNonLoopbackBox.IsChecked == true;
        var loopback = IsLoopbackText(addr);

        if (loopback)
        {
            AddressHint.Text = "仅本机可连接，是推荐的默认值。此时无需勾选上方授权。";
            AddressHint.Foreground = (Brush)FindResource("Tx3");
        }
        else if (!allowed)
        {
            AddressHint.Text = $"⚠ “{addr}” 不是回环地址，但尚未勾选授权 —— "
                             + "保存后隧道启动会被拒绝。若确需跨机访问，请勾选上方授权；否则请改回 127.0.0.1。";
            AddressHint.Foreground = (Brush)FindResource("Danger");
        }
        else
        {
            AddressHint.Text = "⚠ 局域网内其它机器可连接本机端口并访问内网数据库，"
                             + "且对方无需知道 AES / 认证密钥（本客户端会用你配置的密钥替对方完成认证）。"
                             + "请确认所在网络可信。";
            AddressHint.Foreground = (Brush)FindResource("Accent");
        }
    }

    /// <summary>
    /// 界面侧的回环判定。
    ///
    /// <para>与 Core 的 <c>ListenAddressPolicy.IsLoopback</c> 规则一致，但<b>刻意不复用</b>：
    /// 那是 <c>internal</c>，为了给界面提示用而放宽成 <c>public</c>，等于让一个纯展示需求
    /// 撬开核心层的封装。这里只影响提示文案的颜色与措辞，真正的闸门在 ConfigLoader ——
    /// 即使这段判断有偏差，也不会让一个不该放行的配置通过。</para>
    /// </summary>
    private static bool IsLoopbackText(string? address)
    {
        var addr = address?.Trim();
        if (string.IsNullOrEmpty(addr)) return false;
        if (addr is "0.0.0.0" or "::" or "*" or "+" or "[::]") return false;
        return System.Net.IPAddress.TryParse(addr.Trim('[', ']'), out var ip)
               && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// 填充算法下拉框。只列<b>规范标识</b>，不列别名。
    ///
    /// <para><c>sm4</c> 是 <c>sm4-cbc-hmac-sha256</c> 的合法别名，解析时会被接受，
    /// 但界面上同时列出两条等价条目只会让人怀疑它们有区别。
    /// 别名的意义是兼容手写配置，不是给界面用的。</para>
    /// </summary>
    private void PopulateCiphers()
    {
        var defaultId = CipherRegistry.Default.Id;
        var canonical = CipherRegistry.Ids
            .Where(id => CipherRegistry.Normalize(id) == id)   // 滤掉别名
            .OrderBy(id => id == defaultId ? 0 : 1)            // 默认项排最前
            .ThenBy(id => id, StringComparer.Ordinal)
            .Select(id => new CipherChoice(id, id == defaultId ? $"{id}（默认）" : id))
            .ToList();

        CipherBox.ItemsSource = canonical;
    }

    private void SelectCipher(string? configured)
    {
        var items = (System.Collections.Generic.IReadOnlyList<CipherChoice>)CipherBox.ItemsSource;
        var wanted = CipherRegistry.Contains(configured?.Trim())
            ? CipherRegistry.Normalize(configured!.Trim())
            : CipherRegistry.Default.Id;

        // 配置里写了个注册表不认的值时（比如手改 YAML 手滑），回落到默认项而不是留空。
        // 留空会让「保存」把一个空 cipher 写回去，等于静默把用户的错误配置洗掉，
        // 反而掩盖了问题；回落到默认至少是个明确、可用、与老行为一致的状态。
        var idx = 0;
        for (var i = 0; i < items.Count; i++)
            if (items[i].Id == wanted) { idx = i; break; }
        CipherBox.SelectedIndex = idx;
    }

    private void CipherBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCipherHint();

    /// <summary>
    /// 非默认算法时给出显式提示。
    ///
    /// <para>选错算法的代价不对称：服务端只会回一句 <c>Auth decrypt failed</c>，
    /// 而这条 reason 与 aesKey 配错完全无法区分。所以在用户还在编辑框里的时候
    /// 就把「你得同时改服务端」摆出来，比事后翻日志便宜得多。</para>
    /// </summary>
    private void UpdateCipherHint()
    {
        if (!_uiReady) return;
        var chosen = (CipherBox.SelectedItem as CipherChoice)?.Id ?? CipherRegistry.Default.Id;
        if (chosen == CipherRegistry.Default.Id)
        {
            CipherHint.Text = "默认算法，与现有服务端部署兼容，配置文件中不会写入该项。";
            CipherHint.Foreground = (Brush)FindResource("Tx3");
        }
        else
        {
            CipherHint.Text = $"⚠ 服务端必须同步配置 cryptunnel.cipher: {chosen} —— "
                            + "两端不一致时报文无法解密，服务端只会返回 “Auth decrypt failed”，"
                            + "该提示与密钥错误无法区分。";
            CipherHint.Foreground = (Brush)FindResource("Accent");
        }
    }

    private void BtnGenAes_Click(object sender, RoutedEventArgs e) => AesBox.Text = NewKey();
    private void BtnGenAuth_Click(object sender, RoutedEventArgs e) => AuthBox.Text = NewKey();
    private void BtnCopyAes_Click(object sender, RoutedEventArgs e) => CopyToClipboard(AesBox.Text);
    private void BtnCopyAuth_Click(object sender, RoutedEventArgs e) => CopyToClipboard(AuthBox.Text);

    private static string NewKey()
    {
        var b = new byte[32];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b);
    }

    private static void CopyToClipboard(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        try { Clipboard.SetText(s); } catch { /* 剪贴板被其它进程占用时静默 */ }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCollect(out var pf, out var err))
        {
            ShowError(err);
            return;
        }
        Result = pf;
        DialogResult = true;
        Close();
    }

    private bool TryCollect(out ProjectFile pf, out string error)
    {
        pf = null!;
        var name = NameBox.Text.Trim();
        var display = DisplayBox.Text.Trim();
        var url = UrlBox.Text.Trim().TrimEnd('/');
        var wsPath = WsPathBox.Text.Trim();
        if (string.IsNullOrEmpty(wsPath)) wsPath = "/ws-cryptunnel";   // 留空视为默认
        var portText = PortBox.Text.Trim();
        var aes = AesBox.Text.Trim();
        var auth = AuthBox.Text.Trim();
        var enabled = EnabledBox.IsChecked == true;

        if (string.IsNullOrEmpty(name)) { error = "名称必填"; return false; }
        if (!NameRe.IsMatch(name)) { error = "名称只能是小写字母/数字/连字符 [a-z0-9-]+"; return false; }
        if (string.IsNullOrEmpty(url) || (!url.StartsWith("http://") && !url.StartsWith("https://")))
        { error = "服务端 URL 必填且以 http:// 或 https:// 开头"; return false; }

        // 防呆：serverUrl 末尾不应再带 WebSocket 路径。
        // 隧道拼接规则是 wsUrl = ServerUrl + WsPath，若 ServerUrl 已含该路径会变成
        // {wsPath}{wsPath}，握手必然失败且不易察觉。wsPath 取用户实际填的值（未必是默认）。
        if (url.EndsWith(wsPath, StringComparison.OrdinalIgnoreCase))
        {
            error = $"serverUrl 不要包含 WebSocket 路径（隧道会自动拼接，否则会变成 {wsPath}{wsPath}）。\n"
                  + $"请改成：{url[..^wsPath.Length]}";
            return false;
        }
        if (string.IsNullOrEmpty(portText) || !int.TryParse(portText, out var port) || port < 1024 || port > 65535)
        { error = "本地端口必填，范围 1024-65535"; return false; }
        if (string.IsNullOrEmpty(aes)) { error = "AES 密钥必填"; return false; }
        if (string.IsNullOrEmpty(auth)) { error = "认证密钥必填"; return false; }

        // 地址与授权的组合在这里就拦。
        //
        // 真正的闸门在 ConfigLoader，这一道是「让反馈立刻发生」：否则用户能顺利保存，
        // 直到启动隧道时才在日志里看到失败，而那时编辑窗口已经关了，
        // 得重新打开、重新回忆自己填了什么。校验逻辑重复一次是值得的。
        var addrText = (AddressBox.Text ?? "").Trim();
        if (!IsLoopbackText(addrText) && AllowNonLoopbackBox.IsChecked != true)
        {
            error = $"监听地址 “{addrText}” 不是回环地址，局域网内其它机器将能连接本机 {port} 端口"
                  + "并通过本隧道访问内网数据库，且对方无需知道 AES / 认证密钥。\n"
                  + "若确需跨机访问，请勾选「允许局域网内其它机器连接」；否则请把地址改回 127.0.0.1。";
            return false;
        }

        pf = new ProjectFile
        {
            SchemaVersion = 1,
            Name = name,
            DisplayName = string.IsNullOrEmpty(display) ? null : display,
            Enabled = enabled,
            ServerUrl = url,
            WsPath = wsPath,
            AesKey = aes,
            AuthKey = auth,
            Local = new LocalSection
            {
                Port = port,
                Address = string.IsNullOrWhiteSpace(AddressBox.Text) ? null : AddressBox.Text.Trim(),
                AllowNonLoopback = AllowNonLoopbackBox.IsChecked == true,
            },
            // 始终带上规范 Id。是否写进 YAML 由 AppController 决定
            // （等于默认值则省略，保证老文件零变化）。
            Cipher = (CipherBox.SelectedItem as CipherChoice)?.Id ?? CipherRegistry.Default.Id,

            // 界面未覆盖的字段原样带回，避免保存时静默抹掉用户手写的配置。
            Health = _preservedHealth,
        };
        error = "";
        return true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}