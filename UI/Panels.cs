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
// 语料面板：加载码表 + 粘贴文章 + 编译成按键表 + 处理 OOV/多音字
// ─────────────────────────────────────────────────────────────
internal sealed class CorpusPanel : UserControl
{
    private readonly TextBox _tbCodePath = new() { Width = 320, ReadOnly = true };
    private readonly TextBox _tbArticle = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
        Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei", 10f),
    };
    private readonly Label _lblStatus = new() { AutoSize = true, Padding = new Padding(4, 8, 4, 4) };
    private readonly DataGridView _gridIssues = Ui.Grid();
    private readonly NumericUpDown _nudMaxKeys = Ui.Num(4, 200, 40);
    private readonly CheckBox _cbPunct = new() { Text = "标点顶屏", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox _tbCommit = new() { Text = "_", Width = 40 };
    private readonly TextBox _tbSep = new() { Text = "", Width = 40 };

    private CodeTable _code = new();
    private List<CompileIssue> _issues = new();
    private string _article = "";

    public event Action<KeyTable>? Compiled;

    public string CodeTablePath => _tbCodePath.Text;

    public CompileOptions CurrentOptions => new()
    {
        MaxKeysPerUnit = (int)_nudMaxKeys.Value,
        PunctCommits = _cbPunct.Checked,
        CommitKeys = _tbCommit.Text.Length == 0 ? " " : _tbCommit.Text,
        SeparatorKeys = _tbSep.Text,
    };

    public CorpusPanel()
    {
        Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 码表（放不下自动换行撑高）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 编译选项（同上）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 编译按钮 + 状态文字
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));

        // 行 1：码表
        var rowCode = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,       // 窗口窄时折到第二行，而不是把按钮裁掉
            AutoSize = true,
            MinimumSize = new Size(0, 40),
        };
        rowCode.Controls.AddRange(new Control[]
        {
            Ui.Lbl("码表:"), _tbCodePath,
            Ui.Btn("浏览…", (_, _) => BrowseCode()),
            Ui.Btn("新建示例", (_, _) => NewSampleCode()),
        });

        // 行 2：编译选项（不含编译按钮，按钮单独一行保证完整显示）
        var rowOpt = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoSize = true,
            MinimumSize = new Size(0, 40),
        };
        rowOpt.Controls.AddRange(new Control[]
        {
            Ui.Lbl("单元键长上限:"), _nudMaxKeys,
            _cbPunct,
            Ui.Lbl("确认键:"), _tbCommit,
            Ui.Lbl("音节分隔符:"), _tbSep,
        });

        // 行 3：编译按钮 + 状态文字
        var rowCompile = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoSize = true,
            MinimumSize = new Size(0, 42),
        };
        rowCompile.Controls.Add(Ui.Btn("编译 →", (_, _) => Compile()));
        rowCompile.Controls.Add(_lblStatus);

        // 行 3：文章
        var gbArticle = new GroupBox { Text = "测试文章（粘贴）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        gbArticle.Controls.Add(_tbArticle);

        // 行 4：待确认清单
        var gbIssues = new GroupBox { Text = "待确认清单（未覆盖 / 多音字）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        _gridIssues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = "KindText", FillWeight = 15 });
        _gridIssues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字符", DataPropertyName = "Ch", FillWeight = 10 });
        _gridIssues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "位置", DataPropertyName = "CharIndex", FillWeight = 12 });
        _gridIssues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "候选编码", DataPropertyName = "CandidateText", FillWeight = 63 });
        var gbIssuesInner = new Panel { Dock = DockStyle.Fill };
        var issueBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false };
        issueBtns.Controls.Add(Ui.Btn("补进码表…", (_, _) => AddSelectedToCodeTable()));
        issueBtns.Controls.Add(Ui.Btn("重新编译", (_, _) => Compile()));
        gbIssuesInner.Controls.Add(_gridIssues);
        gbIssuesInner.Controls.Add(issueBtns);
        gbIssues.Controls.Add(gbIssuesInner);

        root.Controls.Add(rowCode, 0, 0);
        root.Controls.Add(rowOpt, 0, 1);
        root.Controls.Add(rowCompile, 0, 2);
        root.Controls.Add(gbArticle, 0, 3);
        root.Controls.Add(gbIssues, 0, 4);
        Controls.Add(root);
    }

    private void BrowseCode()
    {
        using var dlg = new OpenFileDialog { Filter = "码表文件|*.txt;*.dict;*.yaml|所有文件|*.*" };
        if (dlg.ShowDialog() == DialogResult.OK) LoadCode(dlg.FileName);
    }

    private void NewSampleCode()
    {
        using var dlg = new SaveFileDialog { Filter = "码表文件|*.txt", FileName = "code-table.txt" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, CodeTable.SampleText, new UTF8Encoding(false));
        LoadCode(dlg.FileName);
    }

    public void LoadCode(string path)
    {
        try
        {
            _code = CodeTable.Load(path);
            _tbCodePath.Text = path;
            _lblStatus.Text = $"码表已加载：{_code.Count} 字，其中多音字 {_code.AmbiguousCount} 个";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "码表加载失败：" + ex.Message, "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void SetArticle(string text) => _tbArticle.Text = text;

    public void Compile()
    {
        _article = _tbArticle.Text;
        if (_article.Length == 0)
        {
            MessageBox.Show(this, "请先粘贴测试文章。", "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_code.Count == 0)
        {
            MessageBox.Show(this, "请先加载码表。", "ImeBench", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var res = Compiler.Build(_article, _code, CurrentOptions);
        _issues = res.Issues;
        _gridIssues.DataSource = new BindingList<CompileIssue>(_issues);
        _lblStatus.Text = res.ToString();
        Compiled?.Invoke(res.Table);
    }

    private void AddSelectedToCodeTable()
    {
        if (_gridIssues.CurrentRow?.DataBoundItem is not CompileIssue iss) return;
        string? input = Ui.Show(this,
            $"为「{iss.Ch}」指定输入键（留空=跳过该字符，不计入统计）", "补充码表", "");
        if (string.IsNullOrWhiteSpace(input)) return;

        _code.Add(iss.Ch, input.Trim());
        try { _code.Save(_tbCodePath.Text); } catch { /* 只读路径就算了，内存里已生效 */ }
        Compile();
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
    private readonly TextBox _tbKeys = new() { Text = "zhongguo_", Width = 160 };
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
