using System.Runtime.InteropServices;
using static ImeBench.Win32.Native;

namespace ImeBench.Win32;

public enum SendStatus
{
    Sent,
    Stopped,     // 停止标志被置位
    LostFocus,   // 前台窗口已不是主窗口
    Failed,      // SendInput 本身失败（看 LastError）
}

/// <summary>
/// 单键 SendInput 发送器。
/// 设计要点：
///  1) 逐键发送（而非批量），每键前过"双闸门"——停止标志 + 前台窗口校验；
///  2) 拼音字母必须走虚拟键，走 KEYEVENTF_UNICODE 会产生 VK_PACKET，IME 不会进入 composition；
///  3) cbSize 必须传真实 sizeof(INPUT)（x64=40 / x86=28），否则 SendInput 直接失败（err=87）。
/// </summary>
public sealed class InputSender
{
    private readonly INPUT[] _buf = new INPUT[1];
    private volatile bool _stopRequested;
    private int _failCount;

    /// <summary>SendInput 返回非 1 的次数（通常意味着参数/队列有问题）。</summary>
    public int FailCount => _failCount;

    /// <summary>最近一次 SendInput 失败的 GetLastError。</summary>
    public int LastError { get; private set; }

    /// <summary>本进程认为的 INPUT 结构体大小（自检用，必须等于 RealInputSize）。</summary>
    public static int StructSize { get; } = Marshal.SizeOf<INPUT>();

    /// <summary>结构体尺寸自检：false 说明 INPUT 声明有误，SendInput 必然全部失败。</summary>
    public static bool StructOk => StructSize == RealInputSize;

    /// <summary>主窗口句柄，用于前台校验。为 IntPtr.Zero 时跳过该校验。</summary>
    public IntPtr MainHwnd { get; set; }

    public void RequestStop() => _stopRequested = true;
    public void Reset() { _stopRequested = false; LastError = 0; }
    public bool StopRequested => _stopRequested;

    /// <summary>双闸门：未被请求停止 且 前台窗口仍是主窗口。</summary>
    public bool Gate()
    {
        if (_stopRequested) return false;
        if (MainHwnd != IntPtr.Zero && GetForegroundWindow() != MainHwnd) return false;
        return true;
    }

    /// <summary>按虚拟键发送一次完整按下+抬起。</summary>
    public SendStatus SendVk(ushort vk)
    {
        if (!Gate()) return _stopRequested ? SendStatus.Stopped : SendStatus.LostFocus;
        Press(vk, scancode: false);
        return LastError != 0 ? SendStatus.Failed : SendStatus.Sent;
    }

    /// <summary>
    /// 用硬编码 ASCII→VK 表，不走 VkKeyScanEx。
    /// 原因：VkKeyScanEx 是按"当前键盘布局"换算的，中文 IME 的 HKL 下结果未必可靠；
    /// 拼音只涉及 a-z，直接查表最稳。
    /// </summary>
    public bool UseAsciiVkMap { get; set; } = true;

    private static bool TryAsciiVk(char c, out ushort vk, out bool shift)
    {
        vk = 0; shift = false;
        if (c >= 'a' && c <= 'z') { vk = (ushort)('A' + (c - 'a')); return true; }
        if (c >= 'A' && c <= 'Z') { vk = (ushort)c; shift = true; return true; }
        if (c >= '0' && c <= '9') { vk = (ushort)c; return true; }
        switch (c)
        {
            case ' ': vk = VK_SPACE; return true;
            case ',': vk = VK_OEM_COMMA; return true;
            case '.': vk = VK_OEM_PERIOD; return true;
            case ';': vk = VK_OEM_1; return true;
            case '/': vk = VK_OEM_2; return true;
            case '[': vk = VK_OEM_4; return true;
            case ']': vk = VK_OEM_6; return true;
            case '\'': vk = VK_OEM_7; return true;
            case '-': vk = 0xBD; return true;
            case '=': vk = 0xBB; return true;
        }
        return false;
    }

    /// <summary>按字符发送：优先虚拟键（IME 可见），无映射时回退 Unicode。</summary>
    public SendStatus SendChar(char c, bool forceUnicode = false)
    {
        if (!forceUnicode && UseAsciiVkMap && TryAsciiVk(c, out ushort avk, out bool ashift))
        {
            if (!Gate()) return _stopRequested ? SendStatus.Stopped : SendStatus.LostFocus;
            if (ashift) Down(VK_SHIFT);
            Press(avk, scancode: false);
            if (ashift) Up(VK_SHIFT);
            return LastError != 0 ? SendStatus.Failed : SendStatus.Sent;
        }

        if (!forceUnicode)
        {
            short r = VkKeyScanEx(c, GetKeyboardLayout(0));
            if (r != -1)
            {
                ushort vk = (ushort)(r & 0x00FF);
                int mods = (r >> 8) & 0x00FF;
                if (!Gate()) return _stopRequested ? SendStatus.Stopped : SendStatus.LostFocus;

                bool shift = (mods & 1) != 0;
                if (shift) Down(VK_SHIFT);
                Press(vk, scancode: false);
                if (shift) Up(VK_SHIFT);
                return LastError != 0 ? SendStatus.Failed : SendStatus.Sent;
            }
        }

        if (!Gate()) return _stopRequested ? SendStatus.Stopped : SendStatus.LostFocus;
        Press((ushort)c, scancode: true);
        return LastError != 0 ? SendStatus.Failed : SendStatus.Sent;
    }

    private void Press(ushort code, bool scancode)
    {
        Down(code, scancode);
        Up(code, scancode);
    }

    private void Down(ushort code, bool scancode = false) => Emit(code, scancode, false);

    private void Up(ushort code, bool scancode = false) => Emit(code, scancode, true);

    private void Emit(ushort code, bool scancode, bool up)
    {
        ref INPUT inp = ref _buf[0];
        inp.type = INPUT_KEYBOARD;
        inp.u.ki.wVk = scancode ? (ushort)0 : code;
        inp.u.ki.wScan = scancode ? code : (ushort)0;
        inp.u.ki.dwFlags = (scancode ? KEYEVENTF_UNICODE : 0) | (up ? KEYEVENTF_KEYUP : 0);
        inp.u.ki.time = 0;
        inp.u.ki.dwExtraInfo = IntPtr.Zero;

        LastError = 0;
        if (SendInput(1, _buf, RealInputSize) != 1)
        {
            _failCount++;
            LastError = Marshal.GetLastWin32Error();
        }
    }
}
