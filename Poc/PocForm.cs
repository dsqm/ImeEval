using System.Diagnostics;
using ImeBench.Win32;
using static ImeBench.Win32.Native;

namespace ImeBench.Poc;

/// <summary>
/// M0 技术验证窗口。
/// 目标：用实测回答几个关键问题，决定 M2 走哪条上屏捕获路径。
///
/// 本版修复了两个会让"看起来什么都没发生"的坑：
///  1) SendInput 的 cbSize 传错（INPUT union 缺 MOUSEINPUT 导致尺寸算成 32 而非 40）→ 一个键都发不出去；
///  2) 点击按钮会抢走键盘焦点 → 按键发给了按钮而不是宿主，IME 根本不参与。
/// 新增：按钮不可聚焦、发送前强制夺回焦点并校验、失败原因直接显示在顶部状态条、
///       Ctrl+Alt+S 全局热键停止、主窗口失焦停止、--auto 一键自检模式。
/// </summary>
public sealed class PocForm : Form
{
    private const int HOTKEY_ID = 9001;
    private const uint HOTKEY_MOD = MOD_CONTROL | MOD_ALT;
    private const uint HOTKEY_VK = 0x53; // 'S'

    private readonly bool _auto;
    private readonly ImeHost _host;
    private readonly TextBox _log;
    private readonly InputSender _sender = new();
    private readonly CheckBox _cbReadEveryKey;
    private readonly NumericUpDown _nudInterval;
    private readonly NumericUpDown _nudSilence;
    private readonly TextBox _tbPinyin;
    private readonly TextBox _tbExpect;
    private readonly TextBox _tbIme;
    private readonly Label _lblStatus;
    private bool _busy;
    private bool _hotkeyOk;
    private int _hotkeyErr;

    public PocForm(bool auto = false)
    {
        _auto = auto;
        Text = "ImeBench - M0 技术验证 POC";
        Size = new Size(1040, 780);
        StartPosition = FormStartPosition.CenterScreen;

        // ---- 顶部状态条（一眼看出问题） ----
        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            BackColor = Color.FromArgb(245, 245, 245),
            Padding = new Padding(8, 6, 8, 6),
            Text = "初始化中...",
        };

