using System.Configuration;
using System.Data;
using BomDiffWinform.Models;
using BomDiffWinform.Services;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Forms;

public partial class MainForm : Form
{
    // ============ UI Controls ============
    private MenuStrip _menuStrip = null!;
    private ToolStrip _toolStrip = null!;
    private ToolStripButton _btnDetailCompare = null!;
    private ToolStripButton _btnParentCompare = null!;
    private ToolStripButton _btnCancel = null!;
    private ToolStripLabel _lblProgressPercent = null!;
    private ToolStripProgressBar _progressBar = null!;
    private TabControl _tabControl = null!;
    private TabPage _tabAll = null!, _tabAdded = null!, _tabDeleted = null!, _tabModified = null!, _tabParentAgg = null!;
    private DataGridView _dgvAll = null!, _dgvAdded = null!, _dgvDeleted = null!, _dgvModified = null!, _dgvParentAgg = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _lblStatus = null!;
    private ToolStripStatusLabel _lblStats = null!;
    private ToolStripStatusLabel _lblMemory = null!;
    private Panel _panelSummary = null!;
    private Label _lblSummary = null!;

    // ============ Services ============
    private ViewMappingConfigService _mappingService = null!;
    private SchemaService _schemaService = null!;
    private OracleService _oracleService = null!;
    private SQLiteService _sqliteService = null!;
    private DiffService _diffService = null!;
    private DatabaseHelper _dbHelper = null!;
    private ScheduleService _scheduleService = null!;
    private ILogger _logger = null!;

    // ============ Dynamic Config ============
    private ComparisonConfig _cfg = null!;

    // ============ State ============
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private List<DynamicBomRow> _parentAggDiffs = new();

    public MainForm()
    {
        _logger = LogService.GetLogger<MainForm>();
        _logger.LogInformation("应用启动");

        InitializeServices();   // 必须在 InitializeComponent 之前调用（加载配置供 GridView 列定义使用）
        InitializeComponent();
        this.Load += (s, e) => InitializeSchedule();
        RefreshStats();
    }

    #region UI Initialization

    private void InitializeComponent()
    {
        this.Text = $"BOM 视图对比工具 v2.0 — {_cfg.OldViewName} vs {_cfg.NewViewName}";
        this.Size = new Size(1200, 750);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(900, 600);
        this.FormClosing += (s, e) =>
        {
            if (_isRunning)
            {
                var result = MessageBox.Show("任务正在执行中，确定要退出吗？",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result == DialogResult.No)
                { e.Cancel = true; return; }
                _cts?.Cancel();
            }
            _scheduleService?.Dispose();
        };

        BuildMenuStrip();
        BuildToolStrip();
        BuildStatusStrip();
        BuildSummaryPanel();
        BuildTabControl();
    }

