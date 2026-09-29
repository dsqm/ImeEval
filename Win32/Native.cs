using System.Runtime.InteropServices;
using System.Text;

namespace ImeEval.Win32;

/// <summary>
/// Win32 / IMM32 P/Invoke 声明。
/// </summary>
internal static class Native
{
    // ---------- 输入模拟 ----------
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYDOWN = 0x0000;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// 必须声明！union 的尺寸由最大成员决定。
    /// 只放 KEYBDINPUT 会让 Marshal.SizeOf&lt;INPUT&gt;() 算出 32（x64）/24（x86），
    /// 而真实 sizeof(INPUT) 是 40（x64）/28（x86）—— SendInput 会因为 cbSize 不对
    /// 直接返回 0（GetLastError=87 ERROR_INVALID_PARAMETER），一个键都发不出去。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    /// <summary>真实 sizeof(INPUT)：x64=40，x86=28。用于自检。</summary>
    public static readonly int RealInputSize = IntPtr.Size == 8 ? 40 : 28;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>把字符按当前键盘布局换算成虚拟键。低字节=VK，高字节=修饰键状态。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);

    [DllImport("user32.dll")]
    public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    // ---------- IMM32 ----------
    public const uint GCS_COMPSTR = 0x0008;
    public const uint GCS_RESULTSTR = 0x0800;
    public const uint GCS_COMPATTR = 0x0010;
    public const uint GCS_RESULTCLAUSE = 0x1000;

    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern int ImmGetCompositionString(IntPtr hIMC, uint dwIndex, StringBuilder lpBuf, int dwBufLen);

    /// <summary>先取长度（传 null），返回字节数。</summary>
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern int ImmGetCompositionString(IntPtr hIMC, uint dwIndex, IntPtr lpBuf, int dwBufLen);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern int ImmGetDescription(IntPtr hkl, StringBuilder lpsz, int uBufLen);

    [DllImport("imm32.dll")]
    public static extern bool ImmGetOpenStatus(IntPtr hIMC);

    [DllImport("imm32.dll")]
    public static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

    [DllImport("imm32.dll")]
    public static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint lpdw, out uint lpSentence);

    /// <summary>清空 composition（SCS_SETSTR 置空串）。TSF IME 不一定吃这套，需配合 ESC。</summary>
    public const uint SCS_SETSTR = 0x0009;

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ImmSetCompositionString(IntPtr hIMC, uint dwIndex,
        IntPtr lpComp, int dwCompLen, IntPtr lpRead, int dwReadLen);

    /// <summary>缓冲区至少 KL_NAMELENGTH(9) 个 WCHAR。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetKeyboardLayoutName(StringBuilder pwszKLID);

    /// <summary>该键盘布局是不是一个真正的 IME（而非纯键盘布局）。</summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmIsIME(IntPtr hkl);

    // ---------- 窗口 / IME 消息 ----------
    public const int WM_IME_STARTCOMPOSITION = 0x010D;
    public const int WM_IME_ENDCOMPOSITION = 0x010E;
    public const int WM_IME_COMPOSITION = 0x010F;
    public const int WM_IME_SETCONTEXT = 0x0281;
    public const int WM_ACTIVATE = 0x0006;
    public const int WM_KILLFOCUS = 0x0008;
    public const int WM_SETFOCUS = 0x0007;
    public const int WM_HOTKEY = 0x0312;
    public const int WA_INACTIVE = 0;

    public const int EM_GETTEXTEX = 0x044E; // WM_USER + 94
    public const int EM_SETEVENTMASK = 0x044D; // WM_USER + 69
    public const int ENM_CHANGE = 0x00000001;

    // ---------- 虚拟键 ----------
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_BACK = 0x08;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_OEM_COMMA = 0xBC;   // ,<
    public const ushort VK_OEM_PERIOD = 0xBE;  // .>
    public const ushort VK_OEM_1 = 0xBA;       // ;:
    public const ushort VK_OEM_2 = 0xBF;       // /?
    public const ushort VK_OEM_3 = 0xC0;       // `~
    public const ushort VK_OEM_4 = 0xDB;       // [{
    public const ushort VK_OEM_5 = 0xDC;       // \|
    public const ushort VK_OEM_6 = 0xDD;       // ]}
    public const ushort VK_OEM_7 = 0xDE;       // '"

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---------- 便捷封装 ----------

    /// <summary>
    /// 取 composition / result 串。
    /// 注意：必须用"先取长度→按返回字节数精确截断"的方式。
    /// 用 StringBuilder 会因为 Capacity 大于实际长度而把尾部垃圾字符带进来
    /// （实测微软拼音下多出一个 U+0221）。
    /// </summary>
    public static string GetCompositionString(IntPtr hWnd, uint index)
    {
        IntPtr himc = ImmGetContext(hWnd);
        if (himc == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int bytes = ImmGetCompositionString(himc, index, IntPtr.Zero, 0);
            if (bytes <= 0) return string.Empty;
            buf = Marshal.AllocHGlobal(bytes + 2);
            int n = ImmGetCompositionString(himc, index, buf, bytes + 2);
            if (n <= 0) return string.Empty;
            return Marshal.PtrToStringUni(buf, n / 2) ?? string.Empty;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            ImmReleaseContext(hWnd, himc);
        }
    }

    /// <summary>强制清空 composition 串（TSF IME 可能不响应，需配合 ESC 键）。</summary>
    public static bool ClearComposition(IntPtr hWnd)
    {
        IntPtr himc = ImmGetContext(hWnd);
        if (himc == IntPtr.Zero) return false;
        try
        {
            return ImmSetCompositionString(himc, SCS_SETSTR, IntPtr.Zero, 0, IntPtr.Zero, 0);
        }
        finally
        {
            ImmReleaseContext(hWnd, himc);
        }
    }

    /// <summary>当前键盘布局对应的输入法名称。</summary>
    public static string GetImeName()
    {
        IntPtr hkl = GetKeyboardLayout(0);
        var sb = new StringBuilder(256);
        ImmGetDescription(hkl, sb, sb.Capacity);
        if (sb.Length > 0) return sb.ToString();

        // ImmGetDescription 对部分 TSF IME 返回空，回退到 KLID + 语言 ID
        var klid = new StringBuilder(9);
        string klidStr = GetKeyboardLayoutName(klid) ? klid.ToString() : "????????";
        ushort lang = (ushort)(hkl.ToInt64() & 0xFFFF);
        string langName = lang switch
        {
            0x0804 => "简体中文",
            0x0404 => "繁体中文(台湾)",
            0x0C04 => "繁体中文(香港)",
            0x0409 => "英语(美国)",
            0x0809 => "英语(英国)",
            0x0411 => "日语",
            0x0412 => "韩语",
            _ => $"LangID=0x{lang:X4}",
        };
        return $"{langName} [KLID={klidStr}]";
    }

    /// <summary>
    /// 靠已知进程名嗅探当前是哪个输入法。
    /// ImmGetDescription 对 TSF IME 一律返回空串，日志里根本分不清在测搜狗还是微软拼音 ——
    /// 而这两者行为差异巨大（实测搜狗 5ms 就稳，微软拼音 20ms 还在失真），必须能标识。
    /// </summary>
    public static string DetectImeByProcess()
    {
        (string proc, string name)[] map =
        {
            ("SogouInput", "搜狗拼音"), ("SogouImeBroker", "搜狗拼音"),
            ("SGTool", "搜狗拼音"), ("SogouCloud", "搜狗拼音"),
            ("ChsIME", "微软拼音"), ("TextInputHost", "微软拼音"),
            ("BaiduPinyin", "百度拼音"), ("baiduime", "百度拼音"),
            ("QQPinyin", "QQ拼音"),
            ("WeaselServer", "小狼毫/Rime"), ("WeaselDeployer", "小狼毫/Rime"),
        };
        var found = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                foreach (var (proc, name) in map)
                    if (n.Contains(proc, StringComparison.OrdinalIgnoreCase) && !found.Contains(name))
                        found.Add(name);
            }
            catch { }
        }
        return found.Count == 0 ? "(未知-请手填)" : string.Join(" / ", found);
    }

    /// <summary>列出系统里已安装的所有键盘布局/输入法。</summary>
    public static List<(IntPtr Hkl, string Name, bool IsIme)> ListLayouts()
    {
        var list = new List<(IntPtr, string, bool)>();
        int n = GetKeyboardLayoutList(0, null);
        if (n <= 0) return list;
        var arr = new IntPtr[n];
        int got = GetKeyboardLayoutList(n, arr);
        for (int i = 0; i < got; i++)
        {
            var sb = new StringBuilder(256);
            ImmGetDescription(arr[i], sb, sb.Capacity);
            list.Add((arr[i], sb.ToString(), ImmIsIME(arr[i])));
        }
        return list;
    }
}
