using System.ComponentModel;
using ImeBench.Core;
using ImeBench.Win32;
using static ImeBench.Win32.Native;

namespace ImeBench.UI;

internal sealed class MainForm : Form
{
    private const int HOTKEY_ID = 0x494D;                       // 'IM'
    private const uint HOTKEY_MOD = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
    private const ushort HOTKEY_VK = 0x53;                      // S

    // 便携化：所有运行时数据都放 exe 文件夹的 data\ 下（不碰注册表 / 用户目录）
    private static readonly string AppDir = CorpusPanel.DataDir;
    private static string CheckpointFile => Path.Combine(AppDir, "checkpoint.json");
    private static string LastRunFile => Path.Combine(AppDir, "last-run.json");
    private static string SessionLogFile => Path.Combine(AppDir, "session.log");

    private readonly ImeHost _host = new()
    {
        Dock = DockStyle.Top,
        Height = 84,
        Font = new Font("Microsoft YaHei", 12f),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        // 必须显式打开 IME：不设的话宿主默认走英文，拼音会被当普通字符直接插进去，
        // 输入法完全不参与（表现为"上屏文本 == 按键串"，CER 爆表）
        ImeMode = ImeMode.On,
        DetectUrls = false,
        AutoWordSelection = false,
        HideSelection = false,
    };

    private readonly InputSender _sender = new();
    private readonly RunController _ctrl = new();
    private Runner? _runner;
    private KeyTable? _table;
    private RunResult? _last;
    private bool _running;
    private bool _hotkeyOk;

    private readonly CorpusPanel _corpus = new();
    private readonly KeyTablePanel _keyPanel = new();
    private readonly CalibPanel _calib = new();

