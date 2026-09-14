using System;
using System.Windows;
using System.Windows.Media;
using Cryptunnel.Core.Health;

namespace Cryptunnel.App.Models;

/// <summary>
/// 单层探活结果的显示模型。
///
/// <para>颜色与图标在这里算好，而不是塞进 XAML 的 Trigger ——
/// 三种状态 × 四个视觉属性用 DataTrigger 表达需要十几行标记，
/// 且「Skipped 必须与 Pass 视觉上明确区分」这条规则会散落在标记里无法一眼核对。</para>
/// </summary>
public sealed class HealthStageVm
{
    private static readonly Brush PassBrush = Freeze("#3ddc97");
    private static readonly Brush FailBrush = Freeze("#ff5d6c");
    private static readonly Brush SkipBrush = Freeze("#6c7890");

    public string StageName { get; }
    public string Summary { get; }
    public string Advice { get; }
    public string Icon { get; }
    public Brush Brush { get; }
    public string ElapsedText { get; }

    /// <summary>无建议时整行隐藏，避免留出一条空白把卡片撑高。</summary>
    public Visibility AdviceVisibility =>
        string.IsNullOrEmpty(Advice) ? Visibility.Collapsed : Visibility.Visible;

    public HealthStageVm(HealthStageResult r)
    {
        StageName = HealthReport.StageName(r.Stage);
        Summary = r.Summary;
        Advice = r.Advice ?? "";

        switch (r.Status)
        {
            case HealthStatus.Pass:
                Icon = "✓";
                Brush = PassBrush;
                break;
            case HealthStatus.Fail:
                Icon = "✕";
                Brush = FailBrush;
                break;
            default:
                // 「未执行」用中空符号且置灰：它既不是通过也不是失败，
                // 视觉上必须一眼与另外两者分开 —— 把没验过的东西显示成绿勾是最糟的一种谎。
                Icon = "○";
                Brush = SkipBrush;
                break;
        }

        // 跳过的层没有耗时可言，显示 0ms 会让人以为「跑了且很快」。
        ElapsedText = r.Status == HealthStatus.Skipped ? "—" : $"{r.ElapsedMs} ms";
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
