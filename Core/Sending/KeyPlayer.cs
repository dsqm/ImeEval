using ImeBench.Win32;

namespace ImeBench.Core;

/// <summary>
/// 发送按键层：把一个上屏单元的 token 序列逐键打出去。
///
/// 职责边界：
///   - 只管【读按键序列 + 按节奏发送】，不知道控制层为什么停、也不知道结果怎么验证；
///   - 每键之前问一次控制层闸门（RunController.AllowSend），拿到 false 立即返回，
///     由上层决定结论是"用户停止"还是"失焦"；
///   - 节奏由 Pacer 负责（Stopwatch 精确定时，不能用 Task.Delay —— 系统时钟粒度 15.6ms）。
/// </summary>
public sealed class KeyPlayer
{
    private readonly InputSender _sender;

    public KeyPlayer(InputSender sender) => _sender = sender;

    /// <summary>收到最近一次 SendInput 失败的原因（=0 表示没失败过）。</summary>
    public int LastError => _sender.LastError;

    /// <summary>累计失败次数。</summary>
    public int FailCount => _sender.FailCount;

    /// <summary>播放一个上屏单元。返回 Sent 表示全部发完。</summary>
    public SendStatus Play(KeyUnit unit, RunController ctrl, Pacer pacer)
    {
        foreach (var token in unit.Tokens)
        {
            foreach (char c in token.Keys)
            {
                if (!ctrl.AllowSend()) return ctrl.Status;

                var st = _sender.SendChar(c);
                if (st != SendStatus.Sent) return st;

                pacer.Wait();
            }
        }
        return SendStatus.Sent;
    }
}
