using System.Diagnostics;

namespace ImeBench.Core;

/// <summary>
/// 按键节拍器：把"每个按键之间的间隔"控制在毫秒甚至亚毫秒级。
///
/// 为什么不能用 Task.Delay / Thread.Sleep：
/// Windows 默认系统时钟粒度约 15.6ms，Sleep(5) 实际睡 15.6ms。
/// 实测（全拼）用 Sleep 控速约 148ms/组，用 Stopwatch 精确定时只要 18ms/组 —— 差 8 倍。
///
/// 策略：粗粒度用 Sleep 睡掉大头，最后 1ms 用 SpinWait 精确补齐。
/// </summary>
public sealed class Pacer
{
    private readonly Stopwatch _sw = new();
    private long _dueTicks;

    /// <summary>目标间隔（毫秒），0 表示全速。</summary>
    public int IntervalMs { get; set; } = 2;

    public void Reset()
    {
        _sw.Restart();
        _dueTicks = 0;
    }

    public void Wait()
    {
        if (IntervalMs <= 0) return;

        _dueTicks += IntervalMs * Stopwatch.Frequency / 1000L;
        long remainTicks = _dueTicks - _sw.ElapsedTicks;

        // 落后太多（>50ms）说明中间被阻塞过，重新对齐，避免"追赶式"爆发发送
        if (remainTicks < -Stopwatch.Frequency / 20L)
        {
            _dueTicks = _sw.ElapsedTicks;
            return;
        }

        long remainMs = remainTicks * 1000L / Stopwatch.Frequency;
        if (remainMs > 1)
            Thread.Sleep((int)Math.Min(remainMs - 1, 1000));

        while (_sw.ElapsedTicks < _dueTicks)
            Thread.SpinWait(16);
    }
}
