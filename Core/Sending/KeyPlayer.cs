using System.Diagnostics;
using ImeBench.Win32;

namespace ImeBench.Core;

/// <summary>
/// 发送按键层：把一个上屏单元的按键串逐键打出去。
///
/// 按键串语法见 KeySeq（_ = 空格、&lt;Shift&gt; 等特殊键、{DELAY=100} 延迟）。
///
/// 职责边界：
///   - 只管【解析按键串 + 按节奏发送】，不知道控制层为什么停、也不知道结果怎么验证；
///   - 每键之前问一次控制层闸门（RunController.AllowSend），拿到 false 立即返回，
///     由上层决定结论是"用户停止"还是"失焦"；
///   - 节奏由 Pacer 负责（Stopwatch 精确定时，不能用 Task.Delay —— 系统时钟粒度 15.6ms）；
///   - {DELAY=} 等待期间每 10ms 检查一次停止标志，保证长延迟也能即时停。
/// </summary>
public sealed class KeyPlayer
{
    private readonly InputSender _sender;

    /// <summary>解析/发送过程中的错误（无法识别的按键名等）。每轮开始前由上层清空。</summary>
    public List<string> Errors { get; } = new();

    public KeyPlayer(InputSender sender) => _sender = sender;

    /// <summary>清空错误累积（每轮测试开始前调用一次）。</summary>
    public void ResetErrors() => Errors.Clear();

    /// <summary>收到最近一次 SendInput 失败的原因（=0 表示没失败过）。</summary>
    public int LastError => _sender.LastError;

    /// <summary>累计失败次数。</summary>
    public int FailCount => _sender.FailCount;

    /// <summary>播放一个上屏单元。返回 Sent 表示全部发完。</summary>
    public SendStatus Play(KeyUnit unit, RunController ctrl, Pacer pacer)
    {
        foreach (var token in unit.Tokens)
        {
            var items = KeySeq.Parse(token.Keys, Errors);
            foreach (var it in items)
            {
                if (it.Kind == KeySeqItem.ItemKind.Delay)
                {
                    WaitDelay(it.DelayMs, ctrl);
                    continue;
                }

                if (!ctrl.AllowSend()) return ctrl.Status;

                var st = it.Kind == KeySeqItem.ItemKind.Vk
                    ? _sender.SendVk(it.Vk)
                    : _sender.SendChar(it.Char);
                if (st != SendStatus.Sent) return st;

                pacer.Wait();
            }
        }
        return SendStatus.Sent;
    }

    /// <summary>标定等场景直接播放一段裸按键串。</summary>
    public SendStatus PlayRaw(string keys, RunController ctrl, Pacer pacer)
    {
        foreach (var it in KeySeq.Parse(keys, Errors))
        {
            if (it.Kind == KeySeqItem.ItemKind.Delay)
            {
                WaitDelay(it.DelayMs, ctrl);
                continue;
            }

            if (!ctrl.AllowSend()) return ctrl.Status;

            var st = it.Kind == KeySeqItem.ItemKind.Vk
                ? _sender.SendVk(it.Vk)
                : _sender.SendChar(it.Char);
            if (st != SendStatus.Sent) return st;

            pacer.Wait();
        }
        return SendStatus.Sent;
    }

    /// <summary>等待指定毫秒，期间分片检查停止标志，长延迟也能即时停。</summary>
    private static void WaitDelay(int ms, RunController ctrl)
    {
        if (ms <= 0) return;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (ctrl.StopRequested) return;
            int remain = (int)(ms - sw.ElapsedMilliseconds);
            Thread.Sleep(Math.Min(10, remain));
        }
    }
}
