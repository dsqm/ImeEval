namespace ImeBench.Core;

/// <summary>编辑距离的三类细分。</summary>
public readonly record struct EditOps(int Sub, int Del, int Ins)
{
    public int Total => Sub + Del + Ins;

    public static EditOps operator +(EditOps a, EditOps b)
        => new(a.Sub + b.Sub, a.Del + b.Del, a.Ins + b.Ins);

    public override string ToString() => $"替{Sub} 删{Del} 插{Ins}";
}

/// <summary>
/// Levenshtein 编辑距离，并细分替换 / 删除 / 插入三类。
/// 约定：a = 期望，b = 实际。
///   Del = 期望有、实际没有（少字）
///   Ins = 实际多出来的（多字）
/// </summary>
public static class Levenshtein
{
    // 复用缓冲，避免每句都分配一个二维数组（上万句时 GC 压力显著）
    private static int[,] _buf = new int[128, 128];

    private static void Ensure(int n, int m)
    {
        if (_buf.GetLength(0) <= n || _buf.GetLength(1) <= m)
            _buf = new int[Math.Max(n + 8, _buf.GetLength(0) * 2), Math.Max(m + 8, _buf.GetLength(1) * 2)];
    }

    public static int Distance(string a, string b) => Ops(a, b).Total;

    public static EditOps Ops(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return new EditOps(0, 0, m);
        if (m == 0) return new EditOps(0, n, 0);

        Ensure(n, m);
        int[,] d = _buf;

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            char ca = a[i - 1];
            for (int j = 1; j <= m; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                int diag = d[i - 1, j - 1] + cost;
                int del = d[i - 1, j] + 1;
                int ins = d[i, j - 1] + 1;
                int v = diag < del ? diag : del;
                if (ins < v) v = ins;
                d[i, j] = v;
            }
        }

        // 回溯分类
        int sub = 0, dl = 0, ins2 = 0;
        int x = n, y = m;
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1]) { x--; y--; continue; }
            int diag = d[x - 1, y - 1], up = d[x - 1, y], left = d[x, y - 1];
            if (diag <= up && diag <= left) { sub++; x--; y--; }
            else if (up <= left) { dl++; x--; }
            else { ins2++; y--; }
        }
        dl += x;
        ins2 += y;

        return new EditOps(sub, dl, ins2);
    }
}

/// <summary>单个上屏单元的结果。</summary>
public sealed class UnitResult
{
    public int Index { get; set; }
    public string Keys { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public int Sub { get; set; }
    public int Del { get; set; }
    public int Ins { get; set; }
    public long Ms { get; set; }

    /// <summary>true = 输入法高置信度自动上屏，本次跳过了上屏键。</summary>
    public bool AutoCommit { get; set; }

    /// <summary>本句期间 IME 真正提交文本的次数（GCS_RESULTSTR 事件数；一字一屏=字数，整句一屏=1）。</summary>
    public int CommitEvents { get; set; }

    public int Errors => Sub + Del + Ins;
    public bool Correct => Errors == 0;

    public string Flag => Correct ? "✓" : "✗";
}

/// <summary>
/// 汇总统计。
///   CER = 错字数 / 期望总字数
///   SER = 错句数 / 总句数（默认按"上屏次数"口径）
/// </summary>
public sealed class BenchStats
{
    public int DoneUnits { get; set; }
    public int ErrorUnits { get; set; }          // 上屏口径
    public int DoneSentences { get; set; }       // 自然句口径
    public int ErrorSentences { get; set; }
    public int ExpectedChars { get; set; }
    public int ActualChars { get; set; }
    public int Sub { get; set; }
    public int Del { get; set; }
    public int Ins { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>IME 真正提交文本的事件总数（GCS_RESULTSTR 次数；一字一屏会远大于句数）。</summary>
    public int CommitEvents { get; set; }

    public int ErrorChars => Sub + Del + Ins;

    public double Cer => ExpectedChars == 0 ? 0 : (double)ErrorChars / ExpectedChars;
    public double Ser => DoneUnits == 0 ? 0 : (double)ErrorUnits / DoneUnits;
    public double SentenceSer => DoneSentences == 0 ? 0 : (double)ErrorSentences / DoneSentences;

    /// <summary>准确率，直观一点。</summary>
    public double Accuracy => 1.0 - Cer;

    public double UnitsPerSec => ElapsedMs == 0 ? 0 : DoneUnits * 1000.0 / ElapsedMs;

    public void Add(UnitResult u, bool closesSentence)
    {
        DoneUnits++;
        if (!u.Correct) ErrorUnits++;
        ExpectedChars += u.Expected.Length;
        ActualChars += u.Actual.Length;
        Sub += u.Sub;
        Del += u.Del;
        Ins += u.Ins;
        CommitEvents += u.CommitEvents;

        _openErrors += u.Errors;
        if (closesSentence)
        {
            DoneSentences++;
            if (_openErrors > 0) ErrorSentences++;
            _openErrors = 0;
        }
    }

    private int _openErrors;

    /// <summary>从断点恢复时载入已累计的统计。</summary>
    public void CopyFrom(BenchStats o)
    {
        DoneUnits = o.DoneUnits;
        ErrorUnits = o.ErrorUnits;
        DoneSentences = o.DoneSentences;
        ErrorSentences = o.ErrorSentences;
        ExpectedChars = o.ExpectedChars;
        ActualChars = o.ActualChars;
        Sub = o.Sub;
        Del = o.Del;
        Ins = o.Ins;
        CommitEvents = o.CommitEvents;
        ElapsedMs = o.ElapsedMs;
        _openErrors = 0;
    }

    public string Summary()
        => $"上屏 {DoneUnits} 句，IME 实际提交 {CommitEvents} 次，错 {ErrorUnits} 句（SER {Ser:P2}）；" +
           $"期望 {ExpectedChars} 字，错 {ErrorChars} 字（CER {Cer:P2}，准确率 {Accuracy:P2}）；" +
           $"替换 {Sub} / 删除 {Del} / 插入 {Ins}；" +
           $"自然句 {DoneSentences} 句，错 {ErrorSentences} 句（SER {SentenceSer:P2}）；" +
           $"耗时 {ElapsedMs / 1000.0:F1}s（{UnitsPerSec:F1} 句/秒）";
}
