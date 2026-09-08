using System.ComponentModel;
using System.Text;
using ImeBench.Core;

namespace ImeBench.UI;

internal static class Ui
{
    public static Label Lbl(string text, int width = 0) => new()
    {
        Text = text,
        AutoSize = width == 0,
        Width = width,
        Padding = new Padding(0, 6, 6, 0),
    };

    /// <summary>
    /// 不可聚焦的按钮。
    /// 普通按钮点击后会把键盘焦点抢走，按键就发给按钮而不是宿主，IME 直接不参与。
    /// 关掉 Selectable 后点击不会夺取焦点。
    /// </summary>
    private sealed class NoFocusButton : Button
    {
        public NoFocusButton() => SetStyle(ControlStyles.Selectable, false);
    }

    public static Button Btn(string text, EventHandler onClick, int width = 0)
    {
        var b = new NoFocusButton
        {
            Text = text,
            AutoSize = width == 0,
            Padding = new Padding(8, 4, 8, 4),
            Margin = new Padding(3),
        };
        if (width != 0) b.Width = width;
        b.Click += onClick;
        return b;
    }

    public static DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            // 绑定 DataSource 后必须关掉，否则会把对象上所有 public 属性
            // （Ms/Errors/Correct/Flag 等）自动追加成新列，把手工列挤变形
            AutoGenerateColumns = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };
        g.DefaultCellStyle.Font = new Font("Microsoft YaHei", 9f);
        return g;
    }

    public static NumericUpDown Num(int min, int max, int value, int width = 70) => new()
    {
        Minimum = min, Maximum = max, Value = value, Width = width,
    };

    /// <summary>极简输入对话框（不引 Microsoft.VisualBasic 依赖）。</summary>
    public static string? Show(IWin32Window owner, string text, string caption, string def)
    {
        using var form = new Form
        {
            Text = caption, Width = 420, Height = 160,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
        };
        var lbl = new Label { Text = text, Left = 12, Top = 14, Width = 380, Height = 40 };
        var tb = new TextBox { Text = def, Left = 12, Top = 58, Width = 380 };
        var ok = new Button { Text = "确定", Left = 236, Top = 90, Width = 76, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 316, Top = 90, Width = 76, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}

// ─────────────────────────────────────────────────────────────
// 语料面板：自动读码表文件夹 + 文章文件夹下拉选择 + 编译成按键表
// 完全便携化：所有数据都在 exe 文件夹的 data\ 下
// ─────────────────────────────────────────────────────────────
internal sealed class CorpusPanel : UserControl
{
    // 便携化路径：码表和文章直接放 exe 文件夹下的一级目录
    public static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly string CodeDir = Path.Combine(AppContext.BaseDirectory, "码表");
    public static readonly string ArticleDir = Path.Combine(AppContext.BaseDirectory, "文章");
    private static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");

    private readonly Label _lblCodeStatus = new() { AutoSize = true, Padding = new Padding(0, 8, 8, 0) };
    private readonly ComboBox _cbCode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _cbArticle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly TextBox _tbArticle = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
        Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei", 10f),
    };
    private readonly Label _lblStatus = new() { AutoSize = true, Padding = new Padding(4, 8, 4, 4) };
    private readonly NumericUpDown _nudMaxChars = Ui.Num(1, 500, 20, 60);
    private readonly NumericUpDown _nudTailDelay = Ui.Num(0, 5000, 0, 70);
    private readonly CheckBox _cbPunct = new() { Text = "标点顶屏", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox _tbCommit = new() { Text = "_", Width = 50 };

    private CodeTable _code = new();
    private bool _loadingList;          // 填充下拉框时抑制 SelectedIndexChanged

    private CorpusSettings _settings;

    public event Action<KeyTable>? Compiled;

    /// <summary>码表来源说明（写进报告 meta）。</summary>
    public string CodeTablePath => CodeDir;

    public CompileOptions CurrentOptions => new()
    {
        MaxCharsPerUnit = (int)_nudMaxChars.Value,
        TailDelayMs = (int)_nudTailDelay.Value,
        PunctCommits = _cbPunct.Checked,
        CommitKeys = _tbCommit.Text.Length == 0 ? "_" : _tbCommit.Text,
    };

    public CorpusPanel()
    {
        Dock = DockStyle.Fill;
        Directory.CreateDirectory(CodeDir);
        Directory.CreateDirectory(ArticleDir);
        _settings = CorpusSettings.Load(SettingsFile);
        _settings.FilePath = SettingsFile;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 码表（自动加载）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 编译选项
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 文章来源
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 文章编辑
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 状态

        // 行 1：码表（data\码表\ 下拉选择，选中即载入）
        var rowCode = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, MinimumSize = new Size(0, 40),
        };
        rowCode.Controls.AddRange(new Control[]
        {
            Ui.Lbl("码表(\\码表):"), _cbCode, _lblCodeStatus,
            Ui.Btn("重新加载", (_, _) => ReloadCodeList(_cbCode.SelectedItem as string)),
        });
        _cbCode.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingList) return;
            if (_cbCode.SelectedItem is string file)
            {
                LoadCodeFile(Path.Combine(CodeDir, file));
                SaveSettings();
            }
        };

        // 行 2：编译选项
        var rowOpt = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, MinimumSize = new Size(0, 40),
        };
        rowOpt.Controls.AddRange(new Control[]
        {
            Ui.Lbl("单句上限:"), _nudMaxChars, Ui.Lbl("字"),
            Ui.Lbl("句尾等待ms:"), _nudTailDelay,
            Ui.Lbl("上屏键:"), _tbCommit,
            _cbPunct,
        });

        // 行 3：文章来源
        var rowArt = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, MinimumSize = new Size(0, 40),
        };
        rowArt.Controls.AddRange(new Control[]
        {
            Ui.Lbl("文章(\\文章):"), _cbArticle,
            Ui.Btn("打开文章文件夹", (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ArticleDir, UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "ImeBench"); }
            }),
        });
        _cbArticle.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingList) return;
            if (_cbArticle.SelectedItem is string file)
            {
                LoadArticleFile(Path.Combine(ArticleDir, file));
                SaveSettings();
            }
        };

        // 行 4：文章编辑（下拉选中后加载进来，仍可手动修改/粘贴）
        var gbArticle = new GroupBox { Text = "测试文章（选中即载入，也可直接粘贴）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        gbArticle.Controls.Add(_tbArticle);

        // 行 5：编译状态
        var rowStatus = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, MinimumSize = new Size(0, 40),
        };
        rowStatus.Controls.Add(Ui.Btn("编译 →", (_, _) => Compile()));
        rowStatus.Controls.Add(_lblStatus);

        root.Controls.Add(rowCode, 0, 0);
        root.Controls.Add(rowOpt, 0, 1);
        root.Controls.Add(rowArt, 0, 2);
        root.Controls.Add(gbArticle, 0, 3);
        root.Controls.Add(rowStatus, 0, 4);
        Controls.Add(root);

        // 应用记忆的方案，再加载码表与文章列表
        _nudMaxChars.Value = Math.Clamp(_settings.MaxCharsPerUnit, _nudMaxChars.Minimum, _nudMaxChars.Maximum);
        _nudTailDelay.Value = Math.Clamp(_settings.TailDelayMs, _nudTailDelay.Minimum, _nudTailDelay.Maximum);
        _tbCommit.Text = string.IsNullOrEmpty(_settings.CommitKeys) ? "_" : _settings.CommitKeys;
        _cbPunct.Checked = _settings.PunctCommits;

        ReloadCodeList(_settings.Code);
        RefreshArticleList(_settings.Article);
    }

    /// <summary>刷新码表下拉框；尽量恢复上次选中的码表。</summary>
    public void ReloadCodeList(string? select = null)
    {
        _loadingList = true;
        try
        {
            _cbCode.Items.Clear();
            var files = Directory.Exists(CodeDir)
                ? Directory.GetFiles(CodeDir, "*.txt").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string?>();
            foreach (var f in files) _cbCode.Items.Add(f);

            string want = select ?? _settings.Code;
            int idx = want.Length == 0 ? -1 : _cbCode.Items.IndexOf(want);
            _cbCode.SelectedIndex = idx >= 0 ? idx : (files.Count > 0 ? 0 : -1);
        }
        finally { _loadingList = false; }

        if (_cbCode.SelectedItem is string file)
            LoadCodeFile(Path.Combine(CodeDir, file));
        else
            _lblCodeStatus.Text = $"（\\码表 里没有 .txt 码表）";
    }

    private void LoadCodeFile(string path)
    {
        try
        {
            _code = CodeTable.Load(path);
            _lblCodeStatus.Text = $"{Path.GetFileName(path)}：{_code.Count} 字，多音字 {_code.AmbiguousCount} 个（取首码）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "码表加载失败：" + ex.Message, "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>刷新文章下拉框；尽量恢复上次选中的方案。</summary>
    public void RefreshArticleList(string? select = null)
    {
        _loadingList = true;
        try
        {
            _cbArticle.Items.Clear();
            var files = Directory.Exists(ArticleDir)
                ? Directory.GetFiles(ArticleDir, "*.txt").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string?>();
            foreach (var f in files) _cbArticle.Items.Add(f);

            string want = select ?? _settings.Article;
            int idx = want.Length == 0 ? -1 : _cbArticle.Items.IndexOf(want);
            _cbArticle.SelectedIndex = idx >= 0 ? idx : (files.Count > 0 ? 0 : -1);
        }
        finally { _loadingList = false; }

        if (_cbArticle.SelectedItem is string file)
            LoadArticleFile(Path.Combine(ArticleDir, file));
    }

    private void LoadArticleFile(string path)
    {
        try { _tbArticle.Text = File.ReadAllText(path); }
        catch (Exception ex) { MessageBox.Show(this, "文章读取失败：" + ex.Message, "ImeBench"); }
    }

    /// <summary>直接加载一个码表文件（--smoke 无人值守用）。</summary>
    public void LoadCode(string path)
    {
        try
        {
            _code = CodeTable.Load(path);
            _lblCodeStatus.Text = $"指定文件：{path}（{_code.Count} 字）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "码表加载失败：" + ex.Message, "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void SetArticle(string text) => _tbArticle.Text = text;

    public void Compile()
    {
        string article = _tbArticle.Text;
        if (article.Length == 0)
        {
            MessageBox.Show(this, "请先选择或粘贴测试文章。", "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_code.Count == 0)
        {
            MessageBox.Show(this, "码表为空——请把码表 .txt 放进 exe 旁的「\\码表」文件夹后点「重新加载」。", "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var res = Compiler.Build(article, _code, CurrentOptions);
        _lblStatus.Text = res.ToString();
        Compiled?.Invoke(res.Table);
        SaveSettings();
    }

    /// <summary>把当前方案（码表/文章选择 + 全部编译选项）写入 exe 文件夹。</summary>
    public void SaveSettings()
    {
        _settings.Code = _cbCode.SelectedItem as string ?? "";
        _settings.Article = _cbArticle.SelectedItem as string ?? "";
        _settings.MaxCharsPerUnit = (int)_nudMaxChars.Value;
        _settings.TailDelayMs = (int)_nudTailDelay.Value;
        _settings.CommitKeys = _tbCommit.Text;
        _settings.PunctCommits = _cbPunct.Checked;
        _settings.Save();
    }
}

// ─────────────────────────────────────────────────────────────
// 按键表面板：可编辑的中间产物
// ─────────────────────────────────────────────────────────────
internal sealed class KeyTablePanel : UserControl
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _lblInfo = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 8, 6, 6) };
    private KeyTable? _table;

    public event Action? Edited;

    public KeyTable? Table => _table;

    public KeyTablePanel()
    {
        Dock = DockStyle.Fill;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", DataPropertyName = "Index", FillWeight = 6, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "按键串（可编辑，⏎=确认上屏）", DataPropertyName = "DisplayText", FillWeight = 47 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "期望", DataPropertyName = "Expected", FillWeight = 21, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "实际", DataPropertyName = "Actual", FillWeight = 21, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "错", DataPropertyName = "Flag", FillWeight = 5, ReadOnly = true });
        _grid.CellValueChanged += (_, _) => ApplyEdit();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false, Padding = new Padding(3) };
        buttons.Controls.Add(Ui.Btn("保存按键表", (_, _) => SaveTable()));
        buttons.Controls.Add(Ui.Btn("载入按键表", (_, _) => LoadTable()));

        var inner = new Panel { Dock = DockStyle.Fill };
        inner.Controls.Add(_grid);
        inner.Controls.Add(buttons);
        inner.Controls.Add(_lblInfo);
        Controls.Add(inner);
    }

    public void Bind(KeyTable table)
    {
        _table = table;
        _grid.DataSource = new BindingList<Row>(table.Units.Select((u, i) => new Row
        {
            Index = i,
            Unit = u,
            DisplayText = u.DisplayText,
            Expected = table.ExpectedOf(u),
        }).ToList());
        _lblInfo.Text = $"共 {table.UnitCount} 个上屏单元 / {table.TotalKeys} 个按键 / 期望 {table.ExpectedText.Length} 字";
    }

    /// <summary>把某单元的实际结果写回网格。</summary>
    public void SetActual(int index, string actual, bool correct)
    {
        if (_grid.DataSource is not BindingList<Row> rows) return;
        if (index < 0 || index >= rows.Count) return;
        rows[index].Actual = actual;
        rows[index].Flag = correct ? "✓" : "✗";
    }

    public void RefreshGrid()
    {
        if (_grid.DataSource is BindingList<Row> rows)
            _grid.Invalidate();
    }

    private void ApplyEdit()
    {
        if (_table == null || _grid.DataSource is not BindingList<Row> rows) return;
        for (int i = 0; i < rows.Count && i < _table.Units.Count; i++)
        {
            var toks = ParseKeys(rows[i].DisplayText);
            if (toks.Count > 0) _table.Units[i].Tokens = toks;
        }
        Edited?.Invoke();
    }

    /// <summary>把显示串解析回 token 序列：⏎=确认上屏，␣=音节分隔符，其余为编码按键。</summary>
    public static List<KeyToken> ParseKeys(string display)
    {
        var toks = new List<KeyToken>();
        var sb = new StringBuilder();
        foreach (char c in display)
        {
            if (c == '⏎')
            {
                Flush();
                toks.Add(new KeyToken { Kind = KeyTokenKind.Commit, Keys = "_" });
            }
            else if (c == '␣')
            {
                Flush();
                toks.Add(new KeyToken { Kind = KeyTokenKind.Sep, Keys = "_" });
            }
            else sb.Append(c);
        }
        Flush();
        return toks;

        void Flush()
        {
            if (sb.Length == 0) return;
            toks.Add(new KeyToken { Kind = KeyTokenKind.Key, Keys = sb.ToString() });
            sb.Clear();
        }
    }

    private void SaveTable()
    {
        if (_table == null) return;
        using var dlg = new SaveFileDialog { Filter = "按键表|*.ikeys.json", FileName = "keytabel.ikeys.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _table.Save(dlg.FileName);
    }

    private void LoadTable()
    {
        using var dlg = new OpenFileDialog { Filter = "按键表|*.ikeys.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var t = KeyTable.Load(dlg.FileName);
        if (t == null)
        {
            MessageBox.Show(this, "按键表解析失败。", "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Bind(t);
    }

    public sealed class Row
    {
        public int Index { get; set; }
        public string DisplayText { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
        public string Flag { get; set; } = "";

        [Browsable(false)]
        public KeyUnit? Unit { get; set; }
    }
}

// ─────────────────────────────────────────────────────────────
// 标定面板：现场测出这台机器 + 这个输入法的最快稳定间隔
// ─────────────────────────────────────────────────────────────
internal sealed class CalibPanel : UserControl
{
    private readonly TextBox _tbKeys = new() { Text = "vsgo_", Width = 160 };
    private readonly TextBox _tbExpect = new() { Text = "中国", Width = 90 };
    private readonly NumericUpDown _nudRepeats = Ui.Num(3, 200, 20);
    private readonly NumericUpDown _nudMargin = Ui.Num(0, 20, 1);
    private readonly TextBox _tbLog = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9.5f), WordWrap = false,
    };
    private readonly Label _lblResult = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 8, 6, 6), Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold) };

    public event Action<int>? Recommended;

    public int MarginMs => (int)_nudMargin.Value;

    public CalibPanel()
    {
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, WrapContents = true, AutoSize = true,
            MinimumSize = new Size(0, 40), Padding = new Padding(3),
        };
        top.Controls.AddRange(new Control[]
        {
            Ui.Lbl("按键串:"), _tbKeys,
            Ui.Lbl("期望上屏:"), _tbExpect,
            Ui.Lbl("每组重复:"), _nudRepeats,
            Ui.Lbl("容错+ms:"), _nudMargin,
        });

        var inner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        inner.Controls.Add(_tbLog);
        inner.Controls.Add(_lblResult);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false, Padding = new Padding(3) };
        bottom.Controls.Add(Ui.Btn("开始标定", (_, _) => StartRequested?.Invoke(this, EventArgs.Empty)));

        Controls.Add(inner);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    public event EventHandler? StartRequested;

    public CalibOptions CurrentOptions => new()
    {
        Keys = _tbKeys.Text,
        Expect = _tbExpect.Text,
        Repeats = (int)_nudRepeats.Value,
        SafetyMarginMs = (int)_nudMargin.Value,
    };

    public void Append(string line)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(Append), line); return; }
        _tbLog.AppendText(line + Environment.NewLine);
    }

    public void ClearLog() => _tbLog.Clear();

    public void ShowResult(CalibResult r)
    {
        if (InvokeRequired) { BeginInvoke(new Action<CalibResult>(ShowResult), r); return; }
        _lblResult.Text = r.ToString();
        _lblResult.ForeColor = r.Found ? Color.DarkGreen : Color.DarkRed;
        if (r.Found && r.RecommendedMs.HasValue)
            Recommended?.Invoke(r.RecommendedMs.Value);
    }
}
