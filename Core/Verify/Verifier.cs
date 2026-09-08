namespace ImeBench.Core;

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

    /// <summary>夺回焦点，保证后续按键确实打给宿主。</summary>
    public void Focus() => _sink.FocusSafe();

    /// <summary>捕获本次上屏的文本。</summary>
    public string Capture(int settleMs, int maxMs, Func<bool>? stop)
        => _sink.WaitStable(settleMs, maxMs, stop);

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

    /// <summary>该期望段是否收束了一个自然句（用于自然句 SER 口径）。</summary>
    public static bool ClosesSentence(string expected)
        => expected.Length > 0 && "。！？!?…".Contains(expected[^1]);
}
