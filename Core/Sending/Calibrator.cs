using System.Diagnostics;
using ImeEval.Win32;

namespace ImeEval.Core;

/// <summary>速度标定参数。</summary>
public sealed class CalibOptions
{
    /// <summary>按键串。语法见 KeySeq：_ = 空格，&lt;Enter&gt; 特殊键，{DELAY=100} 延迟。默认 "mwlidevsgo_"（双拼"美丽的中国"+空格）。</summary>
    public string Keys { get; set; } = "mwlidevsgo_";

    /// <summary>期望上屏文本。</summary>
    public string Expect { get; set; } = "美丽的中国";

    /// <summary>每个档位重复次数。全部命中才算通过。</summary>
    public int Repeats { get; set; } = 20;

    /// <summary>扫描阶梯（毫秒），从快到慢，命中即停。</summary>
    public int[] Ladder { get; set; } = { 0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128 };

    /// <summary>文本连续多少毫秒不变即认为本组上屏完成。</summary>
    public int SettleMs { get; set; } = 40;

    /// <summary>单组最长等待（兜底，防死等）。</summary>
    public int SettleMaxMs { get; set; } = 1500;

    /// <summary>组与组之间的静默间隔。上一组的按键可能在 IME 管道里尚未消化完，
    /// 零间隔连发会前后串音（实测 0ms 档出现「美丽的中国m」这类混入下一组首键的样本）。</summary>
    public int GroupGapMs { get; set; } = 200;

    /// <summary>焦点被抢后暂停等待回归的最长时间（秒），超时中止。</summary>
    public int FocusWaitSec { get; set; } = 60;

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

    /// <summary>实际跑过的组数（被停止/焦点超时截断时小于 Total）。</summary>
    public int Done;

    /// <summary>输出为空（连组字都没出现）的组数——通常意味着焦点不在宿主或 IME 没参与，而非速度问题。</summary>
    public int Empty;

    /// <summary>焦点被抢且等待超时，本档提前放弃。</summary>
    public bool FocusLost;

    /// <summary>是否为「推荐值验证」档（非扫描档）。</summary>
    public bool IsVerify;

    public double MsPerGroup => Done == 0 ? 0 : (double)ElapsedMs / Done;

    public override string ToString()
        => $"{(IsVerify ? "验证 " : "")}{IntervalMs,3}ms | {Hit,3}/{(Done != Total ? $"{Done}/" : "")}{Total}" +
           $"{(Empty > 0 ? $" 空{Empty}" : "")} | {MsPerGroup,7:F1}ms/组 | {(Pass ? "通过" : "失真")} | 样本=\"{Sample}\"";
}

/// <summary>标定进行中的实时进度（每组刷新一次，供界面即时显示）。</summary>
public sealed class CalibProgress
{
    public int IntervalMs;
    public int Done;
    public int Total;
    public int Hit;
    public bool Verify;

    /// <summary>非空时整行显示该状态（如暂停等待焦点回归）。</summary>
    public string? Waiting;

    /// <summary>附注（如「IME 已自动重开」），随进度一起显示。</summary>
    public string? Note;

