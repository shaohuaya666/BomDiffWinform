using System.Configuration;

namespace BomDiffWinform.Forms;

/// <summary>
/// 系统配置表单：Oracle连接、视图名称、分页大小、自动执行时间
/// </summary>
public partial class ConfigForm : Form
{
    private TextBox _txtOracleConn = null!;
    private TextBox _txtOldView = null!;
    private TextBox _txtNewView = null!;
    private NumericUpDown _numPageSize = null!;
    private CheckBox _chkAutoRun = null!;
    private DateTimePicker _dtpAutoRunTime = null!;
    private Button _btnSave = null!;
    private Button _btnCancel = null!;
    private Button _btnTestConn = null!;

    public ConfigForm()
    {
        InitializeComponent();
        LoadConfig();
    }

    private void InitializeComponent()
    {
        this.Text = "系统配置";
        this.Size = new Size(620, 480);
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(15),
            ColumnCount = 3,
            RowCount = 9,
            AutoSize = true
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        // Row 0: 标题
        var lblTitle = new Label
        {
            Text = "⚙ Oracle 连接与同步配置",
            Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold),
            ForeColor = Color.DarkBlue,
            AutoSize = true,
            Padding = new Padding(0, 5, 0, 10)
        };
        mainPanel.Controls.Add(lblTitle, 0, 0);
        mainPanel.SetColumnSpan(lblTitle, 3);

