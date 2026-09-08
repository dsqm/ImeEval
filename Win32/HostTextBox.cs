using ImeBench.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static ImeBench.Win32.Native;

namespace ImeBench.Win32;

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

    /// <summary>
    /// IME 提交事件计数快照（GCS_RESULTSTR 累计次数），线程安全。
    /// 每次 IME 真正把文本提交进宿主都会 +1：一字一屏 = 字数次，整句一屏 = 1。
    /// </summary>
    public int SnapshotCommitEvents() => Volatile.Read(ref ResultCount);

    public void Reset()
    {
        _committed.Clear();
        _composing = false;
        StartCount = CompCount = EndCount = ResultCount = 0;
        Text = string.Empty;
    }

    // ---------- 跨线程安全访问 ----------
    // 发送循环跑在后台线程，读写控件必须封送回 UI 线程，否则取到的是脏数据。

    /// <summary>线程安全地读取控件文本。</summary>
    public string ReadTextSafe()
        => InvokeRequired ? (string)Invoke(new Func<string>(() => Text)) : Text;

    /// <summary>线程安全清空（含未提交的 composition）。</summary>
    public void ResetSafe()
    {
        if (InvokeRequired) { Invoke(new Action(ResetSafe)); return; }
        ClearIme();
        Reset();
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
            if (cur != last) { last = cur; stableSince = sw.ElapsedMilliseconds; continue; }
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
