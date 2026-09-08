namespace ImeBench.Core;

/// <summary>
/// 标点输入键配置：标点字符 → 按键串。
/// 与码表分开维护：标点走「输入法顶屏」逻辑，按键也常是组合键（如 ！ = &lt;Shift+1&gt;）。
/// </summary>
public static class PunctuationConfig
{
    /// <summary>默认配置：常见中文标点（一行一条：字符 + 空格/制表符 + 按键串）。</summary>
    public static string DefaultText => string.Join('\n', new[]
    {
        "。 .",
        "， ,",
        "、 \\",
        "； ;",
        "： <Shift+;>",
        "？ <Shift+/>",
        "！ <Shift+1>",
        "（ <Shift+9>",
        "） <Shift+0>",
        "“ <Shift+'>",
        "” <Shift+'>",
        "《 <Shift+,>",
        "》 <Shift+.>",
        "— <Shift+->",
        "… <Shift+6>",
        ".  .",
        ",  ,",
        "!  <Shift+1>",
        "?  <Shift+/>",
        ";  ;",
        ":  <Shift+;>",
    });

    /// <summary>解析配置文本。首列必须是单个字符，之后是按键串（空格或制表符分隔）。</summary>
    public static Dictionary<char, string> Parse(string? text, List<string>? errors = null)
    {
        var map = new Dictionary<char, string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim('\r', '\n').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0].Length != 1)
            {
                errors?.Add($"标点配置无效（首列须为单个字符）：{line}");
                continue;
            }
            map[parts[0][0]] = parts[1].Trim();
        }
        return map;
    }
}