        // ---- 按钮区 ----
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(6),
        };

        _tbPinyin = new TextBox { Text = "zhongguo", Width = 110 };
        _tbExpect = new TextBox { Text = "中国", Width = 60 };
        _nudInterval = new NumericUpDown { Minimum = 0, Maximum = 500, Value = 60, Width = 60 };
        _nudSilence = new NumericUpDown { Minimum = 0, Maximum = 3000, Value = 200, Width = 60 };
        _cbReadEveryKey = new CheckBox { Text = "每键读控件文本(测扰动)", Checked = true, AutoSize = true };
        _tbIme = new TextBox { Text = DetectImeByProcess(), Width = 120 };

        flow.Controls.Add(Lbl("被测输入法:"));
        flow.Controls.Add(_tbIme);
        flow.Controls.Add(Lbl("拼音:"));
        flow.Controls.Add(_tbPinyin);
        flow.Controls.Add(Lbl("期望上屏:"));
        flow.Controls.Add(_tbExpect);
        flow.Controls.Add(Lbl("键间隔ms:"));
        flow.Controls.Add(_nudInterval);
        flow.Controls.Add(Lbl("静默ms:"));
        flow.Controls.Add(_nudSilence);
        flow.Controls.Add(_cbReadEveryKey);

        AddBtn(flow, "0. 环境诊断", (_, _) => DumpDiag());
        AddBtn(flow, "1. 拼音(VK)", (_, _) => _ = RunAsync(unicode: false, space: false, comma: false));
        AddBtn(flow, "2. 拼音(Unicode对比)", (_, _) => _ = RunAsync(unicode: true, space: false, comma: false));
        AddBtn(flow, "3. 拼音+空格上屏", (_, _) => _ = RunAsync(unicode: false, space: true, comma: false));
        AddBtn(flow, "4. 拼音+逗号顶屏", (_, _) => _ = RunAsync(unicode: false, space: false, comma: true));
        AddBtn(flow, "5. 压测200句", (_, _) => _ = RunStressAsync(200));
        AddBtn(flow, "8. 间隔扫描(粗)", (_, _) => _ = RunScanAsync());
        AddBtn(flow, "9. 精确定时扫描(关键)", (_, _) => _ = RunPreciseScanAsync());
        AddBtn(flow, "6. 停止", (_, _) => _sender.RequestStop());
        AddBtn(flow, "7. 清空", (_, _) => { _host.Reset(); _log.Clear(); });

        // ---- 中部宿主 ----
        var hostPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        var hostLabel = new Label
        {
            Text = "文本宿主：先手动点进这里、用真实键盘敲 zhongguo，确认输入法在本控件里能弹出候选 —— 否则问题不在 SendInput",
            Dock = DockStyle.Top,
            Height = 32,
            ForeColor = Color.DarkRed,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        _host = new ImeHost
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 14f),
            DetectUrls = false,
            AutoWordSelection = false,
            HideSelection = false,
            MaxLength = 0,
            BorderStyle = BorderStyle.FixedSingle,
            ImeMode = ImeMode.On,
        };
        _host.Log = s => Log("[host] " + s);
        _host.GotFocus += (_, _) => UpdateStatus();
        _host.LostFocus += (_, _) => UpdateStatus();
        hostPanel.Controls.Add(_host);
        hostPanel.Controls.Add(hostLabel);

        // ---- 底部日志 ----
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            BackColor = Color.White,
        };

        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tlp.Controls.Add(_lblStatus, 0, 0);
        tlp.Controls.Add(flow, 0, 1);
        tlp.Controls.Add(hostPanel, 0, 2);
        tlp.Controls.Add(_log, 0, 3);
        Controls.Add(tlp);

        Shown += async (_, _) =>
        {
            _sender.MainHwnd = Handle;
            StartLog();
            Log($"INPUT 结构体自检: Marshal.SizeOf={InputSender.StructSize} 期望={RealInputSize} 正确={InputSender.StructOk}");
            if (!InputSender.StructOk)
                Log("!!!! INPUT 结构体尺寸错误，SendInput 将全部失败（err=87）!!!!!");
            DumpDiag();
            ActivateHost();
            if (_auto) await AutoSequence();
        };

        UpdateStatus();
    }

    private static Label Lbl(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(6, 6, 0, 0) };

    private static int LoWord(IntPtr p) => (int)(p.ToInt64() & 0xFFFF);

    /// <summary>不可聚焦的按钮：避免点击后把键盘焦点从宿主抢走。</summary>
    private sealed class NoFocusButton : Button
    {
        public NoFocusButton() => SetStyle(ControlStyles.Selectable, false);
    }

    private void AddBtn(FlowLayoutPanel parent, string text, EventHandler h)
    {
        var b = new NoFocusButton { Text = text, AutoSize = true, Margin = new Padding(4, 6, 4, 2) };
        b.Click += h;
        parent.Controls.Add(b);
    }

    // ---------------- 热键与失焦 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyOk = RegisterHotKey(Handle, HOTKEY_ID, HOTKEY_MOD, HOTKEY_VK);
        _hotkeyErr = _hotkeyOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        // 注意：这里不能 Log —— Form 的 handle 在 Shown 之前创建，
        // 而 StartLog() 会重写日志文件，写在这里的内容会被冲掉。改到 DumpDiag 里输出。
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_HOTKEY when m.WParam.ToInt32() == HOTKEY_ID:
                _sender.RequestStop();
                Log("[HOTKEY] Ctrl+Alt+S 触发 → 已请求停止");
                UpdateStatus("热键触发：已请求停止");
                return;

            case WM_ACTIVATE when LoWord(m.WParam) == WA_INACTIVE:
                Log("[ACTIVATE] 主窗口失焦 (WA_INACTIVE)");
                if (_busy) { _sender.RequestStop(); UpdateStatus("主窗口失焦：已请求停止"); }
                break;
        }
        base.WndProc(ref m);
    }

    // ---------------- 观测与日志 ----------------

    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "poc-log.txt");

    private void StartLog()
    {
        try
        {
            File.WriteAllText(LogFilePath,
                $"=== ImeBench M0 POC started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (auto={_auto}) ==={Environment.NewLine}");
        }
        catch { }
    }

    private void Log(string s)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
        try { File.AppendAllText(LogFilePath, line + Environment.NewLine); } catch { }
        if (_log.IsHandleCreated && _log.InvokeRequired) { _log.BeginInvoke(new Action<string>(Log), s); return; }
        if (_log.TextLength > 300_000) _log.Clear();
        _log.AppendText(line + Environment.NewLine);
    }

    // ---------------- 状态条 ----------------

    private void UpdateStatus(string extra = null)
    {
        if (_lblStatus.IsHandleCreated && _lblStatus.InvokeRequired)
        {
            _lblStatus.BeginInvoke(new Action<string>(UpdateStatus), extra);
            return;
        }
        IntPtr fg = GetForegroundWindow();
        IntPtr foc = GetFocus();
        bool fgOk = fg == Handle;
        bool focOk = foc == _host.Handle;

        var sb = new System.Text.StringBuilder();
        sb.Append($"SendInput尺寸: {(InputSender.StructOk ? "OK" : "错误!")}  |  ");
        sb.Append($"前台: {(fgOk ? "是我" : "不是我")}  |  ");
        sb.Append($"焦点: {(focOk ? "在宿主" : foc == IntPtr.Zero ? "无" : "在别处0x" + foc.ToInt64().ToString("X"))}  |  ");
        sb.Append($"热键: {(_hotkeyOk ? "已注册" : "未注册")}  |  ");
        sb.Append($"输入法: {GetImeName()}");
        if (extra != null) sb.Append(Environment.NewLine + extra);

        _lblStatus.Text = sb.ToString();
        _lblStatus.ForeColor = (InputSender.StructOk && fgOk && focOk) ? Color.DarkGreen : Color.DarkRed;
    }

    /// <summary>把焦点夺回宿主；返回是否成功。</summary>
    private bool ActivateHost()
    {
        if (!Visible || IsDisposed) return false;
        if (GetForegroundWindow() != Handle)
        {
            Activate();
            SetForegroundWindow(Handle);
        }
        bool ok = _host.Focus();
        if (!ok || GetFocus() != _host.Handle)
            SetFocus(_host.Handle);
        Application.DoEvents();
        return GetFocus() == _host.Handle;
    }

    // ---------------- 诊断 ----------------

    private void DumpDiag()
    {
        Log("================ 环境诊断 ================");
        Log($"进程: {(Environment.Is64BitProcess ? "x64" : "x86")} | .NET {Environment.Version}");
        Log($"INPUT: Marshal.SizeOf={InputSender.StructSize} 期望={RealInputSize} 正确={InputSender.StructOk}");

        Log("--- 已安装输入法/布局 ---");
        foreach (var (hkl, name, isIme) in ListLayouts())
            Log($"   HKL=0x{hkl.ToInt64():X8} IME={isIme} 名称=\"{name}\"");

        IntPtr cur = GetKeyboardLayout(0);
        Log($"当前线程布局 HKL=0x{cur.ToInt64():X8} 是IME={ImmIsIME(cur)} 名称=\"{GetImeName()}\"");

        Log("--- 窗口/焦点 ---");
        Log($"主窗口=0x{Handle.ToInt64():X} 宿主=0x{_host.Handle.ToInt64():X}");
        Log($"前台窗口=0x{GetForegroundWindow().ToInt64():X} (是我:{GetForegroundWindow() == Handle})");
        Log($"键盘焦点=0x{GetFocus().ToInt64():X} (是宿主:{GetFocus() == _host.Handle})");
        Log($"宿主 ImeMode={_host.ImeMode} Focused={_host.Focused} CanFocus={_host.CanFocus}");
        Log($"全局热键 Ctrl+Alt+S: {(_hotkeyOk ? "已注册" : "未注册 err=" + _hotkeyErr)}（宿主会吞普通键，只有全局热键可靠）");

        Log("--- IMM 状态 ---");
        IntPtr himc = ImmGetContext(_host.Handle);
        if (himc == IntPtr.Zero)
        {
            Log("   !! ImmGetContext 返回 0 —— 宿主没有关联输入上下文");
        }
        else
        {
            Log($"   HIMC=0x{himc.ToInt64():X} 打开={ImmGetOpenStatus(himc)}");
            ImmGetConversionStatus(himc, out uint conv, out uint sent);
            Log($"   转换模式=0x{conv:X4} 句型模式=0x{sent:X4}");
            ImmReleaseContext(_host.Handle, himc);
        }
        Log("==========================================");
        UpdateStatus();
    }

    private void Snapshot(string tag)
    {
        string ctl = _host.ControlText;
        string comp = _host.CompositionString;
        string committed = _host.CommittedText;
        Log($"  ·{tag}: 控件=\"{ctl}\" | 组字=\"{comp}\" | 累加=\"{committed}\" | composing={_host.Composing} | 控件==累加? {ctl == committed}");
    }

    // ---------------- 测试主体 ----------------

    private async Task RunAsync(bool unicode, bool space, bool comma)
    {
        if (_busy) { Log("繁忙中，忽略"); return; }
        _busy = true;
        try
        {
            _sender.Reset();
            _host.Reset();

            int interval = (int)_nudInterval.Value;
            int silence = (int)_nudSilence.Value;
            string py = _tbPinyin.Text;

            Log($"===== 开始: 拼音=\"{py}\" 方式={(unicode ? "Unicode(VK_PACKET)" : "虚拟键")} 间隔={interval}ms 空格={space} 逗号={comma} =====");

            if (!PrepareFocus()) return;
            if (!await ClearLeftoverAsync()) return;

            foreach (char c in py)
            {
                var st = _sender.SendChar(c, unicode);
                if (st != SendStatus.Sent) { Abort(st, c); return; }
                if (_cbReadEveryKey.Checked) Snapshot($"按 '{c}' 后");
                if (interval > 0) await Task.Delay(interval);
            }
            Snapshot("拼音敲完");

            if (space)
            {
                var st = _sender.SendVk(VK_SPACE);
                if (st != SendStatus.Sent) { Abort(st, ' '); return; }
                await Task.Delay(silence);
                Snapshot("按空格后");
            }

            if (comma)
            {
                var st = _sender.SendChar(',');
                if (st != SendStatus.Sent) { Abort(st, ','); return; }
                await Task.Delay(silence);
                Snapshot("按逗号后");
            }

            await Task.Delay(silence);
            Snapshot("静默期后(最终)");

            string ctl = _host.ControlText, committed = _host.CommittedText;
            Log("---- 结论 ----");
            Log($"  控件文本 = \"{ctl}\" (长度 {ctl.Length})");
            Log($"  累加器   = \"{committed}\" (长度 {committed.Length})");
            if (ctl == committed)
                Log("  => 两者一致：本次未观察到 composition 污染（或本次输入未产生 composition）");
            else
            {
                Log("  => !! 两者不一致：控件文本被 composition 污染，或累加器漏收 GCS_RESULTSTR");
                Log($"     尾部差值 = \"{ctl.Substring(Math.Min(ctl.Length, committed.Length))}\"");
            }
            Log($"  IME消息计数: START={_host.StartCount} COMP={_host.CompCount} END={_host.EndCount} RESULT={_host.ResultCount}");
            Log($"  SendInput 失败次数 = {_sender.FailCount}  LastError = {_sender.LastError}");

            if (_host.StartCount == 0 && ctl.Length == 0)
                Log("  !! 既无 composition 也无字符进入宿主 —— 检查输入法是否为中文 IME / 焦点是否真在宿主");
            else if (_host.StartCount == 0 && ctl.Length > 0)
                Log("  !! 字符进去了但 IME 未进入 composition —— 当前不是中文 IME，或 IME 处于英文模式");
        }
        finally
        {
            _busy = false;
            UpdateStatus();
        }
    }

    /// <summary>
    /// 丢弃上一次残留的 composition。
    /// 实测教训：未上屏的拼音会残留在 IME 里串进下一次结果，
    /// 必须每次开跑前清干净，否则 错字率统计全是噪声。
    /// </summary>
    private async Task<bool> ClearLeftoverAsync()
    {
        _host.ClearIme();                    // ImmSetCompositionString 对 TSF IME 多半无效
        _sender.SendVk(VK_ESCAPE);           // ESC：imm32 时代有效，TSF 下未必
        await Task.Delay(120);
        for (int i = 0; i < 32; i++)         // 暴力兜底：连发 Backspace 掏空 composition
            _sender.SendVk(VK_BACK);
        await Task.Delay(120);
        _host.Reset();
        bool clean = _host.ControlText.Length == 0;
        Log($"清空残留: ESC+32×Backspace 后 控件=\"{_host.ControlText}\" 干净={clean}");
        if (!clean) Log("  !! 清空后仍有残留文本，统计可能失真");
        return true;
    }

    private bool PrepareFocus()
    {
        bool ok = ActivateHost();
        Log($"焦点准备: 主窗口前台={GetForegroundWindow() == Handle} 焦点在宿主={GetFocus() == _host.Handle}");
        string msg = ok ? null : "焦点未能落到宿主 —— 按键会发给别的控件";
        UpdateStatus(msg);
        if (!ok) Log("!! " + msg);
        return ok;
    }

    private void Abort(SendStatus st, char c)
    {
        string why = st switch
        {
            SendStatus.Stopped => "已被请求停止",
            SendStatus.LostFocus => "前台窗口已不是本窗口（失焦即停生效）",
            SendStatus.Failed => $"SendInput 失败 GetLastError={_sender.LastError}（87=参数错 / 5=被UIPI拦截）",
            _ => st.ToString(),
        };
        Log($"!! 发送 '{c}' 中止: {why}");
        UpdateStatus("发送中止: " + why);
    }

    /// <summary>
    /// 压力测试：连续 N 组「拼音 + 空格上屏」，键间隔 0ms。
    /// 之所以不再测"连发 2000 个 a"：那种写法字符全被 IME 吃进 composition 而永不上屏，
    /// 控件文本为 0 是正常的（未上屏），测不出丢键。只有"上屏了多少"才是有意义的指标。
    /// </summary>
    private async Task RunStressAsync(int groups)
    {
        if (_busy) { Log("繁忙中，忽略"); return; }
        _busy = true;
        try
        {
            _sender.Reset();
            _host.Reset();

            string py = _tbPinyin.Text;
            string expect = _tbExpect.Text;
            Log($"===== 压力测试: {groups} 组「{py}+空格」，键间隔 0ms，期望每组上屏\"{expect}\" =====");
            if (!PrepareFocus()) return;
            if (!await ClearLeftoverAsync()) return;

            var sw = Stopwatch.StartNew();
            int done = 0, sent = 0;
            for (int i = 0; i < groups; i++)
            {
                foreach (char c in py)
                {
                    var st = _sender.SendChar(c);
                    if (st != SendStatus.Sent) { Abort(st, c); sw.Stop(); Report(sent, done, groups, expect, sw); return; }
                    sent++;
                }
                var ss = _sender.SendVk(VK_SPACE);
                if (ss != SendStatus.Sent) { Abort(ss, ' '); break; }
                sent++;
                done++;
                // 定期让出 UI，否则消息队列不转、IME 没机会消化
                if ((i & 0x0F) == 0x0F) await Task.Yield();
            }
            sw.Stop();

            await Task.Delay(800);
            Report(sent, done, groups, expect, sw);
        }
        finally
        {
            _busy = false;
            UpdateStatus();
        }
    }

    private void Report(int sent, int done, int groups, string expect, Stopwatch sw)
    {
        string ctl = _host.ControlText;
        string committed = _host.CommittedText;
        int ok = expect.Length == 0 ? 0 : CountOccurrences(ctl, expect);

        Log($"发送 {sent} 键 / 完成 {done}/{groups} 组，耗时 {sw.ElapsedMilliseconds}ms，" +
            $"平均 {sw.Elapsed.TotalMilliseconds / Math.Max(1, done):F2} ms/组");
        Log($"SendInput 失败次数 = {_sender.FailCount} LastError = {_sender.LastError}");
        Log($"控件文本长度={ctl.Length} | 累加器长度={committed.Length} | 控件==累加? {ctl == committed}");
        Log($"命中期望\"{expect}\"的组数 = {ok}/{done} → 错句率 {(done == 0 ? 0 : (done - ok) * 100.0 / done):F1}%");
        Log($"IME消息计数: START={_host.StartCount} COMP={_host.CompCount} END={_host.EndCount} RESULT={_host.ResultCount}");
        Log($"上屏次数(RESULT) = {_host.ResultCount} vs 组数 = {done} → {(done == _host.ResultCount ? "一致，可按上屏次数计错句" : "!! 不一致，需改用别的上屏判定")}");
        string preview = ctl.Length > 80 ? ctl[..80] + "..." : ctl;
        Log($"文本预览: \"{preview}\"");
    }

    /// <summary>
    /// 间隔扫描：在多个按键间隔档位下各跑固定组数，找出"结果仍然正确"的最快档位。
    /// 这是决定默认速度的关键实验 —— 全速不等于可用，实测 0ms 下微软拼音会大量丢键。
    /// </summary>
    private async Task RunScanAsync()
    {
        if (_busy) { Log("繁忙中，忽略"); return; }
        _busy = true;
        try
        {
            int[] intervals = { 0, 5, 10, 20, 40, 60 };
            int groups = 20;
            string py = _tbPinyin.Text;
            string expect = _tbExpect.Text;
            int expectLen = expect.Length * groups;

            Log($"########## 间隔扫描: {groups} 组「{py}+空格」/档，期望共 {expectLen} 字 ##########");
            Log("档位 | 上屏字符 | 命中期望 | 耗时 | ms/组 | 预览");

            foreach (int iv in intervals)
            {
                _sender.Reset();
                _host.Reset();
                if (!PrepareFocus()) return;
                await ClearLeftoverAsync();

                var sw = Stopwatch.StartNew();
                int done = 0;
                for (int i = 0; i < groups; i++)
                {
                    foreach (char c in py)
                    {
                        var st = _sender.SendChar(c);
                        if (st != SendStatus.Sent) { Abort(st, c); sw.Stop(); return; }
                        if (iv > 0) await Task.Delay(iv);
                    }
                    var ss = _sender.SendVk(VK_SPACE);
                    if (ss != SendStatus.Sent) { Abort(ss, ' '); break; }
                    done++;
                    if (iv > 0) await Task.Delay(iv);
                    await Task.Yield();
                }
                sw.Stop();

                await Task.Delay(700);
                string ctl = _host.ControlText;
                int ok = CountOccurrences(ctl, expect);
                string prev = ctl.Length > 24 ? ctl[..24] + "..." : ctl;
                string verdict = ctl.Length == expectLen && ok == groups ? "✔ 正确" : "✘ 失真";
                Log($"{iv,4}ms | {ctl.Length,4}/{expectLen,-4} | {ok,3}/{done,-3} | " +
                    $"{sw.ElapsedMilliseconds,6}ms | {sw.Elapsed.TotalMilliseconds / Math.Max(1, done),5:F1} | {verdict} \"{prev}\"");
            }
            Log("########## 扫描结束：取「✔ 正确」里最快的一档作为默认速度 ##########");
        }
        finally
        {
            _busy = false;
            UpdateStatus();
        }
    }

    /// <summary>
    /// 精确定时扫描。
    /// Task.Delay 受系统时钟粒度限制（约 15.6ms），测不出真正的速度上限。
    /// 这里改在后台线程用 Stopwatch 做 spin-wait 精确定时；UI 线程在 await 期间正常泵消息，
    /// IME 才有机会消费输入队列。这样能摸到"结果仍正确"的真实最快档位。
    /// </summary>
    private async Task RunPreciseScanAsync()
    {
        if (_busy) { Log("繁忙中，忽略"); return; }
        _busy = true;
        try
        {
            double[] ivs = { 0, 0.25, 0.5, 1, 2, 4, 8, 16 };
            int groups = 20;
            string py = _tbPinyin.Text;
            string expect = _tbExpect.Text;
            int expectLen = expect.Length * groups;

            Log($"########## 精确定时扫描: {groups} 组「{py}+空格」/档，期望共 {expectLen} 字 ##########");
            Log("（后台线程 spin-wait 定时，UI 线程正常泵消息）");

            foreach (double ms in ivs)
            {
                _sender.Reset();
                _host.Reset();
                if (!PrepareFocus()) return;
                await ClearLeftoverAsync();

                var sw = Stopwatch.StartNew();
                await Task.Run(() => SendGroupsPrecise(groups, py, ms));
                sw.Stop();

                await Task.Delay(800);
                string ctl = _host.ControlText;
                int ok = CountOccurrences(ctl, expect);
                string prev = ctl.Length > 24 ? ctl[..24] + "..." : ctl;
                bool good = ctl.Length == expectLen && ok == groups;
                Log($"{ms,6:F2}ms | {ctl.Length,4}/{expectLen,-4} | {ok,3}/{groups,-3} | " +
                    $"{sw.ElapsedMilliseconds,6}ms | {sw.Elapsed.TotalMilliseconds / Math.Max(1, groups),6:F1} ms/组 | " +
                    $"{(good ? "✔ 正确" : "✘ 失真")} \"{prev}\"");
            }
            Log("########## 精确定时扫描结束 ##########");
        }
        finally
        {
            _busy = false;
            UpdateStatus();
        }
    }

    private void SendGroupsPrecise(int groups, string py, double intervalMs)
    {
        var th = Thread.CurrentThread;
        var old = th.Priority;
        th.Priority = ThreadPriority.AboveNormal;
        try
        {
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            long step = (long)(intervalMs * ticksPerMs);
            var sw = Stopwatch.StartNew();
            long next = 0;

            void Wait()
            {
                next += step;
                if (step <= 0) return;
                while (sw.ElapsedTicks < next) Thread.SpinWait(20);
            }

            for (int i = 0; i < groups; i++)
            {
                foreach (char c in py)
                {
                    if (_sender.SendChar(c) != SendStatus.Sent) return;
                    Wait();
                }
                if (_sender.SendVk(VK_SPACE) != SendStatus.Sent) return;
                Wait();
            }
        }
        finally
        {
            th.Priority = old;
        }
    }

    private static int CountOccurrences(string s, string pat)
    {
        if (string.IsNullOrEmpty(pat)) return 0;
        int c = 0, i = 0;
        while ((i = s.IndexOf(pat, i, StringComparison.Ordinal)) >= 0) { c++; i += pat.Length; }
        return c;
    }

    /// <summary>--auto：无人值守跑完核心用例，日志落盘后自动退出。</summary>
    private async Task AutoSequence()
    {
        await Task.Delay(600);
        Log("########## AUTO 模式：自动执行 1 / 3 / 4 / 间隔扫描 ##########");
        await RunAsync(unicode: false, space: false, comma: false);
        await Task.Delay(300);
        await RunAsync(unicode: false, space: true, comma: false);
        await Task.Delay(300);
        await RunAsync(unicode: false, space: false, comma: true);
        await Task.Delay(300);
        await RunPreciseScanAsync();
        await Task.Delay(300);
        // 放最后：Unicode 方式会往控件里塞原始 ascii，会污染后续用例
        await RunAsync(unicode: true, space: true, comma: false);
        Log("########## AUTO 完成 ##########");
        await Task.Delay(200);
        Close();
    }
}