    private readonly RichTextBox _rtResult = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White,
        Font = new Font("Consolas", 9.5f), WordWrap = false,
        ScrollBars = RichTextBoxScrollBars.Both, BorderStyle = BorderStyle.None,
    };
    private readonly List<UnitResult> _runResults = new();
    private readonly NumericUpDown _nudInterval = Ui.Num(0, 500, 2);
    private readonly NumericUpDown _nudSettle = Ui.Num(0, 3000, 40);
    private readonly CheckBox _cbOnlyError = new() { Text = "只看错误", Checked = true, AutoSize = true, Margin = new Padding(10, 10, 6, 0) };
    private readonly Label _lblStats = new()
    {
        Dock = DockStyle.Top, AutoSize = true,
        Padding = new Padding(10, 3, 10, 5),
        Font = new Font("Microsoft YaHei", 9.5f),
        Text = "（尚未开始——点「▶ 开始」后这里实时刷新）",
    };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom, Height = 38 };
    private readonly Label _lblStatus = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        Padding = new Padding(10, 0, 0, 0),
    };
    private readonly Button _btnStart = Ui.Btn("▶ 开始", (_, _) => { });
    private readonly Button _btnStop = Ui.Btn("■ 停止", (_, _) => { });
    private readonly Button _btnResume = Ui.Btn("↻ 从断点继续", (_, _) => { });

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private TabControl _tabs = null!;
    private TabPage _pageRun = null!;   // 宿主所在的页——隐藏的页签里控件拿不到键盘焦点

    public MainForm()
    {
        Text = "ImeBench — 输入法整句正确度评测";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1080, 780);
        Font = new Font("Microsoft YaHei", 9f);

        RestoreWindowSize();
        RestoreRunOptions();

        _statusBar.Controls.Add(_lblStatus);
        Controls.Add(BuildRoot());
        Controls.Add(_statusBar);

        _sender.MainHwnd = Handle;
        _host.Log = _ => { };

        WireEvents();

        _timer.Tick += (_, _) => UpdateBar();
        _timer.Start();

        Log("就绪。流程：① 语料页选码表+文章→生成按键表 ② 标定页测速度 ③ 回到本页开始。");
        Log("提示：开始前请确认输入法模式与码表编码一致（如码表是小鹤双拼，输入法也要在小鹤双拼）。");
        CheckExistingCheckpoint();
    }

    // ───────────────────────── 布局 ─────────────────────────

    private Control BuildRoot()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(6, 4) };
        _tabs = tabs;

        var pageRun = new TabPage("运行") { Padding = new Padding(6) };
        pageRun.Controls.Add(BuildRunPage());
        _pageRun = pageRun;

        var pageCorpus = new TabPage("语料") { Padding = new Padding(6) };
        pageCorpus.Controls.Add(_corpus);

        var pageKeys = new TabPage("按键表") { Padding = new Padding(6) };
        pageKeys.Controls.Add(_keyPanel);

        var pageCalib = new TabPage("速度标定") { Padding = new Padding(6) };
        pageCalib.Controls.Add(_calib);

        // 页签顺序按使用流程：语料 → 按键表 → 运行 → 速度标定
        tabs.TabPages.AddRange(new[] { pageCorpus, pageKeys, pageRun, pageCalib });
        return tabs;
    }

    private Control BuildRunPage()
    {
        // 用停靠布局替代 TLP 绝对行高：
        //   统计区自适应内容高度（不再被 64px 行高压扁），
        //   复选框行独立成条（不再贴着分组框边框被裁），
        //   工具栏窄窗口自动换行。
        var container = new Panel { Dock = DockStyle.Fill };

        // 工具栏（最顶部）
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            WrapContents = true,
            AutoSize = true,
            MinimumSize = new Size(0, 46),
        };
        _btnStart.Click += (_, _) => _ = StartAsync(0, null);
        _btnStop.Click += (_, _) => StopRun("用户点击停止");
        _btnResume.Click += (_, _) => _ = ResumeAsync();
        bar.Controls.AddRange(new Control[]
        {
            _btnStart, _btnStop, _btnResume,
            Ui.Lbl("按键间隔ms:"), _nudInterval,
            Ui.Lbl("静默期ms:"), _nudSettle,
            Ui.Btn("导出 JSON", (_, _) => Export("json")),
            Ui.Btn("导出 CSV", (_, _) => Export("csv")),
            Ui.Btn("导出 HTML", (_, _) => Export("html")),
        });

        // 输入宿主
        var gbHost = new GroupBox { Text = "输入宿主（按键发到这里，测试期间请勿切换窗口）", Dock = DockStyle.Top, Height = 96, Padding = new Padding(6) };
        gbHost.Controls.Add(_host);

        // 实时统计：AutoSze 分组框 + 顶部停靠自动换行标签，内容多高框就多高
        var gbStats = new GroupBox { Text = "实时统计", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 0, 4, 2) };
        gbStats.Controls.Add(_lblStats);

        // 逐句对照（占据剩余全部空间）：富文本三行式条目，错处标色
        var gbResult = new GroupBox { Text = "逐句对照", Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 6) };
        var resultBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        resultBtns.Controls.Add(_cbOnlyError);
        _cbOnlyError.CheckedChanged += (_, _) => RebuildRows();
        var resultInner = new Panel { Dock = DockStyle.Fill };
        resultInner.Controls.Add(_rtResult);
        resultInner.Controls.Add(resultBtns);
        gbResult.Controls.Add(resultInner);

        // 日志已取消独立面板：最新一条显示在底部状态栏，完整日志落盘 data\session.log

        // 停靠按「后加先停」处理：bar 最后加 → 最顶；gbResult 最先加 → 占余下空间
        container.Controls.Add(gbResult);
        container.Controls.Add(gbStats);
        container.Controls.Add(gbHost);
        container.Controls.Add(bar);
        return container;
    }

    private void WireEvents()
    {
        _corpus.Compiled += table =>
        {
            _table = table;
            _keyPanel.Bind(table);
            _runResults.Clear();
            _rtResult.Clear();
            Log($"按键表已生成：{table.UnitCount} 个上屏单元 / {table.TotalKeys} 个按键 / 期望 {table.ExpectedText.Length} 字");
            _tabs.SelectedIndex = 1;   // 跳到按键表页
        };
        _keyPanel.GoRunRequested += () => _tabs.SelectedIndex = 2;   // 跳到运行页

        _calib.StartRequested += (_, _) => _ = RunCalibAsync();
        _calib.Recommended += ms =>
        {
            _nudInterval.Value = Math.Min(_nudInterval.Maximum, ms);
            Log($"标定完成，已应用推荐间隔 {ms}ms（可在工具栏自行修改）");
        };
    }

    // ───────────────────────── 运行 ─────────────────────────

    private async Task StartAsync(int startIndex, BenchStats? seed)
    {
        if (_running) { Log("正在运行中。"); return; }
        if (_table == null) { Log("还没有按键表——请先到【语料】页编译。"); return; }

        _runResults.Clear();
        _rtResult.Clear();
        _running = true;
        UpdateButtons();
        if (!await EnsureHostReadyAsync())
            Log("警告：抢不到前台窗口。请把本窗口点到最前面再开始，否则按键会发给别的程序。");
        await Task.Delay(300);   // 让焦点稳定，否则前几个键会发给别的控件

        var opt = new RunOptions
        {
            IntervalMs = (int)_nudInterval.Value,
            SettleMs = (int)_nudSettle.Value,
            CheckpointPath = CheckpointFile,
        };

        _runner = new Runner(_host, _sender, _ctrl);
        _runner.UnitDone += OnUnitDone;

        Log($"开始：从第 {startIndex} 个单元，间隔 {opt.IntervalMs}ms，静默期 {opt.SettleMs}ms");

        var res = await _runner.RunAsync(_table, opt, startIndex, seed);

        _last = res;
        _running = false;
        UpdateButtons();

        Log("结束：" + res.ReasonText);
        foreach (var e in _runner.SendErrors.Distinct())
            Log("按键串警告：" + e);
        if (_runner.AutoCommitSkips > 0)
            Log($"其中 {_runner.AutoCommitSkips} 句检测到高置信度自动上屏，已跳过上屏键（按键表页句尾标绿⏎）。");
        Log(res.Stats.Summary());
        _lblStats.Text = FormatSummary(res.Stats.Summary());
        try { Exporter.ToJson(LastRunFile, res, BuildMeta()); } catch { }
    }

    private async Task ResumeAsync()
    {
        var ck = Checkpoint.Load(CheckpointFile);
        if (ck == null) { Log("没有找到断点文件。"); return; }

        var table = KeyTable.FromJson(ck.KeyTableJson);
        if (table == null) { Log("断点文件里的按键表已损坏。"); return; }

        _table = table;
        _keyPanel.Bind(table);
        Log($"从断点恢复：第 {ck.NextIndex} 个单元（保存于 {ck.SavedAt:MM-dd HH:mm:ss}）");
        await StartAsync(ck.NextIndex, ck.Stats);
    }

    /// <summary>所有停止来源统一汇到控制层；热键/失焦入口在 WndProc 里指定各自来源。</summary>
    private void StopRun(string why) => _ctrl.RequestStop(StopSource.StopButton);

    private void OnUnitDone(UnitResult u)
    {
        if (InvokeRequired) { BeginInvoke(new Action<UnitResult>(OnUnitDone), u); return; }

        _runResults.Add(u);
        if (!u.Correct || !_cbOnlyError.Checked) AppendResult(u);
        if (u.AutoCommit) _keyPanel.MarkAutoCommit(u.Index);
        if (_lblStats.Parent != null && _runner != null)
            _lblStats.Text = FormatSummary(_runner.Live.Summary());
    }

    /// <summary>把一条结果按三行式追加到逐句对照：头部信息 / 按键串 / 期望（标错）/ 实际（标错）。</summary>
    private void AppendResult(UnitResult u)
    {
        AppendRt($"{u.Index + 1}. ", Color.DimGray);
        AppendRt(u.Correct ? "✓ " : "✗ ", u.Correct ? Color.Green : Color.Red);
        AppendRt($"S{u.Sub} D{u.Del} I{u.Ins}  {u.Ms}ms  上屏{u.CommitEvents}" + (u.AutoCommit ? " 自动上屏" : ""),
                 u.Correct ? Color.DimGray : Color.Black);
        AppendRt(Environment.NewLine, Color.Black);

        var (markedExpected, markedActual) = Levenshtein.Mark(u.Expected, u.Actual);
        AppendMarked("   按键串  ", u.Keys);
        AppendMarked("   期望    ", markedExpected);
        AppendMarked("   实际    ", markedActual);
        if (u.CommitSegments.Count > 0)
            AppendMarked("   上屏    ", string.Join(" ", u.CommitSegments));
        AppendRt(Environment.NewLine + Environment.NewLine, Color.Black);
    }

    /// <summary>追加一段文本，[ ] 括起的部分用红色显示（括号本身不显示）。</summary>
    private void AppendMarked(string prefix, string marked)
    {
        AppendRt(prefix, Color.DimGray);
        bool inError = false;
        var sb = new System.Text.StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            AppendRt(sb.ToString(), inError ? Color.Red : _rtResult.ForeColor);
            sb.Clear();
        }

        foreach (char c in marked)
        {
            if (c == '[') { Flush(); inError = true; continue; }
            if (c == ']') { Flush(); inError = false; continue; }
            sb.Append(c);
        }
        Flush();
        AppendRt(Environment.NewLine, Color.Black);
    }

    private void AppendRt(string s, Color color)
    {
        _rtResult.SelectionStart = _rtResult.TextLength;
        _rtResult.SelectionColor = color;
        _rtResult.AppendText(s);
        _rtResult.SelectionColor = _rtResult.ForeColor;
    }

    /// <summary>把一行式统计摘要按分号折成 5 行，实时统计区更好读。</summary>
    private static string FormatSummary(string s)
        => s.Replace("；", "；\n");

    /// <summary>按「只看错误」筛选重新渲染全部结果。</summary>
    private void RebuildRows()
    {
        _rtResult.Clear();
        foreach (var u in _runResults)
            if (!u.Correct || !_cbOnlyError.Checked) AppendResult(u);
    }

    // ───────────────────────── 标定 ─────────────────────────

    private async Task RunCalibAsync()
    {
        if (_running) { Log("正在运行中，请先停止。"); return; }

        var opt = _calib.CurrentOptions;
        if (opt.Keys.Length == 0 || opt.Expect.Length == 0)
        {
            Log("请填入按键串与期望上屏文本。");
            return;
        }

        _running = true;
        UpdateButtons();
        _ctrl.Reset();          // 清掉上一轮遗留的停止标志，否则闸门会直接拦死标定
        _calib.ClearLog();
        _calib.Append($"标定：\"{opt.Keys}\" → 期望 \"{opt.Expect}\"，每组 {opt.Repeats} 次，命中即停");
        if (!await EnsureHostReadyAsync())
            _calib.Append("警告：抢不到前台窗口，标定结果不可信。请把本窗口点到最前面。");
        await Task.Delay(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Calibrator.RunAsync(_host, _sender, _ctrl, opt,
            step => _calib.Append(step.ToString()));
        sw.Stop();

        _calib.Append($"耗时 {sw.Elapsed.TotalSeconds:F1}s");
        _calib.Append("结果：" + r);
        _calib.ShowResult(r);

        _running = false;
        UpdateButtons();
        Log("标定结束：" + r);
    }

    // ───────────────────────── 导出 ─────────────────────────

    private ReportMeta BuildMeta() => new()
    {
        ImeName = Native.GetImeName(),
        IntervalMs = (int)_nudInterval.Value,
        CodeTablePath = _corpus.CodeTablePath,
    };

    private void Export(string kind)
    {
        if (_last == null || _last.Units.Count == 0)
        {
            Log("没有可导出的结果——先跑一轮。");
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = kind switch
            {
                "json" => "JSON|*.json",
                "csv" => "CSV|*.csv",
                _ => "HTML|*.html",
            },
            FileName = $"imebench-{DateTime.Now:yyyyMMdd-HHmmss}.{kind}",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var meta = BuildMeta();
            switch (kind)
            {
                case "json": Exporter.ToJson(dlg.FileName, _last, meta); break;
                case "csv": Exporter.ToCsv(dlg.FileName, _last); break;
                default: Exporter.ToHtml(dlg.FileName, _last, meta); break;
            }
            Log("已导出：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            Log("导出失败：" + ex.Message);
        }
    }

    // ──────────────────── 无人值守端到端烟测 ────────────────────
    // 用法：ImeBench.exe --smoke <码表> <文章> [间隔ms]
    // 省略间隔时先自动标定再跑。结果导出到 %LOCALAPPDATA%\ImeBench\smoke-*

    public async Task<int> RunSmokeAsync(string codePath, string articlePath, int? intervalMs)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            _corpus.LoadCode(codePath);
            _corpus.SetArticle(File.ReadAllText(articlePath));
            _corpus.Compile();

            if (_table == null) { Log("烟测失败：按键表为空（码表没覆盖文章？）"); return 2; }
            Log($"编译结果：{_table.UnitCount} 单元 / {_table.TotalKeys} 键 / 期望 {_table.ExpectedText.Length} 字");

            if (intervalMs.HasValue)
            {
                _nudInterval.Value = Math.Min(_nudInterval.Maximum, intervalMs.Value);
            }
            else
            {
                await RunCalibAsync();
                if (_nudInterval.Value <= 0)
                    Log("警告：标定未找到可用档位，仍按 0ms 全速跑，结果可能失真");
            }

            await StartAsync(0, null);

            if (_last != null && _last.Units.Count > 0)
            {
                var meta = BuildMeta();
                Exporter.ToJson(Path.Combine(AppDir, "smoke-report.json"), _last, meta);
                Exporter.ToCsv(Path.Combine(AppDir, "smoke-report.csv"), _last);
                Exporter.ToHtml(Path.Combine(AppDir, "smoke-report.html"), _last, meta);
                Log("报告已写入 " + AppDir);
            }

            SaveLog(Path.Combine(AppDir, "smoke.log"));
            return _last != null && _last.Reason == RunEndReason.Completed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("烟测异常：" + ex);
            SaveLog(Path.Combine(AppDir, "smoke.log"));
            return 3;
        }
    }

    private void SaveLog(string path)
    {
        lock (_logLock)
        {
            try { File.WriteAllLines(path, _logLines); } catch { }
        }
    }

    // ───────────────────────── 杂项 ─────────────────────────

    private void CheckExistingCheckpoint()
    {
        var ck = Checkpoint.Load(CheckpointFile);
        if (ck == null || ck.NextIndex <= 0) { _btnResume.Enabled = false; return; }
        _btnResume.Enabled = true;
        Log($"发现断点：第 {ck.NextIndex} 个单元（{ck.SavedAt:MM-dd HH:mm:ss}）。点【从断点继续】接着跑。");
    }

    private void UpdateButtons()
    {
        _btnStart.Enabled = !_running;
        _btnStop.Enabled = _running;
    }

    private void UpdateBar()
    {
        bool structOk = InputSender.StructOk;
        bool foreground = GetForegroundWindow() == Handle;
        bool focused = _host.Focused;

        // 运行中每 500ms 刷新一次实时统计（含句中已发生的 IME 提交次数）
        if (_running && _runner != null)
        {
            _runner.Live.CommitEvents = _runner.LiveCommits;
            _lblStats.Text = FormatSummary(_runner.Live.Summary());
        }

        _lblStatus.Text = $"SendInput {InputSender.StructSize}B {(structOk ? "OK" : "错误!")}"
                        + $" | 前台 {(foreground ? "本窗口" : "其他")}"
                        + $" | 宿主焦点 {(focused ? "有" : "无")}"
                        + $" | 热键 {(_hotkeyOk ? "已注册" : "失败")}"
                        + $" | 间隔 {_nudInterval.Value}ms";

        _lblStatus.ForeColor = structOk && foreground ? SystemColors.ControlText : Color.DarkRed;
        _btnResume.Enabled = !_running && File.Exists(CheckpointFile);
    }

    /// <summary>
    /// 把前台窗口与键盘焦点都抢回宿主。
    /// 只调 Control.Focus() 不够 —— 窗体不在前台时它不生效，
    /// 按键会被发到别的窗口（表现为拼音当英文直接插进去，IME 完全没参与）。
    /// </summary>
    private bool ActivateHost()
    {
        if (!Visible || IsDisposed) return false;

        // 宿主在「运行」页上：Tab页不可见时里面的控件拿不到键盘焦点，必须先切过去
        if (_tabs.SelectedTab != _pageRun)
        {
            _tabs.SelectedIndex = _tabs.TabPages.IndexOf(_pageRun);
            Application.DoEvents();   // 让页签切换和布局完成，否则焦点设置会落空
        }

        if (GetForegroundWindow() != Handle)
        {
            Activate();
            SetForegroundWindow(Handle);
        }

        if (!_host.Focus() || GetFocus() != _host.Handle)
            SetFocus(_host.Handle);

        Application.DoEvents();

        // 两个条件都要满足：窗口在前台（SendInput 发给前台窗口）+ 焦点在宿主
        return GetForegroundWindow() == Handle && GetFocus() == _host.Handle;
    }

    /// <summary>
    /// 反复尝试抢前台。
    /// 从脚本/后台 shell 启动时，Windows 常会拒绝一次性把前台让给新窗口，
    /// 于是"失焦即停"闸门会在第一个键就触发。重试几轮即可。
    /// </summary>
    private async Task<bool> EnsureHostReadyAsync(int tries = 12)
    {
        for (int i = 0; i < tries; i++)
        {
            if (ActivateHost()) return true;
            await Task.Delay(400);
        }
        return false;
    }

    private readonly object _logLock = new();
    private readonly List<string> _logLines = new();

    /// <summary>
    /// 日志：最新一条显示在底部状态栏，完整时间线落盘 data\session.log。
    /// 独立日志面板已取消（实时统计已承担运行期反馈）。
    /// </summary>
    public void Log(string s)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {s}";
        lock (_logLock) _logLines.Add(line);
        try { File.AppendAllText(SessionLogFile, line + Environment.NewLine); } catch { }

        try
        {
            if (_lblStatus.IsDisposed) return;
            if (_lblStatus.InvokeRequired) { _lblStatus.BeginInvoke(new Action<string>(x => _lblStatus.Text = x), s); return; }
            _lblStatus.Text = s;
        }
        catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 小屏（如 768p）下别让底部状态栏被屏幕边缘裁掉
        var wa = Screen.FromControl(this).WorkingArea;
        if (Height > wa.Height) Height = wa.Height;
        if (Width > wa.Width) Width = wa.Width;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _sender.MainHwnd = Handle;
        _ctrl.MainHwnd = Handle;
        _hotkeyOk = RegisterHotKey(Handle, HOTKEY_ID, HOTKEY_MOD, HOTKEY_VK);
        if (!_hotkeyOk)
            Log("警告：全局热键注册失败（可能被占用）。仍可用【停止】按钮。");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_hotkeyOk) UnregisterHotKey(Handle, HOTKEY_ID);
        _timer.Stop();
        RememberWindowSize();
        RememberRunOptions();
        _corpus.SaveSettings();   // 便携化：方案随程序走
        base.OnFormClosing(e);
    }

    /// <summary>恢复上次记录的窗口大小（关窗时由 RememberWindowSize 写入）。</summary>
    private void RestoreWindowSize()
    {
        var s = _corpus.Settings;
        if (s.WindowWidth >= 400 && s.WindowHeight >= 300)
            Size = new Size(s.WindowWidth, s.WindowHeight);
        if (s.WindowMaximized)
            WindowState = FormWindowState.Maximized;
    }

    /// <summary>恢复运行页/标定页参数（按键间隔、静默期、标定输入、只看错误）。</summary>
    private void RestoreRunOptions()
    {
        var s = _corpus.Settings;
        _nudInterval.Value = Math.Clamp(s.IntervalMs, _nudInterval.Minimum, _nudInterval.Maximum);
        _nudSettle.Value = Math.Clamp(s.SettleMs, _nudSettle.Minimum, _nudSettle.Maximum);
        _cbOnlyError.Checked = s.OnlyErrors;
        _calib.ApplyOptions(s.CalibKeys, s.CalibExpect, s.CalibRepeats, s.CalibMargin);
    }

    /// <summary>关窗前把全部参数写进设置。</summary>
    private void RememberRunOptions()
    {
        var s = _corpus.Settings;
        s.IntervalMs = (int)_nudInterval.Value;
        s.SettleMs = (int)_nudSettle.Value;
        s.OnlyErrors = _cbOnlyError.Checked;
        var co = _calib.CurrentOptions;
        s.CalibKeys = co.Keys;
        s.CalibExpect = co.Expect;
        s.CalibRepeats = co.Repeats;
        s.CalibMargin = co.SafetyMarginMs;
    }

    private void RememberWindowSize()
    {
        var s = _corpus.Settings;
        if (WindowState == FormWindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        s.WindowMaximized = WindowState == FormWindowState.Maximized;
    }

    protected override void WndProc(ref Message m)
    {
        // 控制层入口 1：全局热键 → 停止（宿主会吞普通键，只有全局热键可靠）
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            _ctrl.RequestStop(StopSource.HotKey);
            return;
        }

        // 控制层入口 2：主窗口失焦 → 停止
        if (m.Msg == WM_ACTIVATE && _running)
        {
            int state = m.WParam.ToInt32() & 0xFFFF;
            if (state == WA_INACTIVE) _ctrl.RequestStop(StopSource.LostFocus);
        }

        base.WndProc(ref m);
    }
}
