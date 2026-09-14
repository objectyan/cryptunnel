using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Cryptunnel.App;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Models;

namespace UiVerify;

/// <summary>
/// <c>ProjectEditorWindow</c> 的运行时验收：真正构造窗口、真正读控件状态。
///
/// <para>编译通过不等于能显示 —— XAML 的模板与资源引用全在运行时解析。
/// 这里不截图、不做像素比对，只验可自动判定的行为契约：
/// 下拉框填了什么、回填选中了谁、保存收上来的是不是规范 Id。</para>
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== ProjectEditorWindow 运行时验收 ===");
        Console.WriteLine();

        // WPF 控件要求 Application 已存在（资源查找、模板应用都依赖它）
        _ = new Application();

        try
        {
            RunChecks();
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"  [FATAL] {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.StackTrace);
        }

        Console.WriteLine();
        Console.WriteLine($"=== 通过 {_passed} 项，失败 {_failed} 项 ===");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunChecks()
    {
        Console.WriteLine("[1] 窗口能否构造（XAML 模板与资源引用全部可解析）");

        Check("新建模式构造不抛异常", () => { _ = New(null); return true; });
        Check("编辑模式构造不抛异常", () => { _ = New(Sample("aes-256-gcm")); return true; });

        Console.WriteLine();
        Console.WriteLine("[2] 算法下拉框内容");

        Check("只列规范标识，不列别名（sm4 不单独成条目）", () =>
        {
            var w = New(null);
            var ids = CipherIds(w);
            return ids.Count == 3 && !ids.Contains("sm4");
        });

        Check("三个算法齐全", () =>
        {
            var w = New(null);
            var ids = CipherIds(w);
            return ids.Contains("aes-256-cbc-hmac-sha256")
                && ids.Contains("aes-256-gcm")
                && ids.Contains("sm4-cbc-hmac-sha256");
        });

        Check("默认算法排在第一位", () =>
        {
            var w = New(null);
            return CipherIds(w)[0] == CipherRegistry.Default.Id;
        });

        Console.WriteLine();
        Console.WriteLine("[3] 回填与归一");

        Check("新建时默认选中默认算法", () =>
        {
            var w = New(null);
            return Selected(w) == CipherRegistry.Default.Id;
        });

        Check("编辑时回填已配置的算法", () =>
        {
            var w = New(Sample("aes-256-gcm"));
            return Selected(w) == "aes-256-gcm";
        });

        Check("别名 sm4 回填时选中规范条目", () =>
        {
            var w = New(Sample("sm4"));
            return Selected(w) == "sm4-cbc-hmac-sha256";
        });

        // 手改 YAML 手滑写了个不认的值时，回落默认而不是留空。
        // 留空会让「保存」把空 cipher 写回去，静默洗掉用户的错误配置、掩盖问题。
        Check("配置里是未知算法时回落默认而非留空", () =>
        {
            var w = New(Sample("no-such-cipher"));
            return Selected(w) == CipherRegistry.Default.Id;
        });

        Check("cipher 为 null 时选中默认", () =>
        {
            var w = New(Sample(null));
            return Selected(w) == CipherRegistry.Default.Id;
        });

        Console.WriteLine();
        Console.WriteLine("[4] 提示文案随选择变化（非默认算法必须警示同步改服务端）");

        Check("默认算法：提示说明不会写入配置文件", () =>
        {
            var w = New(null);
            return Hint(w).Contains("不会写入");
        });

        Check("非默认算法：提示点名服务端配置键与具体算法", () =>
        {
            var w = New(Sample("aes-256-gcm"));
            var h = Hint(w);
            return h.Contains("cryptunnel.cipher") && h.Contains("aes-256-gcm");
        });

        Check("非默认算法：提示点明与密钥错误无法区分", () =>
        {
            var w = New(Sample("sm4"));
            return Hint(w).Contains("Auth decrypt failed");
        });

        Console.WriteLine();
        Console.WriteLine("[5] 保存时收上来的必须是规范 Id");

        Check("保存别名配置产出规范 Id", () =>
        {
            var w = New(Sample("sm4"));
            var pf = Collect(w);
            return pf?.Cipher == "sm4-cbc-hmac-sha256";
        });

        Check("保存默认配置产出默认 Id（由写回层决定是否落盘）", () =>
        {
            var w = New(Sample(null));
            var pf = Collect(w);
            return pf?.Cipher == CipherRegistry.Default.Id;
        });

        Console.WriteLine();
        Console.WriteLine("[6] 监听地址与非回环授权");

        Check("新建时地址默认 127.0.0.1", () =>
        {
            var w = New(null);
            return AddrText(w) == "127.0.0.1";
        });

        Check("新建时授权开关默认关闭", () =>
        {
            var w = New(null);
            return AllowBox(w).IsChecked != true;
        });

        // IsEditable 的 ComboBox 若模板缺少 PART_EditableTextBox，Text 会读不到手输内容。
        // 本项目的 GlassCombo 是整套自定义模板，这条断言就是防它。
        Check("地址框可编辑且 Text 可读写（自定义模板未破坏 IsEditable）", () =>
        {
            var w = New(null);
            var box = AddrBox(w);
            if (!box.IsEditable) return false;
            box.Text = "10.0.0.7";
            return AddrText(w) == "10.0.0.7";
        });

        Check("编辑模式回填已配置的地址与授权", () =>
        {
            var w = New(SampleAddr("0.0.0.0", true));
            return AddrText(w) == "0.0.0.0" && AllowBox(w).IsChecked == true;
        });

        Check("回环地址提示为安全说明，不出现拒绝字样", () =>
        {
            var w = New(null);
            var hint = AddrHint(w);
            return hint.Contains("仅本机") && !hint.Contains("拒绝");
        });

        // 只说「不安全」是不够的：校验在 ConfigLoader，用户会以为保存成功即可用。
        Check("非回环且未授权：提示必须点明启动会被拒绝", () =>
        {
            var w = New(SampleAddr("0.0.0.0", false));
            return AddrHint(w).Contains("拒绝");
        });

        Check("非回环且已授权：提示改为风险确认，说明对方无需密钥", () =>
        {
            var w = New(SampleAddr("0.0.0.0", true));
            var hint = AddrHint(w);
            return hint.Contains("无需知道") && !hint.Contains("拒绝");
        });

        Check("非回环且未授权时保存被拒", () =>
        {
            var w = New(SampleAddr("0.0.0.0", false));
            return Collect(w) is null;
        });

        Check("非回环且已授权时保存通过并带上开关", () =>
        {
            var w = New(SampleAddr("0.0.0.0", true));
            var pf = Collect(w);
            return pf?.Local?.Address == "0.0.0.0" && pf.Local.AllowNonLoopback == true;
        });

        // 127.0.0.5 同属 127.0.0.0/8，只有本机可达，不该要求授权。
        Check("127.0.0.0/8 段内地址无需授权即可保存", () =>
        {
            var w = New(SampleAddr("127.0.0.5", false));
            return Collect(w)?.Local?.Address == "127.0.0.5";
        });
    }

    private static ProjectEditorWindow New(ProjectFile? existing) => new(existing);

    private static ProjectFile Sample(string? cipher) => new()
    {
        SchemaVersion = 1,
        Name = "demo",
        Enabled = true,
        ServerUrl = "http://127.0.0.1:8080",
        AesKey = "verify-aes-key",
        AuthKey = "verify-auth-key",
        Local = new LocalSection { Port = 5300 },
        Cipher = cipher,
    };

    private static ComboBox Combo(ProjectEditorWindow w) =>
        (ComboBox)Field(w, "CipherBox");

    /// <summary>构造一个只在监听地址与授权开关上有差异的样本。</summary>
    private static ProjectFile SampleAddr(string address, bool allow) => new()
    {
        SchemaVersion = 1,
        Name = "demo",
        Enabled = true,
        ServerUrl = "http://127.0.0.1:8080",
        AesKey = "verify-aes-key",
        AuthKey = "verify-auth-key",
        Local = new LocalSection { Port = 5300, Address = address, AllowNonLoopback = allow },
    };

    private static ComboBox AddrBox(ProjectEditorWindow w) =>
        (ComboBox)Field(w, "AddressBox");

    private static string AddrText(ProjectEditorWindow w) =>
        (AddrBox(w).Text ?? string.Empty).Trim();

    private static CheckBox AllowBox(ProjectEditorWindow w) =>
        (CheckBox)Field(w, "AllowNonLoopbackBox");

    private static string AddrHint(ProjectEditorWindow w) =>
        ((TextBlock)Field(w, "AddressHint")).Text ?? string.Empty;

    private static List<string> CipherIds(ProjectEditorWindow w) =>
        Combo(w).Items.Cast<object>().Select(IdOf).ToList();

    private static string Selected(ProjectEditorWindow w) => IdOf(Combo(w).SelectedItem);

    private static string Hint(ProjectEditorWindow w) =>
        ((TextBlock)Field(w, "CipherHint")).Text ?? string.Empty;

    /// <summary>条目是 private record CipherChoice，反射取 Id 属性。</summary>
    private static string IdOf(object? item) =>
        item?.GetType().GetProperty("Id")?.GetValue(item) as string ?? "";

    private static object Field(ProjectEditorWindow w, string name) =>
        w.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w)
        ?? throw new InvalidOperationException($"控件字段 {name} 不存在");

    /// <summary>
    /// 调 private TryCollect 拿到将要落盘的 ProjectFile。
    /// 不为测试便利把它改成 public —— 生产代码的封装不该被验收需求侵蚀。
    /// </summary>
    private static ProjectFile? Collect(ProjectEditorWindow w)
    {
        var mi = w.GetType().GetMethod("TryCollect", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("TryCollect 方法不存在");
        var args = new object?[] { null, null };
        var ok = (bool)mi.Invoke(w, args)!;
        return ok ? (ProjectFile?)args[0] : null;
    }

    private static void Check(string name, Func<bool> assertion)
    {
        bool ok;
        var extra = string.Empty;
        try
        {
            ok = assertion();
        }
        catch (Exception e)
        {
            ok = false;
            extra = $" -> {e.GetType().Name}: {e.Message}";
        }

        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{extra}"); }
    }
}
