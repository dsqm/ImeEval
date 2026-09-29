namespace ImeEval.Core;

/// <summary>
/// 上屏文本捕获能力抽象。
/// 由宿主控件实现（当前是内嵌 RichEdit 宿主），验证层只依赖这个接口，
/// 这样将来换成 TSF sink 或外部窗口时，验证层一行都不用改。
/// </summary>
public interface IUpTextSink
{
    /// <summary>等待本次上屏稳定并返回文本（文本连续若干毫秒不变即认为结束）。</summary>
    string WaitStable(int stableMs, int maxMs, Func<bool>? stop = null);

    /// <summary>线程安全地清空宿主（含未提交的 composition）。</summary>
    void ResetSafe();

    /// <summary>线程安全地夺回键盘焦点。</summary>
    void FocusSafe();

    /// <summary>线程安全读取焦点是否在本宿主上。</summary>
    bool IsFocusedSafe();

    /// <summary>确保 IME 处于打开（中文）状态，被切走则重开。返回 true = 执行了重开。</summary>
    bool EnsureImeReadySafe();

    /// <summary>IME 提交事件计数快照（GCS_RESULTSTR 累计次数，线程安全）。</summary>
    int SnapshotCommitEvents();

    /// <summary>取自 fromIndex 起的提交文本段（每次上屏的内容），线程安全。</summary>
    IReadOnlyList<string> ReadCommitEvents(int fromIndex);
}

/// <summary>
/// 验证层：取输入法上屏的字符，与期望文本比对，产出逐单元的错误明细。
///
/// 职责边界：
///   - 只管【拿到上屏文本 + 算错】，不管按键是怎么发过去的；
///   - 错字数 = Levenshtein 编辑距离，细分 S 替换 / D 删除 / I 插入；
///   - 每个上屏单元独立捕获、独立比对（单元跑完即清空宿主），错误不会跨单元传播。
/// </summary>
public sealed class Verifier
{
    private readonly IUpTextSink _sink;

    public Verifier(IUpTextSink sink) => _sink = sink;

    /// <summary>单元开跑前把宿主清干净（含 IME 残留的半截 composition）。</summary>
    public void Reset() => _sink.ResetSafe();

    /// <summary>IME 提交事件计数快照（GCS_RESULTSTR 累计次数，透传宿主计数）。</summary>
    public int SnapshotCommitEvents() => _sink.SnapshotCommitEvents();

    /// <summary>取自 fromIndex 起的提交文本段（每次上屏的内容）。</summary>
    public IReadOnlyList<string> ReadCommitEvents(int fromIndex) => _sink.ReadCommitEvents(fromIndex);

    /// <summary>夺回焦点，保证后续按键确实打给宿主。</summary>
    public void Focus() => _sink.FocusSafe();

    /// <summary>焦点是否真实落在本宿主上（切换页签/误点其他控件会丢）。</summary>
    public bool IsFocused() => _sink.IsFocusedSafe();

    /// <summary>确保 IME 打开（中文），被切走则重开。返回 true = 执行了重开。</summary>
    public bool EnsureImeReady() => _sink.EnsureImeReadySafe();

    /// <summary>捕获本次上屏的文本。</summary>
    public string Capture(int settleMs, int maxMs, Func<bool>? stop)
        => _sink.WaitStable(settleMs, maxMs, stop);

    /// <summary>
    /// 探测输入法是否已把整句自动上屏（高置信度无需空格）。
    /// 必须与期望全文相等才算——有些输入法会分段提前上屏，文本非空不代表整句已完成。
    /// 探测本身也是一次短静默等待：自动上屏发生时立即返回 true；
    /// 未发生时约消耗 settleMs 毫秒后返回 false。
    /// </summary>
    public bool IsAutoCommitted(string expected, int settleMs, Func<bool>? stop)
        => expected.Length > 0 && Capture(settleMs, Math.Max(settleMs * 4, 200), stop) == expected;

    /// <summary>比对并产出一条逐单元结果。</summary>
    public UnitResult Compare(int index, string keys, string expected, string actual, long elapsedMs)
    {
        var ops = Levenshtein.Ops(expected, actual);
        return new UnitResult
        {
            Index = index,
            Keys = keys,
            Expected = expected,
            Actual = actual,
            Sub = ops.Sub,
            Del = ops.Del,
            Ins = ops.Ins,
            Ms = elapsedMs,
        };
    }

    /// <summary>该期望段是否收束了一个自然句（用于自然句错句率口径）。</summary>
    public static bool ClosesSentence(string expected)
        => expected.Length > 0 && "。！？!?…".Contains(expected[^1]);
}
