using System.Text;

namespace ImeBench.Core;

/// <summary>按键串条目：一个按键、一个原样字符，或一段延迟。</summary>
public sealed class KeySeqItem
{
    public enum ItemKind { Vk, Char, Delay }

    public ItemKind Kind { get; init; }

    /// <summary>Kind=Vk：要发送的虚拟键码。</summary>
    public ushort Vk { get; init; }

    /// <summary>Kind=Char：原样字符（交给 InputSender 的 ASCII 表 / VkKeyScanEx / Unicode 兜底链）。</summary>
    public char Char { get; init; }

    /// <summary>Kind=Delay：等待毫秒数。</summary>
    public int DelayMs { get; init; }

    /// <summary>原文片段（报错定位用）。</summary>
    public string Source { get; init; } = "";
}

/// <summary>
/// 按键串语法（发送层的输入 DSL，码表按键列 / 标定按键串共用）：
///
///   _           → 空格
///   空格 / 制表符 → 直接忽略，当作不存在（避免肉眼分不清的空格混进来）
///   &lt;Shift&gt;   → 特殊键，支持 F1~F24、退格、回车、方向键、修饰键等全部可发送按键
///   {DELAY=100} → 在此等待 100 毫秒
///   其余字符     → 按普通字符发送（ASCII 虚拟键表 → VkKeyScanEx → Unicode 兜底）
///
/// 解析永不抛异常：无法识别的片段记入 errors 列表并跳过，保证测试不因一处笔误中断。
/// </summary>
public static class KeySeq
{
    // ---- 虚拟键码（Win32 标准值，本地定义避免依赖 Native 的覆盖范围） ----
    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10,
        VK_CONTROL = 0x11, VK_MENU = 0x12, VK_PAUSE = 0x13, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B,
        VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D, VK_DELETE = 0x2E,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D,
        VK_NUMPAD0 = 0x60, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91, VK_F1 = 0x70;

    /// <summary>把按键名翻译成虚拟键码。名字大小写不敏感，支持中英文别名。</summary>
    public static bool TryNamedVk(string name, out ushort vk)
    {
        vk = 0;
        switch (name.Trim().ToLowerInvariant())
        {
            case "shift": vk = VK_SHIFT; return true;
            case "ctrl": case "control": vk = VK_CONTROL; return true;
            case "alt": vk = VK_MENU; return true;
            case "win": case "windows": vk = VK_LWIN; return true;
            case "enter": case "return": case "回车": vk = VK_RETURN; return true;
            case "backspace": case "bs": case "退格": vk = VK_BACK; return true;
            case "esc": case "escape": vk = VK_ESCAPE; return true;
            case "tab": case "制表": vk = VK_TAB; return true;
            case "space": case "空格": vk = VK_SPACE; return true;
            case "del": case "delete": case "删除": vk = VK_DELETE; return true;
            case "ins": case "insert": case "插入": vk = VK_INSERT; return true;
            case "home": vk = VK_HOME; return true;
            case "end": vk = VK_END; return true;
            case "pageup": case "pgup": case "上翻": vk = VK_PRIOR; return true;
            case "pagedown": case "pgdn": case "下翻": vk = VK_NEXT; return true;
            case "up": case "↑": vk = VK_UP; return true;
            case "down": case "↓": vk = VK_DOWN; return true;
            case "left": case "←": vk = VK_LEFT; return true;
            case "right": case "→": vk = VK_RIGHT; return true;
            case "capslock": case "caps": vk = VK_CAPITAL; return true;
            case "numlock": vk = VK_NUMLOCK; return true;
            case "scrolllock": vk = VK_SCROLL; return true;
            case "printscreen": case "prtsc": vk = VK_SNAPSHOT; return true;
            case "pause": vk = VK_PAUSE; return true;
            case "menu": case "apps": vk = VK_APPS; return true;
            case "numpad0": vk = VK_NUMPAD0; return true;
        }

        if (name.Length == 2 && (name[0] == 'f' || name[0] == 'F') && name[1] >= '1' && name[1] <= '9'
            && int.TryParse(name.AsSpan(1), out int f) && f >= 1 && f <= 24)
        {
            vk = (ushort)(VK_F1 + f - 1);
            return true;
        }
        if (name.Length == 3 && name.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(1), out int f2) && f2 >= 10 && f2 <= 24)
        {
            vk = (ushort)(VK_F1 + f2 - 1);
            return true;
        }
        return false;
    }

    /// <summary>解析按键串。无法识别的片段记入 errors（若提供）并跳过，永不抛异常。</summary>
    public static List<KeySeqItem> Parse(string? s, List<string>? errors = null)
    {
        var items = new List<KeySeqItem>();
        if (string.IsNullOrEmpty(s)) return items;

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            if (c == ' ' || c == '\t')
            {
                i++;                       // 真实空格：当作不存在
                continue;
            }

            if (c == '_')
            {
                items.Add(new KeySeqItem { Kind = KeySeqItem.ItemKind.Vk, Vk = VK_SPACE, Source = "_" });
                i++;
                continue;
            }

            if (c == '{')
            {
                int end = s.IndexOf('}', i);
                if (end < 0)
                {
                    errors?.Add($"未闭合的指令：{s[i..]}");
                    break;
                }
                string body = s[(i + 1)..end].Trim();
                int val = -1;
                if (body.StartsWith("DELAY=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(body[6..].Trim(), out val);
                if (val >= 0)
                    items.Add(new KeySeqItem { Kind = KeySeqItem.ItemKind.Delay, DelayMs = val, Source = s[i..(end + 1)] });
                else
                    errors?.Add($"无法识别的指令 {{{body}}}（目前只支持 {{DELAY=毫秒}}）");
                i = end + 1;
                continue;
            }

            if (c == '<')
            {
                int end = s.IndexOf('>', i);
                if (end < 0)
                {
                    errors?.Add($"未闭合的特殊键：{s[i..]}");
                    break;
                }
                string name = s[(i + 1)..end].Trim();
                if (TryNamedVk(name, out ushort vk))
                    items.Add(new KeySeqItem { Kind = KeySeqItem.ItemKind.Vk, Vk = vk, Source = s[i..(end + 1)] });
                else
                    errors?.Add($"无法识别的按键名 <{name}>");
                i = end + 1;
                continue;
            }

            items.Add(new KeySeqItem { Kind = KeySeqItem.ItemKind.Char, Char = c, Source = c.ToString() });
            i++;
        }
        return items;
    }

    /// <summary>这段按键串实际会发送多少个按键（不含延迟）。</summary>
    public static int CountKeys(string? s)
        => s == null ? 0 : Parse(s).Count(it => it.Kind != KeySeqItem.ItemKind.Delay);
}