    private void BuildMenuStrip()
    {
        _menuStrip = new MenuStrip();

        var configMenu = new ToolStripMenuItem("配置(&C)");
        var menuConfig = new ToolStripMenuItem("系统配置...", null, (s, e) => OpenConfigForm());
        configMenu.DropDownItems.Add(menuConfig);

        var helpMenu = new ToolStripMenuItem("帮助(&H)");
        var logMenu = new ToolStripMenuItem("日志(&L)");
        var menuOpenLogDir = new ToolStripMenuItem("打开日志目录", null, (s, e) => LogService.OpenLogDirectory());
        var menuOpenLatestLog = new ToolStripMenuItem("查看最新日志", null, (s, e) => LogService.OpenLatestLogFile());
        var menuLogInfo = new ToolStripMenuItem("日志信息", null, (s, e) =>
        {
            var files = LogService.GetLogFiles();
            var info = $"日志目录: {LogService.LogDirectory}\n日志级别: {LogService.MinimumLevel}\n" +
                       $"保留天数: {LogService.RetentionDays}天\n日志总大小: {LogService.GetLogDirectorySizeMB():F1} MB\n" +
                       $"日志文件数: {files.Count}个\n\n最近日志:\n{string.Join("\n", files.Take(5))}";
            MessageBox.Show(info, "日志信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        logMenu.DropDownItems.Add(menuOpenLogDir);
        logMenu.DropDownItems.Add(menuOpenLatestLog);
        logMenu.DropDownItems.Add(new ToolStripSeparator());
        logMenu.DropDownItems.Add(menuLogInfo);

        var menuResetDb = new ToolStripMenuItem("重置本地数据库", null, (s, e) =>
        {
            if (MessageBox.Show("确定要清空所有本地快照和差异数据吗？", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _sqliteService.ClearSnapshot("OLD");
                _sqliteService.ClearSnapshot("NEW");
                _sqliteService.ClearDiff();
                RefreshStats();
                ClearAllGrids();
                UpdateStatus("本地数据已清空", Color.Blue);
            }
        });

        var menuAbout = new ToolStripMenuItem("关于", null, (s, e) => ShowAbout());
        helpMenu.DropDownItems.Add(logMenu);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(menuResetDb);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(menuAbout);

        _menuStrip.Items.Add(configMenu);
        _menuStrip.Items.Add(helpMenu);
        this.MainMenuStrip = _menuStrip;
        this.Controls.Add(_menuStrip);
    }

    private void BuildToolStrip()
    {
        _toolStrip = new ToolStrip { Top = _menuStrip.Height };

        _btnDetailCompare = new ToolStripButton("明细对比")
        {
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font(_toolStrip.Font, FontStyle.Bold)
        };
        _btnDetailCompare.Click += BtnDetailCompare_Click!;

        _btnParentCompare = new ToolStripButton("父项聚合对比")
        {
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font(_toolStrip.Font, FontStyle.Bold)
        };
        _btnParentCompare.Click += BtnParentCompare_Click!;

        _btnCancel = new ToolStripButton("取消")
        {
            Enabled = false,
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = Color.Red
        };
        _btnCancel.Click += BtnCancel_Click!;

        _lblProgressPercent = new ToolStripLabel("0%") { Alignment = ToolStripItemAlignment.Right };
        _progressBar = new ToolStripProgressBar
        {
            Width = 200, Alignment = ToolStripItemAlignment.Right, Style = ProgressBarStyle.Continuous
        };

        _toolStrip.Items.Add(_btnDetailCompare);
        _toolStrip.Items.Add(_btnParentCompare);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_btnCancel);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_lblProgressPercent);
        _toolStrip.Items.Add(_progressBar);

        this.Controls.Add(_toolStrip);
    }

    private void BuildStatusStrip()
    {
        _statusStrip = new StatusStrip();
        _lblStatus = new ToolStripStatusLabel("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblStats = new ToolStripStatusLabel("请点击「明细对比」开始") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        _lblMemory = new ToolStripStatusLabel("内存: --") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        _statusStrip.Items.Add(_lblStatus);
        _statusStrip.Items.Add(_lblStats);
        _statusStrip.Items.Add(_lblMemory);
        this.Controls.Add(_statusStrip);
    }

    private void BuildSummaryPanel()
    {
        _panelSummary = new Panel
        {
            Dock = DockStyle.Bottom, Height = 60, BackColor = SystemColors.ControlLight
        };
        _lblSummary = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0), Font = new Font("Microsoft YaHei", 10F)
        };
        _panelSummary.Controls.Add(_lblSummary);
        this.Controls.Add(_panelSummary);
    }

    private void BuildTabControl()
    {
        _tabControl = new TabControl
        {
            Top = _toolStrip.Bottom + 3, Left = 3,
            Width = this.ClientSize.Width - 6,
            Height = this.ClientSize.Height - _toolStrip.Bottom - _panelSummary.Height - _statusStrip.Height - 6,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        _tabAll = new TabPage("全部差异");
        _tabAdded = new TabPage("新增");
        _tabDeleted = new TabPage("删除");
        _tabModified = new TabPage("修改");
        _tabParentAgg = new TabPage("父项聚合");

        _dgvAll = CreateDynamicDiffGridView();
        _dgvAdded = CreateDynamicDiffGridView();
        _dgvDeleted = CreateDynamicDiffGridView();
        _dgvModified = CreateDynamicDiffGridView();
        _dgvParentAgg = CreateDynamicParentAggGridView();

        _tabAll.Controls.Add(_dgvAll);
        _tabAdded.Controls.Add(_dgvAdded);
        _tabDeleted.Controls.Add(_dgvDeleted);
        _tabModified.Controls.Add(_dgvModified);
        _tabParentAgg.Controls.Add(_dgvParentAgg);

        _tabControl.TabPages.Add(_tabAll);
        _tabControl.TabPages.Add(_tabAdded);
        _tabControl.TabPages.Add(_tabDeleted);
        _tabControl.TabPages.Add(_tabModified);
        _tabControl.TabPages.Add(_tabParentAgg);

        this.Controls.Add(_tabControl);
    }

    // ==================== 动态 DataGridView 创建 ====================

    private DataGridView CreateDynamicDiffGridView()
    {
        var dgv = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None
        };

        // 动态生成列：键字段列（用显示名）
        foreach (var kf in _cfg.KeyFields)
        {
            var displayName = GetDisplayName(kf);
            dgv.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = displayName,
                DataPropertyName = displayName,
                HeaderText = displayName
            });
        }

