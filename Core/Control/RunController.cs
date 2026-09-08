using ImeBench.Win32;
using static ImeBench.Win32.Native;

namespace ImeBench.Core;

/// <summary>停止来源。记录"谁喊停的"，便于事后判断这批数据是否被中断过。</summary>
public enum StopSource
{
    None,
    HotKey,      // 全局热键 F9
    StopButton,  // 界面停止按钮
    LostFocus,   // 主窗口失去焦点
}

/// <summary>
/// 控制层：整个测试的"急停与闸门"总控。
///
/// 职责边界：
///   - 只管【能不能继续发】，不管怎么发、怎么比对、怎么算分；
///   - 所有停止来源（热键 / 停止按钮 / 主窗口失焦）统一汇到这里，
///     不再散落在 UI 的 WndProc、发送器、执行循环三处；
///   - 每发送一个键之前过一次 AllowSend() 闸门，
///     把"失焦后还能溢出几个键"压到最多 1 个（SendInput 已入队的键撤不回来，这是物理上限）。
/// </summary>
public sealed class RunController
{
    private volatile bool _stopRequested;
    private StopSource _source;

    /// <summary>主窗口句柄，用于前台校验。为 Zero 时跳过该项校验。</summary>
    public IntPtr MainHwnd { get; set; }

    public bool StopRequested => _stopRequested;

    public StopSource Source => _source;

    public string SourceText => _source switch
    {
        StopSource.HotKey => "热键 Ctrl+Alt+S",
        StopSource.StopButton => "停止按钮",
        StopSource.LostFocus => "主窗口失去焦点",
        _ => "无",
    };

    public void Reset()
    {
        _stopRequested = false;
        _source = StopSource.None;
    }

    /// <summary>统一停止入口。第一个请求胜出（保留最初的停止原因）。</summary>
    public void RequestStop(StopSource src)
    {
        if (_stopRequested) return;
        _source = src;
        _stopRequested = true;
    }

    /// <summary>每键闸门：未被请求停止 且 前台窗口仍是主窗口。</summary>
    public bool AllowSend()
    {
        if (_stopRequested) return false;

        if (MainHwnd != IntPtr.Zero && GetForegroundWindow() != MainHwnd)
        {
            RequestStop(StopSource.LostFocus);
            return false;
        }
        return true;
    }

    /// <summary>把控制层的状态翻译成发送层能理解的结果。</summary>
    public SendStatus Status =>
        _source == StopSource.LostFocus ? SendStatus.LostFocus : SendStatus.Stopped;
}