        // Row 1: Oracle连接字符串
        AddLabel(mainPanel, "Oracle连接字符串:", 1);
        _txtOracleConn = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Width = 400 };
        mainPanel.Controls.Add(_txtOracleConn, 1, 1);

        _btnTestConn = new Button { Text = "测试连接", Width = 75, Height = 28 };
        _btnTestConn.Click += BtnTestConn_Click!;
        mainPanel.Controls.Add(_btnTestConn, 2, 1);

        // Row 2: 旧视图名
        AddLabel(mainPanel, "旧视图名称:", 2);
        _txtOldView = new TextBox { Width = 200, Text = "PVS_BOM" };
        mainPanel.Controls.Add(_txtOldView, 1, 2);

        // Row 3: 新视图名
        AddLabel(mainPanel, "新视图名称:", 3);
        _txtNewView = new TextBox { Width = 200, Text = "PVS_BOM2" };
        mainPanel.Controls.Add(_txtNewView, 1, 3);

        // Row 4: 分页大小
        AddLabel(mainPanel, "分页大小(行):", 4);
        _numPageSize = new NumericUpDown
        {
            Minimum = 500,
            Maximum = 50000,
            Increment = 500,
            Value = 5000,
            Width = 120
        };
        mainPanel.Controls.Add(_numPageSize, 1, 4);

        // Row 5: 分隔线
        var separator = new Label { Text = "", Height = 2, BorderStyle = BorderStyle.Fixed3D };
        mainPanel.Controls.Add(separator, 0, 5);
        mainPanel.SetColumnSpan(separator, 3);

        // Row 6: 自动执行标题
        var lblAutoTitle = new Label
        {
            Text = "⏰ 夜间自动执行设置",
            Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold),
            ForeColor = Color.DarkBlue,
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 5)
        };
        mainPanel.Controls.Add(lblAutoTitle, 0, 6);
        mainPanel.SetColumnSpan(lblAutoTitle, 3);

        // Row 7: 启用自动执行
        AddLabel(mainPanel, "启用自动执行:", 7);
        _chkAutoRun = new CheckBox
        {
            Text = "每天定时自动执行对比",
            Checked = false,
            AutoSize = true
        };
        mainPanel.Controls.Add(_chkAutoRun, 1, 7);

        // Row 8: 自动执行时间
        AddLabel(mainPanel, "执行时间:", 8);
        _dtpAutoRunTime = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            Value = DateTime.Today.AddHours(0),
            Width = 100
        };
        mainPanel.Controls.Add(_dtpAutoRunTime, 1, 8);

        // 底部按钮
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(15, 10, 15, 15),
            Height = 50
        };

        _btnCancel = new Button { Text = "取消", Width = 80, Height = 30 };
        _btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

        _btnSave = new Button { Text = "保存配置", Width = 80, Height = 30, BackColor = Color.LightGreen };
        _btnSave.Click += BtnSave_Click!;

        buttonPanel.Controls.Add(_btnCancel);
        buttonPanel.Controls.Add(_btnSave);

        this.Controls.Add(mainPanel);
        this.Controls.Add(buttonPanel);
        this.AcceptButton = _btnSave;
        this.CancelButton = _btnCancel;
    }

    private static void AddLabel(TableLayoutPanel panel, string text, int row)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Padding = new Padding(0, 5, 10, 5)
        }, 0, row);
    }

    private void LoadConfig()
    {
        _txtOracleConn.Text = ConfigurationManager.AppSettings["OracleConnectionString"] ?? string.Empty;
        _txtOldView.Text = ConfigurationManager.AppSettings["OldViewName"] ?? "PVS_BOM";
        _txtNewView.Text = ConfigurationManager.AppSettings["NewViewName"] ?? "PVS_BOM2";

        if (int.TryParse(ConfigurationManager.AppSettings["PageSize"], out var ps))
            _numPageSize.Value = Math.Max(500, Math.Min(50000, ps));

        if (bool.TryParse(ConfigurationManager.AppSettings["AutoRunEnabled"], out var autoRun))
            _chkAutoRun.Checked = autoRun;

        if (TimeOnly.TryParse(ConfigurationManager.AppSettings["AutoRunTime"] ?? "00:00", out var time))
            _dtpAutoRunTime.Value = DateTime.Today.Add(time.ToTimeSpan());
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        // 验证输入
        if (string.IsNullOrWhiteSpace(_txtOracleConn.Text))
        {
            MessageBox.Show("Oracle连接字符串不能为空", "验证失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtOracleConn.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_txtOldView.Text))
        {
            MessageBox.Show("旧视图名称不能为空", "验证失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtOldView.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_txtNewView.Text))
        {
            MessageBox.Show("新视图名称不能为空", "验证失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtNewView.Focus();
            return;
        }

        try
        {
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

            SetAppSetting(config, "OracleConnectionString", _txtOracleConn.Text.Trim());
            SetAppSetting(config, "OldViewName", _txtOldView.Text.Trim());
            SetAppSetting(config, "NewViewName", _txtNewView.Text.Trim());
            SetAppSetting(config, "PageSize", ((int)_numPageSize.Value).ToString());
            SetAppSetting(config, "AutoRunEnabled", _chkAutoRun.Checked.ToString().ToLower());
            SetAppSetting(config, "AutoRunTime", _dtpAutoRunTime.Value.ToString("HH:mm"));

            config.Save(ConfigurationSaveMode.Minimal);
            ConfigurationManager.RefreshSection("appSettings");

            MessageBox.Show("配置已保存成功！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnTestConn_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_txtOracleConn.Text))
        {
            MessageBox.Show("请先输入Oracle连接字符串", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnTestConn.Enabled = false;
        _btnTestConn.Text = "测试中...";

        try
        {
            using var conn = new Oracle.ManagedDataAccess.Client.OracleConnection(_txtOracleConn.Text.Trim());
            conn.Open();
            MessageBox.Show("Oracle连接成功！", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Oracle连接失败:\n{ex.Message}", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnTestConn.Enabled = true;
            _btnTestConn.Text = "测试连接";
        }
    }

    private static void SetAppSetting(Configuration config, string key, string value)
    {
        var setting = config.AppSettings.Settings[key];
        if (setting != null)
            setting.Value = value;
        else
            config.AppSettings.Settings.Add(key, value);
    }
}