        // 差异类型列
        dgv.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "差异类型", DataPropertyName = "差异类型", HeaderText = "差异类型"
        });
        // 旧值 / 新值列
        var compareDisplay = GetDisplayName(_cfg.CompareField);
        dgv.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = $"旧{compareDisplay}", DataPropertyName = $"旧{compareDisplay}", HeaderText = $"旧{compareDisplay}"
        });
        dgv.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = $"新{compareDisplay}", DataPropertyName = $"新{compareDisplay}", HeaderText = $"新{compareDisplay}"
        });

        // 差异类型列着色
        var diffTypeColIndex = _cfg.KeyFields.Count;
        dgv.CellFormatting += (s, e) =>
        {
            if (e.ColumnIndex == diffTypeColIndex && e.Value is string diffType)
            {
                e.CellStyle!.ForeColor = diffType switch
                {
                    "新增" => Color.Green,
                    "删除" => Color.Red,
                    "修改" => Color.OrangeRed,
                    _ => SystemColors.ControlText
                };
                e.CellStyle.Font = new Font(dgv.Font, FontStyle.Bold);
            }
        };

        return dgv;
    }

    private DataGridView CreateDynamicParentAggGridView()
    {
        var dgv = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None
        };

        // 分组字段列
        dgv.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = GetDisplayName(_cfg.ParentGroupField),
            DataPropertyName = GetDisplayName(_cfg.ParentGroupField),
            HeaderText = GetDisplayName(_cfg.ParentGroupField)
        });
        // 显示名称字段列
        if (_cfg.ParentDisplayField != null)
        {
            dgv.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = GetDisplayName(_cfg.ParentDisplayField),
                DataPropertyName = GetDisplayName(_cfg.ParentDisplayField),
                HeaderText = GetDisplayName(_cfg.ParentDisplayField)
            });
        }
        // 聚合统计列
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "旧视图子项数", DataPropertyName = "旧视图子项数", HeaderText = "旧视图子项数" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "新视图子项数", DataPropertyName = "新视图子项数", HeaderText = "新视图子项数" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "差异数", DataPropertyName = "差异数", HeaderText = "差异数" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "差异状态", DataPropertyName = "差异状态", HeaderText = "差异状态" });

        // 差异状态着色
        var statusColIndex = (_cfg.ParentDisplayField != null ? 2 : 1) + 3; // 在统计列之后
        dgv.CellFormatting += (s, e) =>
        {
            if (e.ColumnIndex == statusColIndex && e.Value is string status)
            {
                e.CellStyle!.ForeColor = status switch
                {
                    "仅新视图存在" => Color.Green,
                    "仅旧视图存在" => Color.Red,
                    _ when status.StartsWith("增加") => Color.DarkGreen,
                    _ when status.StartsWith("减少") => Color.OrangeRed,
                    _ => SystemColors.ControlText
                };
                e.CellStyle.Font = new Font(dgv.Font, FontStyle.Bold);
            }
        };

        return dgv;
    }

    #endregion

    #region Service Initialization

    private void InitializeServices()
    {
        // 1. 加载视图字段映射配置（最先加载）
        _mappingService = new ViewMappingConfigService();
        _mappingService.LoadConfig();
        _mappingService.ValidateAll();

        _cfg = _mappingService.GetRequiredComparison();

        // 2. 初始化动态 Schema 服务
        _schemaService = new SchemaService(_mappingService);

        _dbHelper = new DatabaseHelper(_schemaService, _mappingService);
        _dbHelper.InitializeDatabase();

        _oracleService = new OracleService(_mappingService);
        _sqliteService = new SQLiteService(_dbHelper, _schemaService, _mappingService);
        _diffService = new DiffService(_mappingService);
    }

    private void InitializeSchedule()
    {
        _scheduleService = new ScheduleService(async ct =>
        {
            if (IsHandleCreated)
                this.BeginInvoke(() => { if (_isRunning) return; SetButtonsRunning(true); });
            await ExecuteDetailComparisonAsync(ct);
            if (IsHandleCreated)
                this.BeginInvoke(() => { RefreshStats(); SetButtonsRunning(false); });
        });

        _scheduleService.OnStatusChanged += msg =>
        {
            if (IsHandleCreated) this.BeginInvoke(() => UpdateStatus(msg, Color.DarkCyan));
        };
        _scheduleService.Start();
    }

    #endregion

    #region Event Handlers

    private async void BtnDetailCompare_Click(object sender, EventArgs e)
    {
        if (_isRunning) return;

        UpdateStatus("正在测试Oracle连接...", Color.Blue);
        var (connOk, connError) = await _oracleService.TestConnectionAsync();
        if (!connOk)
        {
            MessageBox.Show($"Oracle连接失败: {connError}", "连接错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateStatus("Oracle连接失败", Color.Red);
            return;
        }
        UpdateStatus("Oracle连接成功，开始执行明细对比...", Color.Green);

        _isRunning = true;
        SetButtonsRunning(true);
        _progressBar.Value = 0;
        _lblProgressPercent.Text = "0%";
        _cts = new CancellationTokenSource();

        try
        {
            await ExecuteDetailComparisonAsync(_cts.Token);
            RefreshStats();
        }
        catch (OperationCanceledException)
        { UpdateStatus("任务已被用户取消", Color.Orange); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "明细对比执行失败");
            UpdateStatus($"执行失败: {ex.Message}", Color.Red);
            MessageBox.Show($"执行错误:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsRunning(false);
            _cts?.Dispose();
            _cts = null;
            UpdateMemoryInfo();
        }
    }

    private async void BtnParentCompare_Click(object sender, EventArgs e)
    {
        if (_isRunning) return;

        var hasOld = _sqliteService.HasSnapshotData("OLD");
        var hasNew = _sqliteService.HasSnapshotData("NEW");
        if (!hasOld || !hasNew)
        {
            MessageBox.Show("请先执行「明细对比」拉取新旧视图数据后再进行父项聚合对比。",
                "数据不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isRunning = true;
        SetButtonsRunning(true);
        _progressBar.Value = 0; _lblProgressPercent.Text = "0%";
        _cts = new CancellationTokenSource();

        try
        {
            await ExecuteParentAggComparisonAsync(_cts.Token);
            RefreshStats();
        }
        catch (OperationCanceledException)
        { UpdateStatus("任务已被用户取消", Color.Orange); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "父项聚合对比失败");
            UpdateStatus($"执行失败: {ex.Message}", Color.Red);
            MessageBox.Show($"执行错误:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsRunning(false);
            _cts?.Dispose();
            _cts = null;
            UpdateMemoryInfo();
        }
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        { UpdateStatus("正在取消...", Color.Orange); _cts.Cancel(); _btnCancel.Enabled = false; }
    }

    private void SetButtonsRunning(bool running)
    {
        _isRunning = running;
        _btnDetailCompare.Enabled = !running;
        _btnParentCompare.Enabled = !running;
        _btnCancel.Enabled = running;
    }

    #endregion

    #region Core Comparison Logic

    private async Task ExecuteDetailComparisonAsync(CancellationToken ct)
    {
        var hasOld = _sqliteService.HasSnapshotData("OLD");
        var hasNew = _sqliteService.HasSnapshotData("NEW");

        if (hasOld) ReportProgress(0, "检测到旧视图数据已存在，跳过拉取");
        if (hasNew) ReportProgress(0, "检测到新视图数据已存在，跳过拉取");

        if (!hasOld) await FetchAndStoreSnapshot("OLD", _oracleService.OldViewName, 0, 45, ct);
        else ReportProgress(45, "旧视图数据已存在（复用）");

        if (!hasNew) await FetchAndStoreSnapshot("NEW", _oracleService.NewViewName, 45, 85, ct);
        else ReportProgress(85, "新视图数据已存在（复用）");

        // 差异对比
        ReportProgress(85, "正在进行明细差异对比...");
        var oldDict = _sqliteService.LoadSnapshotsToDictionary("OLD", ct);
        ReportProgress(90, $"已加载旧视图 {oldDict.Count:N0} 条");
        var newDict = _sqliteService.LoadSnapshotsToDictionary("NEW", ct);
        ReportProgress(93, $"已加载新视图 {newDict.Count:N0} 条");

        var diffs = _diffService.CompareDetails(oldDict, newDict, _cfg, ct);
        ReportProgress(96, $"明细对比完成，共 {diffs.Count:N0} 处差异");

        _sqliteService.ClearDiff();
        _sqliteService.BulkInsertDiffs(diffs, ct);
        ReportProgress(100, $"差异数据已写入本地数据库，共 {diffs.Count:N0} 条");

        if (IsHandleCreated) this.BeginInvoke(() => LoadDetailDiffToGrids());

        var stats = _sqliteService.GetDiffStatistics();
        UpdateStatus($"明细对比完成! {stats}", Color.Green);
    }

    private async Task ExecuteParentAggComparisonAsync(CancellationToken ct)
    {
        ReportProgress(0, "正在进行父项聚合对比...");

        var diffs = await Task.Run(() =>
        {
            var oldAgg = _sqliteService.GetParentAggregation("OLD");
            var newAgg = _sqliteService.GetParentAggregation("NEW");
            return _diffService.CompareParentAggregation(oldAgg, newAgg, _cfg, ct);
        }, ct);

        _parentAggDiffs = diffs;
        ReportProgress(100, $"父项聚合对比完成，{diffs.Count:N0} 个父项存在差异");
        LoadParentAggToGrid();

        if (diffs.Count > 0)
            UpdateStatus($"父项聚合对比完成! 共 {diffs.Count:N0} 个父项存在差异", Color.Green);
        else
            UpdateStatus("父项聚合对比完成! 未发现子项数量差异", Color.Blue);
    }

    private async Task FetchAndStoreSnapshot(string snapshotType, string viewName,
        int progressStart, int progressEnd, CancellationToken ct)
    {
        var startTime = DateTime.Now;
        ReportProgress(progressStart, $"正在统计 {viewName} 视图总行数...");

        var totalCount = await _oracleService.GetTotalCountAsync(viewName, ct);
        var totalPages = (totalCount + _oracleService.PageSize - 1) / _oracleService.PageSize;

        _logger.LogInformation("分页拉取 {Type}/{View}: {TotalCount:N0}行 {TotalPages}页",
            snapshotType, viewName, totalCount, totalPages);
        ReportProgress(progressStart, $"{viewName} 共 {totalCount:N0} 行，{totalPages} 页");

        _sqliteService.ClearSnapshot(snapshotType);

        var batchErrors = 0;
        for (long page = 0; page < totalPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var startRow = page * _oracleService.PageSize + 1;
            var endRow = Math.Min((page + 1) * _oracleService.PageSize, totalCount);

            try
            {
                var data = await _oracleService.GetPageDataAsync(viewName, startRow, endRow, ct);
                foreach (var item in data) item.SnapshotType = snapshotType;
                _sqliteService.BulkInsertSnapshots(data, snapshotType, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                batchErrors++;
                _logger.LogError(ex, "拉取页面失败: {Type} 第{Page}页", snapshotType, page + 1);
                if (batchErrors > 3) throw new InvalidOperationException(
                    $"拉取{snapshotType}视图数据连续失败", ex);
            }

            var progress = progressStart + (int)((page + 1) * (progressEnd - progressStart) / (double)totalPages);
            var stored = _sqliteService.GetSnapshotCount(snapshotType);
            ReportProgress(progress, $"拉取{snapshotType}: {stored:N0}/{totalCount:N0} ({page + 1}/{totalPages}页)");
        }

        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        _logger.LogInformation("拉取完成 {Type}: {Stored:N0}条 耗时{Elapsed:F2}s", snapshotType,
            _sqliteService.GetSnapshotCount(snapshotType), elapsed);
    }

    #endregion

    #region UI Update Helpers

    private void ReportProgress(int percent, string message)
    {
        if (!IsHandleCreated) return;
        if (this.InvokeRequired) { this.BeginInvoke(() => ReportProgress(percent, message)); return; }
        _progressBar.Value = Math.Min(percent, 100);
        _lblProgressPercent.Text = $"{percent}%";
        UpdateStatus(message, Color.Black);
        Application.DoEvents();
    }

    private void UpdateStatus(string message, Color color)
    {
        if (!IsHandleCreated) return;
        if (this.InvokeRequired) { this.BeginInvoke(() => UpdateStatus(message, color)); return; }
        _lblStatus.Text = message;
        _lblStatus.ForeColor = color;
        UpdateMemoryInfo();
    }

    private void UpdateMemoryInfo()
    {
        var mb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        _lblMemory.Text = $"内存: {mb:F0} MB | DB: {_dbHelper.GetDbFileSizeMB():F1} MB";
    }

    // ==================== 动态 DataGridView 绑定 ====================

    private void LoadDetailDiffToGrids()
    {
        var allDiffs = _sqliteService.GetDiffsByType(null, 1, 10000);
        var added = allDiffs.Where(d => d.DiffType == "ADD").ToList();
        var deleted = allDiffs.Where(d => d.DiffType == "DELETE").ToList();
        var modified = allDiffs.Where(d => d.DiffType == "MODIFY").ToList();

        BindDynamicDiffGrid(_dgvAll, allDiffs);
        BindDynamicDiffGrid(_dgvAdded, added);
        BindDynamicDiffGrid(_dgvDeleted, deleted);
        BindDynamicDiffGrid(_dgvModified, modified);

        _tabAll.Text = $"全部差异 ({allDiffs.Count:N0})";
        _tabAdded.Text = $"新增 ({added.Count:N0})";
        _tabDeleted.Text = $"删除 ({deleted.Count:N0})";
        _tabModified.Text = $"修改 ({modified.Count:N0})";
    }

    private void LoadParentAggToGrid()
    {
        BindDynamicParentAggGrid(_dgvParentAgg, _parentAggDiffs);
        _tabParentAgg.Text = $"父项聚合 ({_parentAggDiffs.Count:N0})";
        _tabControl.SelectedTab = _tabParentAgg;
        _dgvParentAgg.Refresh();
    }

    private void BindDynamicDiffGrid(DataGridView dgv, List<DynamicBomRow> diffs)
    {
        var dt = new DataTable();

        // 键字段列（用显示名）
        foreach (var kf in _cfg.KeyFields)
            dt.Columns.Add(GetDisplayName(kf), typeof(string));
        // 差异类型、旧值、新值
        dt.Columns.Add("差异类型", typeof(string));
        var compareDisplay = GetDisplayName(_cfg.CompareField);
        dt.Columns.Add($"旧{compareDisplay}", typeof(string));
        dt.Columns.Add($"新{compareDisplay}", typeof(string));

        foreach (var d in diffs)
        {
            var row = dt.NewRow();
            int colIdx = 0;
            foreach (var kf in _cfg.KeyFields)
                row[colIdx++] = d.GetString(kf) ?? "";
            row[colIdx++] = d.DiffTypeDisplay;
            row[colIdx++] = d.OldValue?.ToString() ?? "";
            row[colIdx++] = d.NewValue?.ToString() ?? "";
            dt.Rows.Add(row);
        }

        dgv.DataSource = dt;
    }

    private void BindDynamicParentAggGrid(DataGridView dgv, List<DynamicBomRow> aggDiffs)
    {
        dgv.DataSource = null;
        dgv.Rows.Clear();
        if (aggDiffs.Count == 0) return;

        var dt = new DataTable();
        dt.Columns.Add(GetDisplayName(_cfg.ParentGroupField), typeof(string));
        if (_cfg.ParentDisplayField != null)
            dt.Columns.Add(GetDisplayName(_cfg.ParentDisplayField), typeof(string));
        dt.Columns.Add("旧视图子项数", typeof(int));
        dt.Columns.Add("新视图子项数", typeof(int));
        dt.Columns.Add("差异数", typeof(int));
        dt.Columns.Add("差异状态", typeof(string));

        foreach (var d in aggDiffs)
        {
            var row = dt.NewRow();
            int ci = 0;
            row[ci++] = d.GetString(_cfg.ParentGroupField) ?? "";
            if (_cfg.ParentDisplayField != null)
                row[ci++] = d.GetString(_cfg.ParentDisplayField) ?? "";
            row[ci++] = d.OldChildCount;
            row[ci++] = d.NewChildCount;
            row[ci++] = d.AggCountDiff;
            row[ci++] = d.AggDiffStatus;
            dt.Rows.Add(row);
        }

        dgv.DataSource = dt;
        dgv.Refresh();
    }

    private void ClearAllGrids()
    {
        foreach (var dgv in new[] { _dgvAll, _dgvAdded, _dgvDeleted, _dgvModified, _dgvParentAgg })
            dgv.DataSource = null;
        _tabAll.Text = "全部差异";
        _tabAdded.Text = "新增";
        _tabDeleted.Text = "删除";
        _tabModified.Text = "修改";
        _tabParentAgg.Text = "父项聚合";
        _parentAggDiffs.Clear();
    }

    private void RefreshStats()
    {
        try
        {
            var stats = _sqliteService.GetDiffStatistics();
            stats.ParentAggDiffCount = _parentAggDiffs.Count;
            _lblStats.Text = stats.ToString();
            _lblSummary.Text = $"统计摘要 — {stats}";
        }
        catch
        {
            _lblStats.Text = "请点击「明细对比」或「父项聚合对比」";
            _lblSummary.Text = "等待执行对比...";
        }
    }

    /// <summary>获取字段显示名（兜底用逻辑名）</summary>
    private string GetDisplayName(string logicalName)
    {
        var field = _cfg.FieldDefinitions.FirstOrDefault(f => f.LogicalName == logicalName);
        return field?.DisplayName ?? logicalName;
    }

    #endregion

    #region Dialogs

    private void OpenConfigForm()
    {
        using var configForm = new ConfigForm();
        if (configForm.ShowDialog(this) == DialogResult.OK)
        {
            _scheduleService.Stop();
            _scheduleService.Start();
            UpdateStatus("配置已更新", Color.Blue);
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "BOM视图数据对比工具 v2.0\n\n" +
            "功能：\n" +
            "• Oracle视图分页抽取（全动态字段映射）\n" +
            "• 本地SQLite动态Schema存储\n" +
            "• 新旧视图全量差异对比\n" +
            "• 父项聚合对比（可配置分组字段）\n" +
            "• DataGridView动态列头\n" +
            "• 支持取消与断点续跑\n" +
            "• 夜间自动批处理执行\n\n" +
            "© 2026",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    #endregion
}
