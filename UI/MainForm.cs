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

    private readonly DataGridView _gridResult = Ui.Grid();
    private readonly BindingList<UnitResult> _rows = new();
    private readonly NumericUpDown _nudInterval = Ui.Num(0, 500, 2);
    private readonly NumericUpDown _nudSettle = Ui.Num(0, 3000, 40);
    private readonly CheckBox _cbOnlyError = new() { Text = "只看错误", Checked = true, AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    private readonly Label _lblStats = new() { Dock = DockStyle.Fill, Padding = new Padding(8), Font = new Font("Microsoft YaHei", 10f) };
    private readonly TextBox _tbLog = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), WordWrap = false,
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

    public MainForm()
    {
        Text = "ImeBench — 输入法整句正确度评测";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1080, 780);
        Font = new Font("Microsoft YaHei", 9f);

        BuildResultGrid();
        _statusBar.Controls.Add(_lblStatus);
        Controls.Add(BuildRoot());
        Controls.Add(_statusBar);

        _sender.MainHwnd = Handle;
        _host.Log = _ => { };

        WireEvents();

        _timer.Tick += (_, _) => UpdateBar();
        _timer.Start();

        Log("就绪。流程：① 语料页加载码表+粘贴文章→编译 ② 标定页测速度 ③ 回到本页开始。");
        Log("提示：开始前请确认已切换到要测的输入法，且处于【全拼】模式。");
        CheckExistingCheckpoint();
    }

    // ───────────────────────── 布局 ─────────────────────────

    private Control BuildRoot()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(6, 4) };
        _tabs = tabs;

        var pageRun = new TabPage("运行") { Padding = new Padding(6) };
        pageRun.Controls.Add(BuildRunPage());

        var pageCorpus = new TabPage("语料") { Padding = new Padding(6) };
        pageCorpus.Controls.Add(_corpus);

        var pageKeys = new TabPage("按键表") { Padding = new Padding(6) };
        pageKeys.Controls.Add(_keyPanel);

        var pageCalib = new TabPage("速度标定") { Padding = new Padding(6) };
        pageCalib.Controls.Add(_calib);

        tabs.TabPages.AddRange(new[] { pageRun, pageCorpus, pageKeys, pageCalib });
        return tabs;
    }

    private Control BuildRunPage()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 工具栏（放不下自动换行撑高）
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));    // 宿主
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));    // 统计
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 结果
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));   // 日志

        // 工具栏：允许换行，窗口窄时自动折到第二行而不是把按钮裁掉
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoSize = true,
            MinimumSize = new Size(0, 44),
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

        // 宿主
        var gbHost = new GroupBox { Text = "输入宿主（按键发到这里，测试期间请勿切换窗口）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        gbHost.Controls.Add(_host);

        // 统计
        var gbStats = new GroupBox { Text = "实时统计", Dock = DockStyle.Fill };
        gbStats.Controls.Add(_lblStats);

        // 结果
        var gbResult = new GroupBox { Text = "逐句对照", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var resultInner = new Panel { Dock = DockStyle.Fill };
        var resultBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        resultBtns.Controls.Add(_cbOnlyError);
        _cbOnlyError.CheckedChanged += (_, _) => RebuildRows();
        resultInner.Controls.Add(_gridResult);
        resultInner.Controls.Add(resultBtns);
        gbResult.Controls.Add(resultInner);

        // 日志
        var gbLog = new GroupBox { Text = "日志", Dock = DockStyle.Fill, Padding = new Padding(6) };
        gbLog.Controls.Add(_tbLog);

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(gbHost, 0, 1);
        root.Controls.Add(gbStats, 0, 2);
        root.Controls.Add(gbResult, 0, 3);
        root.Controls.Add(gbLog, 0, 4);
        return root;
    }

    private void BuildResultGrid()
    {
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", DataPropertyName = "Index", FillWeight = 6 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "按键串", DataPropertyName = "Keys", FillWeight = 30 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "期望", DataPropertyName = "Expected", FillWeight = 28 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "实际", DataPropertyName = "Actual", FillWeight = 28 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "S", DataPropertyName = "Sub", FillWeight = 3 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "D", DataPropertyName = "Del", FillWeight = 3 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "I", DataPropertyName = "Ins", FillWeight = 3 });
        _gridResult.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "耗时ms", DataPropertyName = "Ms", FillWeight = 7 });
        _gridResult.DataSource = _rows;
    }

    private void WireEvents()
    {
        _corpus.Compiled += table =>
        {
            _table = table;
            _keyPanel.Bind(table);
            _rows.Clear();
            Log($"编译完成：{table.UnitCount} 个上屏单元 / {table.TotalKeys} 个按键 / 期望 {table.ExpectedText.Length} 字");
            _tabs.SelectedIndex = 2;
        };

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

        _rows.Clear();
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
        Log(res.Stats.Summary());
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

        _keyPanel.SetActual(u.Index, u.Actual, u.Correct);
        if (!u.Correct || !_cbOnlyError.Checked) _rows.Add(u);
        if (_lblStats.Parent != null && _runner != null)
            _lblStats.Text = _runner.Live.Summary();
    }

    private void RebuildRows()
    {
        if (_last == null) return;
        _rows.Clear();
        foreach (var u in _last.Units)
            if (!u.Correct || !_cbOnlyError.Checked) _rows.Add(u);
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
        try { File.WriteAllText(path, _tbLog.Text); } catch { }
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

    public void Log(string s)
    {
        if (_tbLog.IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
        if (_tbLog.TextLength > 200_000) _tbLog.Clear();
        _tbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
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
        _corpus.SaveSettings();   // 便携化：方案随程序走
        base.OnFormClosing(e);
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
