using System.Text;

namespace ImeBench.Core;

public enum IssueKind
{
    /// <summary>码表没覆盖的字符。默认跳过，且不计入 CER 分母（否则会虚高）。</summary>
    Oov,

    /// <summary>一字多码（多音字）。默认取首码，需在清单里确认。</summary>
    Ambiguous,
}

public sealed class CompileIssue
{
    public IssueKind Kind { get; set; }
    public char Ch { get; set; }
    public int CharIndex { get; set; }
    public List<string> Candidates { get; set; } = new();

    /// <summary>用户选定的码（下标）。-1 表示"跳过该字符"。</summary>
    public int ChosenIndex { get; set; }

    public string KindText => Kind == IssueKind.Oov ? "未覆盖" : "多音字";
    public string CandidateText => Candidates.Count == 0 ? "(无)" : string.Join(" / ", Candidates);
}

public sealed class CompileOptions
{
    /// <summary>单个上屏单元的按键长度上限，超过就插入确认键。</summary>
    public int MaxKeysPerUnit { get; set; } = 40;

    /// <summary>遇到标点时切分上屏单元（利用 IME 的标点顶屏）。</summary>
    public bool PunctCommits { get; set; } = true;

    /// <summary>确认键（默认空格）。</summary>
    public string CommitKeys { get; set; } = " ";

    /// <summary>音节分隔符（双拼/五笔可能需要；全拼留空）。不上屏。</summary>
    public string SeparatorKeys { get; set; } = "";

    /// <summary>ASCII 字符码表没覆盖时原样发键。</summary>
    public bool AsciiPassthrough { get; set; } = true;

    /// <summary>换行直接切分单元，不产生按键。</summary>
    public bool SkipNewline { get; set; } = true;

    /// <summary>视为标点的字符集合。</summary>
    public string Punctuation { get; set; } = "，。！？；：、,.!?;:？！";
}

public sealed class CompileResult
{
    public KeyTable Table { get; set; } = new();
    public List<CompileIssue> Issues { get; set; } = new();
    public int SourceChars { get; set; }
    public int CoveredChars { get; set; }

    public int OovCount => Issues.Count(i => i.Kind == IssueKind.Oov);
    public int AmbiguousCount => Issues.Count(i => i.Kind == IssueKind.Ambiguous);

    public override string ToString()
        => $"单元 {Table.UnitCount} 个 / 按键 {Table.TotalKeys} 个 / 覆盖 {CoveredChars}/{SourceChars} 字" +
           $" / 未覆盖 {OovCount} / 多音字 {AmbiguousCount}";
}

/// <summary>
/// 把「文章 + 码表」编译成可执行的按键表。
///
/// 歧义（OOV、多音字）在这一步就暴露成清单交给用户确认，
/// 而不是留到运行时 —— 运行时零歧义，按键表本身就是可复现的测试脚本。
/// </summary>
public static class Compiler
{
    public static CompileResult Build(string article, CodeTable table, CompileOptions opt)
    {
        var res = new CompileResult { SourceChars = article.Length };
        var kt = new KeyTable();
        var expected = new StringBuilder();

        KeyUnit unit = new() { ExpectedStart = 0 };
        int unitKeys = 0;

        void CloseUnit(bool withCommit)
        {
            if (unit.Tokens.Count == 0) return;

            if (withCommit && !unit.Tokens[^1].IsCommit)
                unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Commit, Keys = opt.CommitKeys });

            unit.ExpectedLength = expected.Length - unit.ExpectedStart;
            if (unit.ExpectedLength > 0)
                kt.Units.Add(unit);

