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

            string[] parts = line.Split('\t');
            if (parts.Length < 2) parts = line.Split(new[] { ' ' }, 2);
            if (parts.Length < 2) continue;

            string key = parts[0];
            string code = parts[1].Trim();
            if (key.Length == 0 || code.Length == 0) continue;

            table.Add(key[0], code);
        }
        return table;
    }

    /// <summary>加载一个文件夹里的所有 .txt 码表并合并（文件名排序，后加载的码排在前一码之后）。</summary>
    public static (CodeTable Table, List<string> Files) LoadFolder(string dir)
    {
        var table = new CodeTable();
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        foreach (string f in files)
        {
            try { table.MergeFrom(CodeTable.Load(f)); } catch { /* 单个文件坏了不影响其他 */ }
        }
        return (table, files);
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

    /// <summary>一份最小全拼样例码表，方便用户照着改（不含汉字注音，只是格式示例）。</summary>
    public static string SampleText => """
        # 码表格式：字符<TAB>按键串
        # 同一字符写多行 = 一字多码（多音字），编译时会在清单里让你指定用哪个
        # 示例（简码，仅演示格式）：
        中	zhong
        国	guo
        人	ren
        民	min
        万	wan
        岁	sui
        ，	,
        。	.
        """;
}
