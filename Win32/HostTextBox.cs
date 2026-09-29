using ImeEval.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static ImeEval.Win32.Native;
namespace ImeEval.Win32;

/// <summary>
/// 内嵌文本宿主：RichTextBox（msftedit）子类，负责捕获 IME 消息与文本变化。
///
/// POC 阶段的核心任务：搞清楚"控件文本"与"真正已上屏文本"的关系。
/// 提供三路观测：
///   - ControlText       : WM_GETTEXT 读到的控件文本（可能含 composition 串）
///   - CompositionString : ImmGetCompositionString(GCS_COMPSTR)，当前未上屏串
///   - CommittedText     : 累加器，只累加 WM_IME_COMPOSITION 的 GCS_RESULTSTR（= 真正上屏内容）
/// </summary>
public sealed class ImeHost : RichTextBox, IUpTextSink
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>日志回调（由 UI 提供）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string> Log { get; set; }

    private readonly StringBuilder _committed = new();
    private bool _composing;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Composing => _composing;

    /// <summary>只由 GCS_RESULTSTR 累加得到的"已上屏文本"。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CommittedText => _committed.ToString();

    /// <summary>当前 composition 串（未上屏）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CompositionString => GetCompositionString(Handle, GCS_COMPSTR) ?? string.Empty;

    /// <summary>控件文本（POC 就靠它验证是否被 composition 污染）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ControlText => Text;

    // 观测计数器
    public int StartCount, CompCount, EndCount, ResultCount;

    private readonly object _commitLogLock = new();
    private readonly List<string> _commitLog = new();   // 每次 IME 提交的文本段（与 ResultCount 一一对应）

    /// <summary>
    /// IME 提交事件计数快照（GCS_RESULTSTR 累计次数），线程安全。
    /// 每次 IME 真正把文本提交进宿主都会 +1：一字一屏 = 字数次，整句一屏 = 1。
    /// </summary>
    public int SnapshotCommitEvents() => Volatile.Read(ref ResultCount);

    /// <summary>取自 fromIndex 起的提交文本段（即这些快照之后发生的上屏），线程安全。</summary>
    public IReadOnlyList<string> ReadCommitEvents(int fromIndex)
    {
        lock (_commitLogLock)
            return _commitLog.Skip(Math.Max(0, fromIndex)).ToList();
    }

    public void Reset()
    {
        _committed.Clear();
        _composing = false;
        StartCount = CompCount = EndCount = ResultCount = 0;
        lock (_commitLogLock) _commitLog.Clear();
        Text = string.Empty;
    }

    // ---------- 跨线程安全访问 ----------
    // 发送循环跑在后台线程，读写控件必须封送回 UI 线程，否则取到的是脏数据。

    /// <summary>线程安全地读取控件文本。</summary>
    public string ReadTextSafe()
        => InvokeRequired ? (string)Invoke(new Func<string>(() => Text)) : Text;

    /// <summary>线程安全清空（含未提交的 composition）。只在真有残留组字时才清，
    /// 避免对空闲的 IME 反复强制清串（实测会把部分输入法的组字会话搅乱）。</summary>
    public void ResetSafe()
    {
        if (InvokeRequired) { Invoke(new Action(ResetSafe)); return; }
        if (Composing || !string.IsNullOrEmpty(CompositionString)) ClearIme();
        Reset();
    }

    /// <summary>线程安全读取组字状态（IME 正在编辑未上屏的拼音串）。</summary>
    public bool IsComposingSafe()
        => InvokeRequired ? (bool)Invoke(new Func<bool>(() => _composing)) : _composing;

    /// <summary>线程安全读取焦点状态。</summary>
    public bool IsFocusedSafe()
        => InvokeRequired ? (bool)Invoke(new Func<bool>(() => Focused)) : Focused;

    /// <summary>
    /// 线程安全确保 IME 处于打开（中文）状态；若被切到关闭（英文）则重新打开。
    /// 返回 true = 本次执行了重开。IME 被外部切走时按键会以英文直插或完全失效，
    /// 标定/运行前检查一次能自愈最常见的中途"全空"故障。
    /// </summary>
    public bool EnsureImeReadySafe()
    {
        if (InvokeRequired) return (bool)Invoke(new Func<bool>(EnsureImeReadySafe));
        IntPtr himc = ImmGetContext(Handle);
        if (himc == IntPtr.Zero) return false;
        try
        {
            if (ImmGetOpenStatus(himc)) return false;
            ImmSetOpenStatus(himc, true);
            return true;
        }
        finally { ImmReleaseContext(Handle, himc); }
    }

    /// <summary>把键盘焦点夺回来（发送前必做，否则按键发给别的控件）。</summary>
    public void FocusSafe()
    {
        if (InvokeRequired) { Invoke(new Action(FocusSafe)); return; }
        if (!Focused) Focus();
    }

    /// <summary>
    /// 等待上屏稳定：文本连续 <paramref name="stableMs"/> 毫秒不再变化即返回。
    /// 比固定 Sleep 快得多，是吞吐的关键。
    ///
    /// 稳定判定对 IME 组字感知：composition 未结束就不算稳定，否则会在提交前
    /// 把组字串（如 "mw'li'de'vs"）当成最终上屏文本采走，把快档位全部误判为失真。
    /// 若按键被 IME 整体吞掉（文本一直为空、也未进入组字），走正常静默判定返回空串，
    /// 由上层按失真处理。
    /// </summary>
    public string WaitStable(int stableMs = 40, int maxMs = 1500, Func<bool>? stop = null)
    {
        var sw = Stopwatch.StartNew();
        string last = ReadTextSafe();
        long stableSince = sw.ElapsedMilliseconds;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(8);
            if (stop != null && stop()) break;
            string cur = ReadTextSafe();
            if (cur != last || IsComposingSafe()) { last = cur; stableSince = sw.ElapsedMilliseconds; continue; }
            if (sw.ElapsedMilliseconds - stableSince >= stableMs) break;
        }
        return ReadTextSafe();
    }

    /// <summary>
    /// 丢弃当前未提交的 composition。
    /// 实测：一次未上屏的拼音会残留到下一次测试，导致结果串互相污染
    /// （第一次 zhongguo 没上屏 → 第二次出来 "张鸥能顾欧章欧能古偶" 这种鬼东西）。
    /// 每次开跑前必须先清空。
    /// </summary>
    public bool ClearIme() => ClearComposition(Handle);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, (IntPtr)ENM_CHANGE);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_IME_STARTCOMPOSITION:
                StartCount++;
                _composing = true;
                Log?.Invoke($"[IME] STARTCOMPOSITION (#{StartCount})");
                break;

            case WM_IME_COMPOSITION:
            {
                CompCount++;
                int flags = unchecked((int)m.LParam.ToInt64());
                string comp = GetCompositionString(Handle, GCS_COMPSTR) ?? "";
                string result = (flags & GCS_RESULTSTR) != 0
                    ? (GetCompositionString(Handle, GCS_RESULTSTR) ?? "")
                    : "";
                if (!string.IsNullOrEmpty(result))
                {
                    ResultCount++;
                    _committed.Append(result);
                    lock (_commitLogLock) _commitLog.Add(result);   // 记录提交点位（文本段）
                }
                Log?.Invoke($"[IME] COMPOSITION flags=0x{flags:X4} comp=\"{comp}\" result=\"{result}\"");
                break;
            }

            case WM_IME_ENDCOMPOSITION:
                EndCount++;
                _composing = false;
                Log?.Invoke($"[IME] ENDCOMPOSITION (#{EndCount})");
                break;
        }

        base.WndProc(ref m);
    }
}
