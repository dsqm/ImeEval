using System.Globalization;
using ImeEval.Core;

namespace ImeEval.UI;

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
    /// <summary>多行输入框（标点按键配置等），返回 null = 取消。resetText 非空时提供「重置」按钮。</summary>
    public static string? ShowMulti(IWin32Window owner, string title, string prompt, string value, string? resetText = null)
    {
        using var form = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent,
            Font = new Font("Microsoft YaHei", 9f), Size = new Size(560, 540),
            MinimizeBox = false, ShowInTaskbar = false,
        };
        // 自动换行 + 自适应高度，提示再长也不会被裁
        var lbl = new Label
        {
            Text = prompt, Dock = DockStyle.Top, AutoSize = true,
            MaximumSize = new Size(520, 0), Padding = new Padding(8, 8, 8, 6),
        };
        // 多行文本框只认 \r\n：先把 \n 统一换成换行，否则配置项会全挤在一行
        string text = (value ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

        var tb = new TextBox
        {
            Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
            WordWrap = false, Font = new Font("Consolas", 10f),
            Text = text, AcceptsReturn = true, AcceptsTab = true,
        };
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 58, WrapContents = false, AutoSize = false,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 8, 10),
        };
        var ok = new Button
        {
            Text = "确定", DialogResult = DialogResult.OK,
            Width = 96, Height = 38, AutoSize = false, Margin = new Padding(6, 0, 0, 0),
        };
        var cancel = new Button
        {
            Text = "取消", DialogResult = DialogResult.Cancel,
            Width = 96, Height = 38, AutoSize = false, Margin = new Padding(6, 0, 0, 0),
        };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);

        // 重置：把编辑框恢复成内置默认（配置存过盘后想换回默认时用）
        if (resetText != null)
        {
            var reset = new Button
            {
                Text = "重置", Width = 96, Height = 30, AutoSize = false,
                Margin = new Padding(6, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            reset.Click += (_, _) =>
                tb.Text = (resetText ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            reset.Height = 38;
            btns.Controls.Add(reset);
        }
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var inner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        inner.Controls.Add(tb);
        form.Controls.Add(inner);
        form.Controls.Add(lbl);
        form.Controls.Add(btns);

        return form.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }

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
    private readonly NumericUpDown _nudTailDelay = Ui.Num(0, 5000, 0, 110);
    private readonly CheckBox _cbPunct = new() { Text = "根据标点断句", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _cbPhrase = new() { Text = "词组优先", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox _tbCommit = new() { Text = "_", Width = 50 };

    private CodeTable _code = new();
    private Dictionary<string, string> _punct = new();   // 标点按键配置
    private string _punctText = "";                    // 标点配置的原始文本（落盘用）
    private bool _loadingList;          // 填充下拉框时抑制 SelectedIndexChanged

    private CorpusSettings _settings;

    public event Action<KeyTable>? Compiled;

    /// <summary>应用设置（含窗口大小记忆），供主窗体读写。</summary>
    public CorpusSettings Settings => _settings;

    /// <summary>码表来源说明（写进报告 meta）。</summary>
    public string CodeTablePath => CodeDir;

    public CompileOptions CurrentOptions => new()
    {
        MaxCharsPerUnit = (int)_nudMaxChars.Value,
        TailDelayMs = (int)_nudTailDelay.Value,
        SplitByPunctuation = _cbPunct.Checked,
        Punctuation = _punct,
        CommitKeys = _tbCommit.Text.Length == 0 ? "_" : _tbCommit.Text,
        PhraseFirst = _cbPhrase.Checked,
    };

    public CorpusPanel()
    {
        Dock = DockStyle.Fill;
        Directory.CreateDirectory(CodeDir);
        Directory.CreateDirectory(ArticleDir);
        _settings = CorpusSettings.Load(SettingsFile);
        _settings.FilePath = SettingsFile;
        _punctText = _settings.PunctKeys.Length == 0 ? PunctuationConfig.DefaultText : _settings.PunctKeys;
        _punct = PunctuationConfig.Parse(_punctText);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 码表（含生成按键表按钮，与运行页「开始」同位）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 编译选项
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 文章来源
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 文章编辑
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 状态

        // 行 0：码表（生成按键表按钮置首，与其他页主按钮同位，方便连点）
        var rowCode = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, MinimumSize = new Size(0, 40),
        };
        rowCode.Controls.AddRange(new Control[]
        {
            Ui.Btn("生成按键表", (_, _) => Compile()),
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
            _cbPhrase,
            Ui.Btn("设置标点按键…", (_, _) => EditPunctuation()),
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
                catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "ImeEval"); }
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
        _cbPhrase.Checked = _settings.PhraseFirst;

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

    /// <summary>码表状态文案：单字数 + 词组数（词组会整词一次输入，值得显式提示）。</summary>
    private string CodeStatusText(string prefix)
        => $"{prefix}：{_code.Count} 字" +
           (_code.PhraseCount > 0 ? $" + {_code.PhraseCount} 词组（最长 {_code.MaxPhraseElements} 字）" : "");

    private void LoadCodeFile(string path)
    {
        try
        {
            _code = CodeTable.Load(path);
            _lblCodeStatus.Text = CodeStatusText(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "码表加载失败：" + ex.Message, "ImeEval", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        catch (Exception ex) { MessageBox.Show(this, "文章读取失败：" + ex.Message, "ImeEval"); }
    }

    /// <summary>直接加载一个码表文件（--smoke 无人值守用）。</summary>
    public void LoadCode(string path)
    {
        try
        {
            _code = CodeTable.Load(path);
            _lblCodeStatus.Text = CodeStatusText($"指定文件：{path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "码表加载失败：" + ex.Message, "ImeEval", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void SetArticle(string text) => _tbArticle.Text = text;

    public void Compile()
    {
        string article = _tbArticle.Text;
        if (article.Length == 0)
        {
            MessageBox.Show(this, "请先选择或粘贴测试文章。", "ImeEval", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_code.Count == 0)
        {
            MessageBox.Show(this, "码表为空——请把码表 .txt 放进 exe 旁的「\\码表」文件夹后点「重新加载」。", "ImeEval", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var res = Compiler.Build(article, _code, CurrentOptions);
        _lblStatus.Text = res.ToString();
        Compiled?.Invoke(res.Table);
        SaveSettings();
    }

    /// <summary>编辑标点按键配置（每行：标点字符 + 空格/制表符 + 按键串）。</summary>
    private void EditPunctuation()
    {
        string? text = Ui.ShowMulti(this, "设置标点按键",
            "每行一条：标点 + 空格 + 按键串（# 开头为注释）\r\n" +
            "_ = 空格，{DELAY=100} = 等待 100ms\r\n" +
            "「重置」恢复内置默认，改完点「确定」保存。",
            _punctText,
            resetText: PunctuationConfig.DefaultText);
        if (text == null) return;

        var errors = new List<string>();
        _punct = PunctuationConfig.Parse(text, errors);
        _punctText = text;
        SaveSettings();

        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors), "标点配置有无效行",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>把当前方案（码表/文章选择 + 全部编译选项）写入 exe 文件夹。</summary>
    public void SaveSettings()
    {
        _settings.Code = _cbCode.SelectedItem as string ?? "";
        _settings.Article = _cbArticle.SelectedItem as string ?? "";
        _settings.PunctKeys = _punctText;
        _settings.MaxCharsPerUnit = (int)_nudMaxChars.Value;
        _settings.TailDelayMs = (int)_nudTailDelay.Value;
        _settings.CommitKeys = _tbCommit.Text;
        _settings.PunctCommits = _cbPunct.Checked;
        _settings.PhraseFirst = _cbPhrase.Checked;
        _settings.Save();
    }
}

// ─────────────────────────────────────────────────────────────
// 按键表面板：只读预览——每句一行，上方标注编码、下方显示字词（类似拼音注音）
// 作用是在跑测试前人工核对按键表，避免出错
// ─────────────────────────────────────────────────────────────
internal sealed class KeyTablePanel : UserControl
{
    private readonly PreviewCanvas _canvas = new();
    private readonly Label _lblInfo = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 8, 6, 6) };

    /// <summary>点「跳转到运行」时触发（主窗体切到运行页）。</summary>
    public event Action? GoRunRequested;

    public KeyTablePanel()
    {
        Dock = DockStyle.Fill;

        // 顶部工具栏：「跳转到运行」与运行页「开始」同位（三击直达）
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, WrapContents = true, AutoSize = true,
            MinimumSize = new Size(0, 44), Padding = new Padding(3),
        };
        buttons.Controls.Add(Ui.Btn("跳转到运行", (_, _) => GoRunRequested?.Invoke()));

        var inner = new Panel { Dock = DockStyle.Fill };
        inner.Controls.Add(_canvas);
        inner.Controls.Add(_lblInfo);

        // 停靠按「后加先停」处理：inner 先加 → 占按钮以下的剩余空间；buttons 后加 → 停靠顶部
        Controls.Add(inner);
        Controls.Add(buttons);
    }

    public void Bind(KeyTable table)
    {
        _canvas.Load(table);
        _lblInfo.Text = $"共 {table.UnitCount} 句 / {table.TotalKeys} 个按键 / 期望 {table.ExpectedText.Length} 字 —— 上为编码，下为字词，只读预览；绿色⏎ = 该句已自动上屏（未发空格）";
    }

    /// <summary>运行中标记某句发生了自动上屏（句尾显示绿色 ⏎）。</summary>
    public void MarkAutoCommit(int index)
    {
        if (InvokeRequired) { BeginInvoke(new Action<int>(MarkAutoCommit), index); return; }
        _canvas.MarkAutoCommit(index);
    }

    /// <summary>注音式预览画布：每句一行（上方编码、下方字词），支持滚动，万句级流畅。</summary>
    private sealed class PreviewCanvas : Panel
    {
        private sealed class Row
        {
            public int Index;                              // 句号（从 1 开始）
            public List<(string Code, string Text)> Cols = new();   // 每字一列：编码在上，字词在下
            public float Y;                                // 行顶逻辑 Y
            public float Width;                            // 行宽
            public bool AutoCommit;                        // 运行中检测到自动上屏
        }

        private readonly List<Row> _rows = new();
        private readonly Font _fontCode = new("Consolas", 10f);
        private readonly Font _fontText = new("Microsoft YaHei", 12f);
        private float _rowH, _codeH, _textH, _totalW;

        public PreviewCanvas()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            DoubleBuffered = true;
            BackColor = Color.White;
            ResizeRedraw = true;
        }

        public void Load(KeyTable? table)
        {
            _rows.Clear();
            _totalW = 0;

            if (table != null)
            {
                float y = 6;
                for (int i = 0; i < table.Units.Count; i++)
                {
                    var row = new Row { Index = i + 1 };
                    var u = table.Units[i];

                    // 逐 token 渲染，与按键表完全一致：
                    //   编码 token（有目标字）→ 编码在上、字词在下
                    //   上屏键 / 音节分隔符 / 句尾延迟 → 编码原样显示，字词行留空
                    var elems = TextElements(table.ExpectedOf(u));
                    int ei = 0;
                    foreach (var t in u.Tokens)
                    {
                        if (t.Kind == KeyTokenKind.Key && t.Target != null)
                        {
                            // TargetText 优先：词组整词占一格（编码在上、整词在下），
                            // 单字也是一个完整文本元素（代理对不会被拆成半个字符）
                            string txt = t.TargetText.Length > 0
                                ? t.TargetText
                                : (ei < elems.Count ? elems[ei] : t.Target.Value.ToString());
                            row.Cols.Add((t.Keys, txt));
                            ei += ElementCount(txt);
                        }
                        else
                        {
                            row.Cols.Add((t.Keys, ""));
                        }
                    }
                    // 数量对不齐时，字词照常显示，编码留空（不静默丢字）
                    for (; ei < elems.Count; ei++)
                        row.Cols.Add(("", elems[ei]));

                    row.Width = 4 + row.Cols.Sum(c => ColW(c.Code, c.Text)) + 10;
                    row.Y = y;
                    y += RowH() + 6;
                    _totalW = Math.Max(_totalW, row.Width);
                    _rows.Add(row);
                }
            }

            AutoScrollMinSize = new Size((int)Math.Ceiling(_totalW) + 20, (int)Math.Ceiling(_rows.Count * (RowH() + 6)) + 12);
            Invalidate();
        }

        private float RowH()
        {
            if (_rowH == 0)
            {
                using var g = CreateGraphics();
                _codeH = _fontCode.GetHeight(g);
                _textH = _fontText.GetHeight(g);
                _rowH = _codeH + _textH + 8;
            }
            return _rowH;
        }

        private float ColW(string code, string text)
        {
            using var g = CreateGraphics();
            float w1 = g.MeasureString(code, _fontCode).Width;
            float w2 = g.MeasureString(text, _fontText).Width;
            return Math.Max(w1, w2) + 6;
        }

        /// <summary>文本元素个数：代理对 / 组合字符算 1 个。</summary>
        private static int ElementCount(string s)
            => s.Length == 0 ? 0 : new StringInfo(s).LengthInTextElements;

        private static List<string> TextElements(string s)
        {
            var list = new List<string>();
            var si = new StringInfo(s ?? "");
            for (int i = 0; i < si.LengthInTextElements; i++)
                list.Add(si.SubstringByTextElements(i, 1));
            return list;
        }

        /// <summary>运行中标记某句发生了自动上屏（句尾画绿色 ⏎）。</summary>
        public void MarkAutoCommit(int unitIndex)
        {
            var row = _rows.FirstOrDefault(r => r.Index == unitIndex + 1);
            if (row == null || row.AutoCommit) return;
            row.AutoCommit = true;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_rows.Count == 0)
            {
                using var f = new Font("Microsoft YaHei", 10f);
                e.Graphics.DrawString("（还没有按键表——回语料页点「生成按键表」）", f, SystemBrushes.ControlText, 12, 12);
                return;
            }

            var g = e.Graphics;
            g.Clear(BackColor);
            // ClearType 消除文字锯齿（GDI+ 默认的灰度抗锯齿在小字号下发虚）
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

            float viewTop = -AutoScrollPosition.Y;
            float viewBottom = viewTop + Height;
            float labelW = 46;

            foreach (var row in _rows)
            {
                if (row.Y + RowH() < viewTop) continue;
                if (row.Y > viewBottom) break;

                // 句号 + 分隔线
                using var fIdx = new Font("Consolas", 9f);
                using var pSep = new Pen(Color.FromArgb(230, 230, 230));
                g.DrawString($"{row.Index}.", fIdx, SystemBrushes.ControlText, 6, row.Y + 2);
                g.DrawLine(pSep, labelW, row.Y + RowH() + 2, Math.Max(row.Width, viewBottom) + 200, row.Y + RowH() + 2);

                float x = labelW;
                foreach (var (code, text) in row.Cols)
                {
                    float w = ColW(code, text);
                    var fmt = new StringFormat { Alignment = StringAlignment.Center };
                    var rectCode = new RectangleF(x, row.Y + 1, w, _codeH);
                    var rectText = new RectangleF(x, row.Y + _codeH + 5, w, _textH);
                    g.DrawString(code, _fontCode, code.Length == 0 ? SystemBrushes.ControlLight : SystemBrushes.ControlText, rectCode, fmt);
                    g.DrawString(text, _fontText, SystemBrushes.ControlText, rectText, fmt);
                    x += w;
                }

                // 自动上屏标记：句尾绿色 ⏎（该句未发空格，由输入法置信度直接上屏）
                if (row.AutoCommit)
                {
                    using var fMark = new Font("Consolas", 10f, FontStyle.Bold);
                    g.DrawString("⏎", fMark, Brushes.Green, x + 2, row.Y + 1);
                }
            }
        }
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

    /// <summary>恢复上次保存的标定参数。</summary>
    public void ApplyOptions(string keys, string expect, int repeats, int margin)
    {
        _tbKeys.Text = keys;
        _tbExpect.Text = expect;
        _nudRepeats.Value = Math.Clamp(repeats, _nudRepeats.Minimum, _nudRepeats.Maximum);
        _nudMargin.Value = Math.Clamp(margin, _nudMargin.Minimum, _nudMargin.Maximum);
    }

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
