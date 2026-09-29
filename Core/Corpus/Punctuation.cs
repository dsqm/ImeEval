namespace ImeEval.Core;

/// <summary>
/// 标点输入键配置：标点串 → 按键串。
/// 与码表分开维护：标点走「输入法顶屏」逻辑，按键常是组合键（如 ！ = &lt;Shift+1&gt;），
/// 且一次按键可能产出多个字符（Shift+6 → ……、Shift+- → ——），所以左列允许多个字符。
/// </summary>
public static class PunctuationConfig
{
    /// <summary>默认配置：常见中文标点（一行一条：标点 + 空格/制表符 + 按键串）。</summary>
    public static string DefaultText => string.Join('\n', new[]
    {
        "。  .",
        "，  ,",
        "、  \\",
        "；  ;",
        "：  <Shift+;>",
        "？  <Shift+/>",
        "！  <Shift+1>",
        "（  <Shift+9>",
        "）  <Shift+0>",
        "“  <Shift+'>",
        "”  <Shift+'>",
        "《  <Shift+,>",
        "》  <Shift+.>",
        "【  <Shift+[>",
        "】  <Shift+]>",
        "——  <Shift+->",
        "……  <Shift+6>",
    });

    /// <summary>解析配置文本。左列是标点（一或多个字符），右列是按键串。</summary>
    public static Dictionary<string, string> Parse(string? text, List<string>? errors = null)
    {
        var map = new Dictionary<string, string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim('\r', '\n').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string punct = parts[0].Trim();
            if (punct.Length == 0)
            {
                errors?.Add($"标点配置无效（左列为空）：{line}");
                continue;
            }
            map[punct] = parts[1].Trim();
        }
        return map;
    }

    /// <summary>
    /// 在 text 的 start 位置做最长匹配，返回命中的标点串；未命中返回 null。
    /// 表为空时自然不命中——此时该字符走码表，码表也没有就当 OOV 过滤掉。
    /// </summary>
    public static string? MatchAt(string text, int start, Dictionary<string, string> map)
    {
        if (map.Count == 0) return null;
        string? hit = null;
        foreach (var kv in map)
        {
            if (kv.Key.Length == 0 || (hit != null && kv.Key.Length <= hit.Length)) continue;
            if (start + kv.Key.Length <= text.Length &&
                string.CompareOrdinal(text, start, kv.Key, 0, kv.Key.Length) == 0)
                hit = kv.Key;
        }
        return hit;
    }
}
