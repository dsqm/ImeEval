using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImeBench.Core;

public sealed class CompileOptions
{
    /// <summary>单句（上屏单元）的字数上限，超过就分段上屏。按「字」数，代理对（如 emoji、生僻字）算 1 字。</summary>
    public int MaxCharsPerUnit { get; set; } = 20;

    /// <summary>句尾等待（毫秒）：每句结尾、上屏键之前插入 {DELAY=n}。0 = 不插入。</summary>
    public int TailDelayMs { get; set; } = 0;

    /// <summary>上屏键（每句结尾追加）。默认 "_" = 空格。</summary>
    public string CommitKeys { get; set; } = "_";

    /// <summary>根据标点断句：遇到标点结束这一句（连续的标点算同一句，如 ！？）。</summary>
    public bool SplitByPunctuation { get; set; } = true;

    /// <summary>标点字符 → 按键串（如 ！ → &lt;Shift+1&gt;）。由标点配置生成，与码表分开维护。</summary>
    public Dictionary<char, string> Punctuation { get; set; } = PunctuationConfig.Parse(PunctuationConfig.DefaultText);

    /// <summary>音节分隔符（双拼/五笔可能需要；全拼留空）。不上屏。</summary>
    public string SeparatorKeys { get; set; } = "";

    /// <summary>换行直接切分单元，收尾与普通句子相同（句尾延迟 + 上屏键）。</summary>
    public bool SkipNewline { get; set; } = true;

}

public sealed class CompileResult
{
    public KeyTable Table { get; set; } = new();

    /// <summary>文章的文本元素总数（代理对算 1 字）。</summary>
    public int SourceChars { get; set; }

    /// <summary>码表覆盖、参与测试的字数。</summary>
    public int CoveredChars { get; set; }

    /// <summary>码表没有、被直接忽略排除的字数（不计入 CER 分母）。</summary>
    public int OovSkipped { get; set; }

    /// <summary>一字多码的字数（取首码）。</summary>
    public int Ambiguous { get; set; }

    public override string ToString()
        => $"单元 {Table.UnitCount} 个 / 按键 {Table.TotalKeys} 个 / 覆盖 {CoveredChars}/{SourceChars} 字" +
           $" / OOV 跳过 {OovSkipped} / 多音字取首码 {Ambiguous}";
}

/// <summary>
/// 把「文章 + 码表」编译成可执行的按键表。
///
/// 规则（2026-09-08 语料页重构版）：
///   1. 码表没有的字符（OOV）直接忽略排除，不出清单、不计入期望文本（= 不计入 CER 分母）；
///   2. 一字多码取首码，不再打断流程确认；
///   3. 上屏单元按「字数」切分（代理对算 1 字），默认 20 字；
///   4. 句尾等待 &gt; 0 时，每个单元的收尾键（上屏键或标点键）之前插入 {DELAY=n}；
///   5. 标点顶屏：标点收尾不加空格确认键（标点自己顶屏），但同样享受句尾等待。
/// </summary>
public static class Compiler
{
    public static CompileResult Build(string article, CodeTable table, CompileOptions opt)
    {
        var res = new CompileResult { SourceChars = new StringInfo(article).LengthInTextElements };
        var kt = new KeyTable();
        var expected = new StringBuilder();

        var unit = new KeyUnit { ExpectedStart = 0 };
        int unitChars = 0;
        bool pendingPunctClose = false;   // 刚发完标点：等连续标点发完再收尾这一句

        void CloseUnit(bool withCommit)
        {
            if (unit.Tokens.Count == 0) { unitChars = 0; return; }

            if (withCommit)
            {
                if (opt.TailDelayMs > 0 && !IsDelay(unit.Tokens[^1]))
                    unit.Tokens.Add(DelayToken(opt.TailDelayMs));
                if (!unit.Tokens[^1].IsCommit)
                    unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Commit, Keys = opt.CommitKeys });
            }

            unit.ExpectedLength = expected.Length - unit.ExpectedStart;
            if (unit.ExpectedLength > 0) kt.Units.Add(unit);

