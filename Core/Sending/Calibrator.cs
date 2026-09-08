using System.Diagnostics;
using ImeBench.Win32;

namespace ImeBench.Core;

/// <summary>速度标定参数。</summary>
public sealed class CalibOptions
{
    /// <summary>按键串。语法见 KeySeq：_ = 空格，&lt;Enter&gt; 特殊键，{DELAY=100} 延迟。默认 "vsgo_"（小鹤双拼"中国"+空格）。</summary>
    public string Keys { get; set; } = "vsgo_";

    /// <summary>期望上屏文本。</summary>
    public string Expect { get; set; } = "中国";

    /// <summary>每个档位重复次数。全部命中才算通过。</summary>
    public int Repeats { get; set; } = 20;

    /// <summary>扫描阶梯（毫秒），从快到慢，命中即停。</summary>
    public int[] Ladder { get; set; } = { 0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128 };

    /// <summary>文本连续多少毫秒不变即认为本组上屏完成。</summary>
    public int SettleMs { get; set; } = 40;

    /// <summary>单组最长等待（兜底，防死等）。</summary>
    public int SettleMaxMs { get; set; } = 1500;

    /// <summary>找到可用档位后额外加的容错（毫秒）。用户可自行覆盖。</summary>
    public int SafetyMarginMs { get; set; } = 1;
}

public sealed class CalibStep
{
    public int IntervalMs;
    public int Hit;
    public int Total;
    public long ElapsedMs;
    public bool Pass;
    public string Sample = "";

    public double MsPerGroup => Total == 0 ? 0 : (double)ElapsedMs / Total;

    public override string ToString()
        => $"{IntervalMs,3}ms | {Hit,3}/{Total,-3} | {MsPerGroup,7:F1}ms/组 | {(Pass ? "通过" : "失真")} | 样本=\"{Sample}\"";
}

public sealed class CalibResult
{
    public List<CalibStep> Steps { get; } = new();
    public int? BestIntervalMs { get; set; }
    public int? RecommendedMs { get; set; }
    public bool Found => BestIntervalMs.HasValue;

    /// <summary>中途放弃（某档一组都没输出，继续扫下去没意义）。</summary>
    public bool Aborted { get; set; }
    public string AbortReason { get; set; } = "";

    public override string ToString()
    {
        if (Found) return $"最快稳定间隔={BestIntervalMs}ms，推荐={RecommendedMs}ms（含容错）";
        if (Aborted) return "标定中止：" + AbortReason;
        return $"全部 {Steps.Count} 档均未通过，建议检查输入法是否切到全拼，或手动指定更大间隔";
    }
}

/// <summary>
/// 速度标定器：自动找出"结果仍然正确"的最快按键间隔。
///
/// 背景：不同输入法 / 不同机器对发送速度的耐受度差异极大 ——
/// 实测搜狗 2ms/键 全对，微软拼音同样档位错 60%。所以间隔不能写死，
/// 必须现场标定；标定结果 +1ms 作为默认，用户仍可手动覆盖。
/// </summary>
public static class Calibrator
{
    public static async Task<CalibResult> RunAsync(
        IUpTextSink host,
        InputSender sender,
        RunController ctrl,
        CalibOptions opt,
        Action<CalibStep>? onStep = null)
    {
        var result = new CalibResult();

        await Task.Run(() =>
        {
            var pacer = new Pacer();

            foreach (int interval in opt.Ladder)
            {
                if (ctrl.StopRequested) break;

                CalibStep step = RunOne(host, sender, ctrl, pacer, opt, interval);
                result.Steps.Add(step);
                onStep?.Invoke(step);

                if (step.Pass)
                {
                    result.BestIntervalMs = interval;
                    break;
                }

                // 一组的输出都没有 —— 说明输入法根本没参与（没切中文 / 没切全拼 / 焦点不在宿主）。
                // 这时候继续往上扫只是白等（每档都要空等到超时），直接放弃。
                if (step.Hit == 0)
                {
                    result.Aborted = true;
                    result.AbortReason = $"间隔 {interval}ms 时 {opt.Repeats} 组输出全为空。" +
                                         "请确认已切换到中文输入法的【全拼】模式，且焦点在宿输入框内。";
                    break;
                }
            }

            if (result.BestIntervalMs.HasValue)
                result.RecommendedMs = result.BestIntervalMs.Value + Math.Max(0, opt.SafetyMarginMs);
        });

        return result;
    }

    private static CalibStep RunOne(
        IUpTextSink host, InputSender sender, RunController ctrl, Pacer pacer,
        CalibOptions opt, int intervalMs)
    {
        var step = new CalibStep { IntervalMs = intervalMs, Total = opt.Repeats };

        pacer.IntervalMs = intervalMs;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < opt.Repeats; i++)
        {
            if (ctrl.StopRequested) break;

            host.ResetSafe();
            pacer.Reset();

            foreach (var it in KeySeq.Parse(opt.Keys))
            {
                if (ctrl.StopRequested) return step;

                if (it.Kind == KeySeqItem.ItemKind.Delay)
                {
                    var dsw = Stopwatch.StartNew();
                    while (dsw.ElapsedMilliseconds < it.DelayMs && !ctrl.StopRequested)
                        Thread.Sleep(Math.Min(10, Math.Max(1, it.DelayMs - (int)dsw.ElapsedMilliseconds)));
                    continue;
                }

                if (!ctrl.AllowSend()) return step;
                _ = it.Kind == KeySeqItem.ItemKind.Vk ? sender.SendVk(it.Vk) : sender.SendChar(it.Char);
                pacer.Wait();
            }

            string text = host.WaitStable(opt.SettleMs, opt.SettleMaxMs, () => ctrl.StopRequested);
            if (text == opt.Expect)
            {
                step.Hit++;
            }
            else if (step.Sample.Length == 0)
            {
                step.Sample = text; // 只留第一个错例，日志不至于被刷爆
            }
        }

        sw.Stop();
        step.ElapsedMs = sw.ElapsedMilliseconds;
        step.Pass = step.Hit == opt.Repeats && opt.Repeats > 0;
        host.ResetSafe();
        return step;
    }
}