            unit = new KeyUnit { ExpectedStart = expected.Length };
            unitKeys = 0;
        }

        for (int i = 0; i < article.Length; i++)
        {
            char c = article[i];

            if (opt.SkipNewline && (c == '\r' || c == '\n'))
            {
                CloseUnit(withCommit: false);
                continue;
            }

            bool has = table.TryGet(c, out List<string> codes);
            string keys;

            if (has)
            {
                if (codes.Count > 1)
                {
                    res.Issues.Add(new CompileIssue
                    {
                        Kind = IssueKind.Ambiguous,
                        Ch = c,
                        CharIndex = i,
                        Candidates = codes,
                        ChosenIndex = 0,
                    });
                }
                keys = codes[0];
            }
            else if (opt.AsciiPassthrough && c < 128)
            {
                keys = c.ToString();
            }
            else
            {
                res.Issues.Add(new CompileIssue
                {
                    Kind = IssueKind.Oov,
                    Ch = c,
                    CharIndex = i,
                    ChosenIndex = -1,
                });
                continue; // 跳过，且不计入期望文本（= 不计入 CER 分母）
            }

            // 需要分隔符时（双拼/五笔），在相邻编码之间插入
            if (opt.SeparatorKeys.Length > 0
                && unit.Tokens.Count > 0
                && unit.Tokens[^1].Kind == KeyTokenKind.Key)
            {
                unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Sep, Keys = opt.SeparatorKeys });
            }

            unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Key, Keys = keys, Target = c });
            unitKeys += keys.Length;
            expected.Append(c);
            res.CoveredChars++;

            bool isPunct = opt.PunctCommits && opt.Punctuation.Contains(c);
            if (isPunct)
                CloseUnit(withCommit: false);   // 标点自己会顶屏，不再补空格，避免多打一个空格
            else if (unitKeys >= opt.MaxKeysPerUnit)
                CloseUnit(withCommit: true);
        }

        CloseUnit(withCommit: true);
        kt.ExpectedText = expected.ToString();
        res.Table = kt;
        return res;
    }

    /// <summary>
    /// 用户确认清单后重新编译：按 ChosenIndex 覆盖默认取码。
    /// ChosenIndex = -1 表示该字符跳过。
    /// </summary>
    public static CompileResult Rebuild(string article, CodeTable table, CompileOptions opt,
                                        IEnumerable<CompileIssue> resolved)
    {
        var chosen = new Dictionary<int, string?>();
        foreach (var iss in resolved)
        {
            if (iss.Kind == IssueKind.Oov)
            {
                if (iss.ChosenIndex >= 0 && iss.ChosenIndex < iss.Candidates.Count)
                    chosen[iss.CharIndex] = iss.Candidates[iss.ChosenIndex];
                else
                    chosen[iss.CharIndex] = null;
            }
            else if (iss.ChosenIndex >= 0 && iss.ChosenIndex < iss.Candidates.Count)
            {
                chosen[iss.CharIndex] = iss.Candidates[iss.ChosenIndex];
            }
        }

        var res = new CompileResult { SourceChars = article.Length };
        var kt = new KeyTable();
        var expected = new StringBuilder();

        KeyUnit unit = new() { ExpectedStart = 0 };
        int unitKeys = 0;

        void CloseUnit(bool withCommit)
        {
            if (unit.Tokens.Count == 0) return;
            if (withCommit && !unit.Tokens[^1].IsCommit)
                unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Commit, Keys = opt.CommitKeys });
            unit.ExpectedLength = expected.Length - unit.ExpectedStart;
            if (unit.ExpectedLength > 0) kt.Units.Add(unit);
            unit = new KeyUnit { ExpectedStart = expected.Length };
            unitKeys = 0;
        }

        for (int i = 0; i < article.Length; i++)
        {
            char c = article[i];

            if (opt.SkipNewline && (c == '\r' || c == '\n')) { CloseUnit(false); continue; }

            string keys;
            if (chosen.TryGetValue(i, out var forced))
            {
                if (forced == null) { res.Issues.Add(new CompileIssue { Kind = IssueKind.Oov, Ch = c, CharIndex = i, ChosenIndex = -1 }); continue; }
                keys = forced;
            }
            else if (table.TryGet(c, out List<string> codes))
            {
                keys = codes[0];
            }
            else if (opt.AsciiPassthrough && c < 128)
            {
                keys = c.ToString();
            }
            else
            {
                res.Issues.Add(new CompileIssue { Kind = IssueKind.Oov, Ch = c, CharIndex = i, ChosenIndex = -1 });
                continue;
            }

            if (opt.SeparatorKeys.Length > 0 && unit.Tokens.Count > 0 && unit.Tokens[^1].Kind == KeyTokenKind.Key)
                unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Sep, Keys = opt.SeparatorKeys });

            unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Key, Keys = keys, Target = c });
            unitKeys += keys.Length;
            expected.Append(c);
            res.CoveredChars++;

            bool isPunct = opt.PunctCommits && opt.Punctuation.Contains(c);
            if (isPunct) CloseUnit(false);
            else if (unitKeys >= opt.MaxKeysPerUnit) CloseUnit(true);
        }

        CloseUnit(true);
        kt.ExpectedText = expected.ToString();
        res.Table = kt;
        return res;
    }
}
