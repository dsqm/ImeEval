namespace ImeBench.Core;

/// <summary>
/// 码表：字符 → 真实输入键。
/// 格式：每行 <c>字符&lt;TAB&gt;按键</c>，同一字符多行表示一字多码（多音字 / 多编码方案兼容）。
/// '#' 开头为注释，空行忽略。
/// </summary>
public sealed class CodeTable
{
    private readonly Dictionary<char, List<string>> _map = new();

    public int Count => _map.Count;

    /// <summary>一字多码的字符数（多音字规模，用于提示）。</summary>
    public int AmbiguousCount => _map.Count(kv => kv.Value.Count > 1);

    public bool TryGet(char c, out List<string> codes) => _map.TryGetValue(c, out codes!);

    public bool Has(char c) => _map.ContainsKey(c);

    public void Add(char c, string code)
    {
        if (!_map.TryGetValue(c, out var list))
            _map[c] = list = new List<string>();
        if (!list.Contains(code)) list.Add(code);
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

            table.Add(key[0], code);
        }
        return table;
    }

    /// <summary>合并另一张码表的内容（同一字符的多条码会累积）。</summary>
    public void MergeFrom(CodeTable other)
    {
        foreach (var kv in other._map)
            foreach (string code in kv.Value)
                Add(kv.Key, code);
    }

    public void Save(string path)
    {
        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        foreach (var kv in _map.OrderBy(k => k.Key))
            foreach (string code in kv.Value)
                w.WriteLine($"{kv.Key}\t{code}");
    }
}