            unit = new KeyUnit { ExpectedStart = expected.Length };
            unitChars = 0;
        }

        var si = new StringInfo(article);
        for (int e = 0; e < si.LengthInTextElements; e++)
        {
            string elem = si.SubstringByTextElements(e, 1);
            char c = elem[0];

            if (opt.SkipNewline && (c == '\r' || c == '\n'))
            {
                // 标点已收尾（标点自己顶屏）就不再补上屏键；否则补句尾延迟 + 上屏键
                CloseUnit(!pendingPunctClose);
                pendingPunctClose = false;
                continue;
            }

            bool isPunct = opt.SplitByPunctuation && opt.Punctuation.ContainsKey(c);

            // 进入非标点字符：上一句（标点结尾）到此收尾——标点自己顶屏，不再补上屏键
            if (!isPunct && pendingPunctClose)
            {
                CloseUnit(false);
                pendingPunctClose = false;
            }

            string keys;
            if (isPunct)
            {
                keys = opt.Punctuation[c];
            }
            else if (table.TryGet(c, out var codes))
            {
                if (codes.Count > 1) res.Ambiguous++;
                keys = codes[0];
            }
            else
            {
                res.OovSkipped++;   // 直接忽略排除，不计入期望文本（= 不计入 CER 分母）
                continue;
            }

            // 句尾等待：只在连续标点的第一个标点之前插一次（例：...{DELAY=100}<Shift+1>）
            if (isPunct && opt.TailDelayMs > 0 && unit.Tokens.Count > 0 && !pendingPunctClose)
                unit.Tokens.Add(DelayToken(opt.TailDelayMs));

            if (opt.SeparatorKeys.Length > 0 && unit.Tokens.Count > 0 && unit.Tokens[^1].Kind == KeyTokenKind.Key)
                unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Sep, Keys = opt.SeparatorKeys });

            unit.Tokens.Add(new KeyToken { Kind = KeyTokenKind.Key, Keys = keys, Target = c });
            expected.Append(elem);          // 整个文本元素一起入期望文本，代理对不会被拆开
            res.CoveredChars++;
            unitChars++;

            if (isPunct)
                pendingPunctClose = true;   // 连续标点放同一句，等后续字符到来再收尾
            else if (unitChars >= Math.Max(1, opt.MaxCharsPerUnit))
                CloseUnit(true);            // CloseUnit 里会先插句尾等待，再插上屏键
        }

        if (pendingPunctClose) CloseUnit(false); else CloseUnit(true);
        kt.ExpectedText = expected.ToString();
        res.Table = kt;
        return res;
    }

    private static bool IsDelay(KeyToken t)
        => t.Kind == KeyTokenKind.Key && t.Keys.Contains("{DELAY=", StringComparison.OrdinalIgnoreCase);

    private static KeyToken DelayToken(int ms)
        => new() { Kind = KeyTokenKind.Key, Keys = $"{{DELAY={ms}}}" };
}

/// <summary>便携化运行时设置（存 exe 文件夹 data\settings.json，不碰注册表和用户目录）。</summary>
public sealed class CorpusSettings
{
    public string Code { get; set; } = "";
    public string Article { get; set; } = "";
    public int MaxCharsPerUnit { get; set; } = 20;
    public int TailDelayMs { get; set; } = 0;
    public string CommitKeys { get; set; } = "_";
    public bool PunctCommits { get; set; } = true;   // 兼容旧设置（= SplitByPunctuation）

    /// <summary>标点配置的原始文本，落盘用。</summary>
    public string PunctKeys { get; set; } = "";

    // 窗口大小记忆（0 = 未记录过，用默认尺寸）
    public int WindowWidth { get; set; } = 0;
    public int WindowHeight { get; set; } = 0;
    public bool WindowMaximized { get; set; } = false;

    // 运行页参数
    public int IntervalMs { get; set; } = 2;
    public int SettleMs { get; set; } = 40;
    public bool OnlyErrors { get; set; } = true;

    // 标定页参数
    public string CalibKeys { get; set; } = "vsgo_";
    public string CalibExpect { get; set; } = "中国";
    public int CalibRepeats { get; set; } = 20;
    public int CalibMargin { get; set; } = 1;

    [JsonIgnore] public string FilePath { get; set; } = "";

    public static CorpusSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<CorpusSettings>(File.ReadAllText(path)) ?? new CorpusSettings();
        }
        catch { }
        return new CorpusSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* exe 目录只读等场景：静默失败，不影响使用 */ }
    }
}