    public override string ToString()
    {
        if (Waiting != null) return Waiting;
        string t = $"{(Verify ? "验证推荐间隔 " : "正在标定 ")}{IntervalMs}ms：{Done}/{Total}（命中 {Hit}）";
        return Note == null ? t : t + " —— " + Note;
    }
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
        if (Found) return $"最快稳定间隔={BestIntervalMs}ms，推荐={RecommendedMs}ms（已实测验证）";
        if (Aborted) return "标定中止：" + AbortReason;
        return $"全部 {Steps.Count} 档均未通过（样本见日志，输出全空多为焦点/IME 问题，失真多为速度问题），可调大容错或手动指定间隔";
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
        Action<CalibStep>? onStep = null,
        Action<CalibProgress>? onProgress = null)
    {
        var result = new CalibResult();

        await Task.Run(() =>
        {
            var pacer = new Pacer();
            bool anyOutput = false;   // 是否有任何一档产生过输出（IME 确实参与过）
            int? best = null;

            foreach (int interval in opt.Ladder)
            {
                if (ctrl.StopRequested) break;

                CalibStep step = RunOne(host, sender, ctrl, pacer, opt, interval, false, onProgress);
                result.Steps.Add(step);
                onStep?.Invoke(step);

                if (step.FocusLost)
                {
                    result.Aborted = true;
                    result.AbortReason = "标定中止：键盘焦点被抢走且一直没回到标定宿主。" +
                                         "标定期间请停留在【速度标定】页，不要切换页签或点击其他窗口。";
                    break;
                }

                if (step.Pass)
                {
                    best = interval;
                    break;
                }

                if (step.Hit > 0) anyOutput = true;

                // 某档全空且此前也从未有过输出 —— 说明输入法根本没参与（没切中文 / 方案不匹配 / 焦点不在宿主）。
                // 但若之前的档位有过正确输出，说明 IME 在参与、只是这个间隔太快被吞键——继续往上扫更慢的档位。
                if (step.Hit == 0 && !anyOutput)
                {
                    result.Aborted = true;
                    result.AbortReason = $"间隔 {interval}ms 时 {opt.Repeats} 组输出全为空。" +
                                         "请确认已切换到中文输入法（全拼/双拼均可，需与按键串方案一致），且焦点在宿输入框内。";
                    break;
                }
            }

            // 扫描被热键/按钮/失焦打断：结论必须如实说「被停止」，不能与「全档失真」混为一谈
            if (!best.HasValue && !result.Aborted && ctrl.StopRequested)
            {
                result.Aborted = true;
                result.AbortReason = $"标定被停止（{ctrl.SourceText}）：已扫 {result.Steps.Count} 档均未通过。可重新标定。";
            }

            if (best.HasValue)
            {
                result.BestIntervalMs = best;

                if (opt.SafetyMarginMs <= 0)
                {
                    result.RecommendedMs = best;
                }
                else
                {
                    // 容错档必须实测通过，不能盲加：实测存在「0ms 全对、1ms 反而失真」的输入法
                    //（对特定间隔敏感，速度与失真不是单调关系）。从 最快档+容错 起
                    // 逐 +1ms 实测验证，取第一个通过完整档位的值作推荐；都不过则退回已验证的最快档。
                    for (int c = best.Value + opt.SafetyMarginMs, tried = 0;
                         tried < 6 && c <= best.Value + opt.SafetyMarginMs + 12 && !ctrl.StopRequested;
                         c++, tried++)
                    {
                        CalibStep v = RunOne(host, sender, ctrl, pacer, opt, c, verify: true, onProgress);
                        onStep?.Invoke(v);
                        if (v.Pass)
                        {
                            result.RecommendedMs = c;
                            break;
                        }
                    }
                    if (!result.RecommendedMs.HasValue)
                        result.RecommendedMs = best;   // 兜底：容错区间全失真，退回已验证的最快稳定档
                }
            }
        });

        return result;
    }

    private static CalibStep RunOne(
        IUpTextSink host, InputSender sender, RunController ctrl, Pacer pacer,
        CalibOptions opt, int intervalMs, bool verify, Action<CalibProgress>? onProgress)
    {
        var step = new CalibStep { IntervalMs = intervalMs, Total = opt.Repeats, IsVerify = verify };

        pacer.IntervalMs = intervalMs;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < opt.Repeats; i++)
        {
            if (ctrl.StopRequested) break;

            // ── 发送前三道闸门：焦点真实在宿主才发，否则暂停等待（绝不盲发）──
            // 切换页签不会让主窗口失焦，AllowSend 的前台检查拦不住这种情况，
            // 键会打进页签上恰好有焦点的控件（如语料页的文章框）——必须在这里逐组校验。
            string? note = host.EnsureImeReadySafe()
                ? "检测到 IME 处于关闭（英文）状态，已自动重新打开"
                : null;
            if (!WaitFocusOnHost(host, ctrl, opt, intervalMs, verify, onProgress, note))
            {
                if (ctrl.StopRequested) return step;   // 等待期间用户主动停止，不算焦点异常
                step.FocusLost = true;
                break;
            }

            // 组间静默：给 IME 时间消化上一组的残留（管道里可能还有未处理的键）
            if (opt.GroupGapMs > 0)
            {
                var gsw = Stopwatch.StartNew();
                while (gsw.ElapsedMilliseconds < opt.GroupGapMs && !ctrl.StopRequested)
                    Thread.Sleep(Math.Min(10, Math.Max(1, opt.GroupGapMs - (int)gsw.ElapsedMilliseconds)));
            }
            if (ctrl.StopRequested) break;

            host.ResetSafe();
            pacer.Reset();
            step.Done = i + 1;   // 本组确已尝试发送（中途停止也如实计数）

            bool retryGroup = false;
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

                if (!ctrl.AllowSend())
                {
                    if (ctrl.StopRequested) return step;
                    retryGroup = true;   // 前台被抢（暂停模式）：丢弃半截组，等回归后整组重发
                    break;
                }
                _ = it.Kind == KeySeqItem.ItemKind.Vk ? sender.SendVk(it.Vk) : sender.SendChar(it.Char);
                pacer.Wait();
            }

            if (retryGroup)
            {
                host.ResetSafe();
                if (!WaitFocusOnHost(host, ctrl, opt, intervalMs, verify, onProgress, "前台窗口被抢，等待恢复后重发本组"))
                {
                    step.FocusLost = true;
                    return step;
                }
                i--;   // 重发同一组
                continue;
            }

            string text = host.WaitStable(opt.SettleMs, opt.SettleMaxMs, () => ctrl.StopRequested);
            if (text == opt.Expect)
            {
                step.Hit++;
            }
            else
            {
                if (text.Length == 0) step.Empty++;
                if (step.Sample.Length == 0) step.Sample = text; // 只留第一个错例，日志不至于被刷爆
            }

            onProgress?.Invoke(new CalibProgress
            {
                IntervalMs = intervalMs,
                Done = i + 1,
                Total = opt.Repeats,
                Hit = step.Hit,
                Verify = verify,
            });
        }

        sw.Stop();
        step.ElapsedMs = sw.ElapsedMilliseconds;
        step.Pass = !step.FocusLost && step.Hit == opt.Repeats && opt.Repeats > 0;
        host.ResetSafe();
        return step;
    }

    /// <summary>
    /// 焦点闸门：确保键盘焦点真实落在宿主上；被抢（如切了页签）则暂停等待回归。
    /// 等待期间一个键都不发。超时返回 false。
    /// </summary>
    private static bool WaitFocusOnHost(
        IUpTextSink host, RunController ctrl, CalibOptions opt,
        int intervalMs, bool verify, Action<CalibProgress>? onProgress, string? note)
    {
        host.FocusSafe();
        if (host.IsFocusedSafe()) return true;

        bool recovered = false;
        var sw = Stopwatch.StartNew();
        while (!ctrl.StopRequested && sw.Elapsed.TotalSeconds < opt.FocusWaitSec)
        {
            host.FocusSafe();
            if (host.IsFocusedSafe()) { recovered = true; break; }
            onProgress?.Invoke(new CalibProgress
            {
                IntervalMs = intervalMs,
                Verify = verify,
                Waiting = $"⏸ 已暂停发送：键盘焦点不在标定宿主（{sw.Elapsed.Seconds}s/{opt.FocusWaitSec}s）" +
                          "——请回到【速度标定】页，F9 可停止",
            });
            Thread.Sleep(500);
        }

        bool ok = !ctrl.StopRequested && host.IsFocusedSafe();
        if (ok && recovered)
        {
            onProgress?.Invoke(new CalibProgress
            {
                IntervalMs = intervalMs,
                Verify = verify,
                Note = (note != null ? note + "；" : "") + "焦点已恢复，继续标定",
            });
        }
        return ok;
    }
}
