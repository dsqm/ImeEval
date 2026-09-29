using System.Globalization;

namespace ImeBench.Core;

/// <summary>
/// 码表：文本元素（单字 / 词组）→ 真实输入键。
/// 格式：每行 <c>字符或词组&lt;TAB&gt;按键</c>，同一左列多行表示一码多码（多音字 / 多编码方案兼容）。
/// '#' 开头为注释，空行忽略。
///
/// 两条与 Unicode 相关的硬约定：
///   1. 左列按【文本元素】切分，不以 char 为键 —— 高低代理对（emoji、扩展 B 区汉字等）
///      是一个整体，两个不同字不会因为共用高位代理而被误判成同一个字；
///   2. 左列含 2 个及以上文本元素 = 词组，编译时最长优先匹配，命中则整词一次输入。
/// </summary>
public sealed class CodeTable
{
    private readonly Dictionary<string, List<string>> _chars = new();
    private readonly Dictionary<string, List<string>> _phrases = new();

    /// <summary>单字条数。</summary>
    public int Count => _chars.Count;

    /// <summary>词组条数。</summary>
    public int PhraseCount => _phrases.Count;

    /// <summary>最长词组含多少个文本元素（编译时最长匹配的上界）。</summary>
    public int MaxPhraseElements { get; private set; }

    /// <summary>一字多码的字数（多音字规模，用于提示）。</summary>
    public int AmbiguousCount => _chars.Count(kv => kv.Value.Count > 1);

    /// <summary>单字查表。textElement 必须是完整的一个文本元素（代理对要整对传进来）。</summary>
    public bool TryGet(string textElement, out List<string> codes) => _chars.TryGetValue(textElement, out codes!);

    public bool Has(string textElement) => _chars.ContainsKey(textElement);

    /// <summary>词组查表。</summary>
    public bool TryGetPhrase(string phrase, out List<string> codes) => _phrases.TryGetValue(phrase, out codes!);

    public void Add(string key, string code)
    {
        if (key.Length == 0 || code.Length == 0) return;

        // 按文本元素个数分流：≥2 是词组，1 是单字。
        // 只用长度判断会踩坑（一个代理对字 = 2 个 char），必须走 StringInfo。
        int n = new StringInfo(key).LengthInTextElements;
        var map = n >= 2 ? _phrases : _chars;

        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<string>();
        if (!list.Contains(code)) list.Add(code);

        if (n > MaxPhraseElements) MaxPhraseElements = n;
    }

    public static CodeTable Load(string path)
    {
        var table = new CodeTable();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // 分隔符：TAB 或一个或多个空格都行
            string[] parts = line.Split(new[] { '\t', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string key = parts[0];
            string code = parts[1].Trim();
            if (key.Length == 0 || code.Length == 0) continue;

            table.Add(key, code);
        }
        return table;
    }

    /// <summary>
    /// 在 article 的 offset 处做词组最长匹配。
    /// 只在文本元素边界上取候选（starts 是元素起点表），所以代理对 / 组合字符不会被切开。
    /// </summary>
    /// <param name="article">全文。</param>
    /// <param name="offset">匹配起点（必须是某个文本元素的起点）。</param>
    /// <param name="elemIdx">offset 对应的文本元素序号。</param>
    /// <param name="starts">文本元素起点表，长度 = 元素数 + 1，末位是全文长度。</param>
    /// <param name="endOffset">命中时返回词组的结束偏移。</param>
    /// <returns>命中的按键串；未命中返回 null。</returns>
    public string? MatchPhraseAt(string article, int offset, int elemIdx, int[] starts, out int endOffset)
    {
        endOffset = offset;
        if (_phrases.Count == 0) return null;

        // 词组不跨行：换行是单元边界，跨行词组没有意义
        int maxK = Math.Min(MaxPhraseElements, starts.Length - 1 - elemIdx);
        for (int k = maxK; k >= 2; k--)
        {
            int end = starts[elemIdx + k];
            string cand = article.Substring(offset, end - offset);
            if (ContainsNewline(cand)) continue;

            if (_phrases.TryGetValue(cand, out var codes) && codes.Count > 0)
            {
                endOffset = end;
                return codes[0];
            }
        }
        return null;
    }

    private static bool ContainsNewline(string s)
        => s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;

    /// <summary>合并另一张码表的内容（同一左列的多条码会累积）。</summary>
    public void MergeFrom(CodeTable other)
    {
        foreach (var kv in other._chars)
            foreach (string code in kv.Value)
                Add(kv.Key, code);
        foreach (var kv in other._phrases)
            foreach (string code in kv.Value)
                Add(kv.Key, code);
    }

    public void Save(string path)
    {
        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        foreach (var kv in _chars.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (string code in kv.Value)
                w.WriteLine($"{kv.Key}\t{code}");
        foreach (var kv in _phrases.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (string code in kv.Value)
                w.WriteLine($"{kv.Key}\t{code}");
    }
}
