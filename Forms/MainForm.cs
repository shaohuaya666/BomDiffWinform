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
    private OracleService _oracleService = null!;
    private SQLiteService _sqliteService = null!;
    private DiffService _diffService = null!;
    private DatabaseHelper _dbHelper = null!;
    private ScheduleService _scheduleService = null!;
    private ILogger _logger = null!;

    // ============ State ============
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private List<ParentAggDiffRecord> _parentAggDiffs = new();

    public MainForm()
    {
        _logger = LogService.GetLogger<MainForm>();
        _logger.LogInformation("应用启动");

        InitializeComponent();
        InitializeServices();
        // 调度服务必须在窗体句柄创建后才能启动，延迟到 Load 事件
        this.Load += (s, e) => InitializeSchedule();
        RefreshStats();
    }

    #region UI Initialization

    private void InitializeComponent()
    {
        this.Text = "BOM视图数据对比工具 v1.0";
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
                {
                    e.Cancel = true;
                    return;
                }
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

        // 日志子菜单
        var logMenu = new ToolStripMenuItem("日志(&L)");
        var menuOpenLogDir = new ToolStripMenuItem("打开日志目录", null, (s, e) =>
        {
            LogService.OpenLogDirectory();
            _logger.LogInformation("用户打开日志目录");
        });
        var menuOpenLatestLog = new ToolStripMenuItem("查看最新日志", null, (s, e) =>
        {
            LogService.OpenLatestLogFile();
            _logger.LogInformation("用户查看最新日志");
        });
        var menuLogInfo = new ToolStripMenuItem("日志信息", null, (s, e) =>
        {
            var files = LogService.GetLogFiles();
            var info = $"日志目录: {LogService.LogDirectory}\n" +
                       $"日志级别: {LogService.MinimumLevel}\n" +
                       $"保留天数: {LogService.RetentionDays}天\n" +
                       $"日志总大小: {LogService.GetLogDirectorySizeMB():F1} MB\n" +
                       $"日志文件数: {files.Count}个\n\n" +
                       $"最近日志:\n{string.Join("\n", files.Take(5))}";
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
                _logger.LogWarning("用户执行重置本地数据库");
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

        _btnDetailCompare = new ToolStripButton("📋 明细对比")
        {
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font(_toolStrip.Font, FontStyle.Bold)
        };
        _btnDetailCompare.Click += BtnDetailCompare_Click!;

        _btnParentCompare = new ToolStripButton("📊 父项聚合对比")
        {
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font(_toolStrip.Font, FontStyle.Bold)
        };
        _btnParentCompare.Click += BtnParentCompare_Click!;

        _btnCancel = new ToolStripButton("⏹ 取消")
        {
            Enabled = false,
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = Color.Red
        };
        _btnCancel.Click += BtnCancel_Click!;

        _lblProgressPercent = new ToolStripLabel("0%")
        {
            Alignment = ToolStripItemAlignment.Right
        };

        _progressBar = new ToolStripProgressBar
        {
            Width = 200,
            Alignment = ToolStripItemAlignment.Right,
            Style = ProgressBarStyle.Continuous
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

        _lblStatus = new ToolStripStatusLabel("就绪")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _lblStats = new ToolStripStatusLabel("请点击\u201C开始对比\u201D")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left
        };

        _lblMemory = new ToolStripStatusLabel("内存: --")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left
        };

        _statusStrip.Items.Add(_lblStatus);
        _statusStrip.Items.Add(_lblStats);
        _statusStrip.Items.Add(_lblMemory);

        this.Controls.Add(_statusStrip);
    }

    private void BuildSummaryPanel()
    {
        _panelSummary = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = SystemColors.ControlLight
        };

        _lblSummary = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0),
            Font = new Font("Microsoft YaHei", 10F)
        };
        _panelSummary.Controls.Add(_lblSummary);

        this.Controls.Add(_panelSummary);
    }

    private void BuildTabControl()
    {
        _tabControl = new TabControl
        {
            Top = _toolStrip.Bottom + 3,
            Left = 3,
            Width = this.ClientSize.Width - 6,
            Height = this.ClientSize.Height - _toolStrip.Bottom - _panelSummary.Height - _statusStrip.Height - 6,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        _tabAll = new TabPage("全部差异");
        _tabAdded = new TabPage("新增");
        _tabDeleted = new TabPage("删除");
        _tabModified = new TabPage("修改");
        _tabParentAgg = new TabPage("父项聚合");

        _dgvAll = CreateDiffGridView();
        _dgvAdded = CreateDiffGridView();
        _dgvDeleted = CreateDiffGridView();
        _dgvModified = CreateDiffGridView();
        _dgvParentAgg = CreateParentAggGridView();

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

    private DataGridView CreateDiffGridView()
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

        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "父项图号", DataPropertyName = "父项图号", HeaderText = "父项图号" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "子项图号", DataPropertyName = "子项图号", HeaderText = "子项图号" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "差异类型", DataPropertyName = "差异类型", HeaderText = "差异类型" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "旧数量", DataPropertyName = "旧数量", HeaderText = "旧数量" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "新数量", DataPropertyName = "新数量", HeaderText = "新数量" });

        // 差异类型列着色
        dgv.CellFormatting += (s, e) =>
        {
            if (e.ColumnIndex == 2 && e.Value is string diffType)
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

    private DataGridView CreateParentAggGridView()
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

        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "父项图号", DataPropertyName = "父项图号", HeaderText = "父项图号" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "父项名称", DataPropertyName = "父项名称", HeaderText = "父项名称" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "旧视图子项数", DataPropertyName = "旧视图子项数", HeaderText = "旧视图子项数" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "新视图子项数", DataPropertyName = "新视图子项数", HeaderText = "新视图子项数" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "差异数", DataPropertyName = "差异数", HeaderText = "差异数" });
        dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "差异状态", DataPropertyName = "差异状态", HeaderText = "差异状态" });

        // 差异状态列着色
        dgv.CellFormatting += (s, e) =>
        {
            if (e.ColumnIndex == 5 && e.Value is string status)
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
        _dbHelper = new DatabaseHelper();
        _dbHelper.InitializeDatabase();

        _oracleService = new OracleService();
        _sqliteService = new SQLiteService(_dbHelper);
        _diffService = new DiffService();
    }

    private void InitializeSchedule()
    {
        _scheduleService = new ScheduleService(async ct =>
        {
            // 在UI线程更新状态
            if (IsHandleCreated)
                this.BeginInvoke(() =>
                {
                    if (_isRunning) return;
                    SetButtonsRunning(true);
                });

            await ExecuteDetailComparisonAsync(ct);

            if (IsHandleCreated)
                this.BeginInvoke(() =>
                {
                    RefreshStats();
                    SetButtonsRunning(false);
                });
        });

        _scheduleService.OnStatusChanged += msg =>
        {
            if (IsHandleCreated)
                this.BeginInvoke(() => UpdateStatus(msg, Color.DarkCyan));
        };

        // 应用启动时检查是否启用自动执行
        _scheduleService.Start();
    }

    #endregion

    #region Event Handlers

    /// <summary>明细对比：拉取新旧视图数据 + 行级差异对比</summary>
    private async void BtnDetailCompare_Click(object sender, EventArgs e)
    {
        if (_isRunning) return;

        _logger.LogInformation("用户触发明细对比");

        // 测试Oracle连接
        UpdateStatus("正在测试Oracle连接...", Color.Blue);
        if (!_oracleService.TestConnection(out var error))
        {
            _logger.LogError("Oracle连接失败: {Error}", error);
            MessageBox.Show($"Oracle连接失败: {error}", "连接错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        {
            _logger.LogWarning("任务已被用户取消");
            UpdateStatus("任务已被用户取消", Color.Orange);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "明细对比执行失败");
            UpdateStatus($"执行失败: {ex.Message}", Color.Red);
            MessageBox.Show($"执行过程中发生错误:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsRunning(false);
            _cts?.Dispose();
            _cts = null;
            UpdateMemoryInfo();
            _logger.LogInformation("明细对比流程结束");
        }
    }

    /// <summary>父项聚合对比：基于已有快照数据，按父项图号聚合统计子项数量差异</summary>
    private async void BtnParentCompare_Click(object sender, EventArgs e)
    {
        if (_isRunning) return;

        _logger.LogInformation("用户触发父项聚合对比");

        // 检查快照数据是否存在
        var hasOld = _sqliteService.HasSnapshotData("OLD");
        var hasNew = _sqliteService.HasSnapshotData("NEW");
        if (!hasOld || !hasNew)
        {
            _logger.LogWarning("父项聚合对比数据不足: OLD={HasOld}, NEW={HasNew}", hasOld, hasNew);
            MessageBox.Show("请先执行「明细对比」拉取新旧视图数据后再进行父项聚合对比。",
                "数据不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isRunning = true;
        SetButtonsRunning(true);
        _progressBar.Value = 0;
        _lblProgressPercent.Text = "0%";

        _cts = new CancellationTokenSource();

        try
        {
            await ExecuteParentAggComparisonAsync(_cts.Token);
            RefreshStats();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("父项聚合对比已被用户取消");
            UpdateStatus("任务已被用户取消", Color.Orange);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "父项聚合对比执行失败");
            UpdateStatus($"执行失败: {ex.Message}", Color.Red);
            MessageBox.Show($"执行过程中发生错误:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsRunning(false);
            _cts?.Dispose();
            _cts = null;
            UpdateMemoryInfo();
            _logger.LogInformation("父项聚合对比流程结束");
        }
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            UpdateStatus("正在取消...", Color.Orange);
            _cts.Cancel();
            _btnCancel.Enabled = false;
        }
    }

    /// <summary>统一管理按钮启用/禁用状态</summary>
    private void SetButtonsRunning(bool running)
    {
        _isRunning = running;
        _btnDetailCompare.Enabled = !running;
        _btnParentCompare.Enabled = !running;
        _btnCancel.Enabled = running;
    }

    #endregion

    #region Core Comparison Logic

    /// <summary>明细对比：拉取新旧视图 → 行级差异对比 → 存储并展示</summary>
    private async Task ExecuteDetailComparisonAsync(CancellationToken ct)
    {
        var hasOld = _sqliteService.HasSnapshotData("OLD");
        var hasNew = _sqliteService.HasSnapshotData("NEW");

        _logger.LogInformation("执行明细对比: HasOld={HasOld}, HasNew={HasNew}", hasOld, hasNew);

        if (hasOld) ReportProgress(0, "检测到旧视图数据已存在，跳过拉取");
        if (hasNew) ReportProgress(0, "检测到新视图数据已存在，跳过拉取");

        // 步骤1: 拉取旧视图 (0%~45%)
        if (!hasOld)
        {
            _logger.LogInformation("开始拉取旧视图");
            await FetchAndStoreSnapshot("OLD", _oracleService.OldViewName, 0, 45, ct);
        }
        else
        {
            ReportProgress(45, "旧视图数据已存在（复用）");
        }

        // 步骤2: 拉取新视图 (45%~85%)
        if (!hasNew)
        {
            _logger.LogInformation("开始拉取新视图");
            await FetchAndStoreSnapshot("NEW", _oracleService.NewViewName, 45, 85, ct);
        }
        else
        {
            ReportProgress(85, "新视图数据已存在（复用）");
        }

        // 步骤3: 行级差异对比 (85%~100%)
        ReportProgress(85, "正在进行明细差异对比...");

        var oldDict = _sqliteService.LoadSnapshotsToDictionary("OLD", ct);
        ReportProgress(90, $"已加载旧视图 {oldDict.Count:N0} 条");

        var newDict = _sqliteService.LoadSnapshotsToDictionary("NEW", ct);
        ReportProgress(93, $"已加载新视图 {newDict.Count:N0} 条");

        var diffs = _diffService.Compare(oldDict, newDict, ct);
        ReportProgress(96, $"明细差异对比完成，共发现 {diffs.Count:N0} 处差异");

        _sqliteService.ClearDiff();
        _sqliteService.BulkInsertDiffs(diffs, ct);
        ReportProgress(100, $"差异数据已写入本地数据库，共 {diffs.Count:N0} 条");

        // 加载差异数据到UI
        if (IsHandleCreated)
            this.BeginInvoke(() => LoadDetailDiffToGrids());

        var stats = _sqliteService.GetDiffStatistics();
        UpdateStatus($"明细对比完成! {stats}", Color.Green);

        _logger.LogInformation("明细对比完成: {Stats}", stats);
    }

    /// <summary>父项聚合对比：基于已有快照数据，GROUP BY 父项图号对比子项数量</summary>
    private async Task ExecuteParentAggComparisonAsync(CancellationToken ct)
    {
        ReportProgress(0, "正在进行父项聚合对比...");

        // 全部在后台线程执行，避免阻塞UI
        var diffs = await Task.Run(() =>
        {
            var oldAgg = _sqliteService.GetParentAggregation("OLD");
            var newAgg = _sqliteService.GetParentAggregation("NEW");
            return _diffService.CompareParentAggregation(oldAgg, newAgg, ct);
        }, ct);

        _parentAggDiffs = diffs;

        ReportProgress(100, $"父项聚合对比完成，{diffs.Count:N0} 个父项存在子项数量差异");

        // 直接调用，此时已回到UI线程
        LoadParentAggToGrid();

        if (diffs.Count > 0)
        {
            UpdateStatus($"父项聚合对比完成! 共 {diffs.Count:N0} 个父项存在差异", Color.Green);
        }
        else
        {
            UpdateStatus("父项聚合对比完成! 未发现子项数量差异", Color.Blue);
        }
    }

    private async Task FetchAndStoreSnapshot(string snapshotType, string viewName,
        int progressStart, int progressEnd, CancellationToken ct)
    {
        var startTime = DateTime.Now;

        ReportProgress(progressStart, $"正在统计{viewName}视图总行数...");

        var totalCount = _oracleService.GetTotalCount(viewName, ct);
        var totalPages = (totalCount + _oracleService.PageSize - 1) / _oracleService.PageSize;

        _logger.LogInformation(
            "开始分页拉取 {Type}/{ViewName}: 共 {TotalCount:N0} 行, {TotalPages} 页, 每页 {PageSize} 条",
            snapshotType, viewName, totalCount, totalPages, _oracleService.PageSize);

        ReportProgress(progressStart, $"{viewName} 视图共 {totalCount:N0} 行，{totalPages} 页，每页 {_oracleService.PageSize:N0} 条");

        // 清空旧数据
        _sqliteService.ClearSnapshot(snapshotType);

        var batchErrors = 0;
        for (long page = 0; page < totalPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var startRow = page * _oracleService.PageSize + 1;
            var endRow = Math.Min((page + 1) * _oracleService.PageSize, totalCount);

            try
            {
                var data = _oracleService.GetPageData(viewName, startRow, endRow, ct);

                // 标记快照类型
                foreach (var item in data)
                {
                    item.SnapshotType = snapshotType;
                }

                _sqliteService.BulkInsertSnapshots(data, snapshotType, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                batchErrors++;
                _logger.LogError(ex, "拉取页面失败: {Type} 第{Page}页 ({StartRow}~{EndRow})",
                    snapshotType, page + 1, startRow, endRow);

                if (batchErrors > 3)
                {
                    _logger.LogError("连续失败超过3次，终止拉取: {Type}", snapshotType);
                    throw new InvalidOperationException($"拉取{snapshotType}视图数据连续失败，请检查网络或数据库连接", ex);
                }
            }

            // 更新进度
            var progress = progressStart + (int)((page + 1) * (progressEnd - progressStart) / (double)totalPages);
            var stored = _sqliteService.GetSnapshotCount(snapshotType);
            ReportProgress(progress, $"拉取{snapshotType}: {stored:N0}/{totalCount:N0} ({page + 1}/{totalPages}页)");
        }

        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        _logger.LogInformation(
            "分页拉取完成 {Type}: {Stored:N0} 条, 耗时 {Elapsed:F2}s, 错误批次: {Errors}",
            snapshotType, _sqliteService.GetSnapshotCount(snapshotType), elapsed, batchErrors);
    }

    #endregion

    #region UI Update Helpers

    private void ReportProgress(int percent, string message)
    {
        if (!IsHandleCreated) return;
        if (this.InvokeRequired)
        {
            this.BeginInvoke(() => ReportProgress(percent, message));
            return;
        }

        _progressBar.Value = Math.Min(percent, 100);
        _lblProgressPercent.Text = $"{percent}%";
        UpdateStatus(message, Color.Black);
        Application.DoEvents();
    }

    private void UpdateStatus(string message, Color color)
    {
        if (!IsHandleCreated) return;
        if (this.InvokeRequired)
        {
            this.BeginInvoke(() => UpdateStatus(message, color));
            return;
        }

        _lblStatus.Text = message;
        _lblStatus.ForeColor = color;
        UpdateMemoryInfo();
    }

    private void UpdateMemoryInfo()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var mb = process.WorkingSet64 / (1024.0 * 1024.0);
        _lblMemory.Text = $"内存: {mb:F0} MB | DB: {_dbHelper.GetDbFileSizeMB():F1} MB";
    }

    private void LoadDetailDiffToGrids()
    {
        var allDiffs = _sqliteService.GetDiffsByType(null, 1, 10000);
        var added = allDiffs.Where(d => d.DiffType == "ADD").ToList();
        var deleted = allDiffs.Where(d => d.DiffType == "DELETE").ToList();
        var modified = allDiffs.Where(d => d.DiffType == "MODIFY").ToList();

        BindDiffGridView(_dgvAll, allDiffs);
        BindDiffGridView(_dgvAdded, added);
        BindDiffGridView(_dgvDeleted, deleted);
        BindDiffGridView(_dgvModified, modified);

        _tabAll.Text = $"全部差异 ({allDiffs.Count:N0})";
        _tabAdded.Text = $"新增 ({added.Count:N0})";
        _tabDeleted.Text = $"删除 ({deleted.Count:N0})";
        _tabModified.Text = $"修改 ({modified.Count:N0})";
    }

    private void LoadParentAggToGrid()
    {
        try
        {
            BindParentAggGridView(_dgvParentAgg, _parentAggDiffs);
            _tabParentAgg.Text = $"父项聚合 ({_parentAggDiffs.Count:N0})";

            // 自动切换到父项聚合标签页
            _tabControl.SelectedTab = _tabParentAgg;

            // 强制刷新 DataGridView 确保数据展示
            _dgvParentAgg.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"父项聚合数据显示失败: {ex.Message}\n\n{ex.StackTrace}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void BindDiffGridView(DataGridView dgv, List<BomDiffRecord> diffs)
    {
        var dt = new DataTable();
        dt.Columns.Add("父项图号");
        dt.Columns.Add("子项图号");
        dt.Columns.Add("差异类型");
        dt.Columns.Add("旧数量");
        dt.Columns.Add("新数量");

        foreach (var d in diffs)
        {
            dt.Rows.Add(d.ParentPartNo, d.ChildPartNo, d.DiffTypeDisplay, d.OldQty, d.NewQty);
        }

        dgv.DataSource = dt;
    }

    private static void BindParentAggGridView(DataGridView dgv, List<ParentAggDiffRecord> aggDiffs)
    {
        // 先清空旧的数据源，强制 DataGridView 重新绑定
        dgv.DataSource = null;
        dgv.Rows.Clear();

        if (aggDiffs.Count == 0)
        {
            return;
        }

        var dt = new DataTable();
        dt.Columns.Add("父项图号", typeof(string));
        dt.Columns.Add("父项名称", typeof(string));
        dt.Columns.Add("旧视图子项数", typeof(int));
        dt.Columns.Add("新视图子项数", typeof(int));
        dt.Columns.Add("差异数", typeof(int));
        dt.Columns.Add("差异状态", typeof(string));

        foreach (var d in aggDiffs)
        {
            dt.Rows.Add(d.ParentPartNo, d.ParentPartName, d.OldChildCount, d.NewChildCount, d.CountDiff, d.DiffStatus);
        }

        dgv.DataSource = dt;
        dgv.Refresh();
    }

    private void ClearAllGrids()
    {
        _dgvAll.DataSource = null;
        _dgvAdded.DataSource = null;
        _dgvDeleted.DataSource = null;
        _dgvModified.DataSource = null;
        _dgvParentAgg.DataSource = null;

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
            _lblSummary.Text = $"📊 统计摘要 — {stats}";
        }
        catch
        {
            _lblStats.Text = "请点击「明细对比」或「父项聚合对比」";
            _lblSummary.Text = "📊 等待执行对比...";
        }
    }

    #endregion

    #region Dialogs

    private void OpenConfigForm()
    {
        using var configForm = new ConfigForm();
        if (configForm.ShowDialog(this) == DialogResult.OK)
        {
            // 配置已保存，刷新计划表
            _scheduleService.Stop();
            _scheduleService.Start();
            UpdateStatus("配置已更新", Color.Blue);
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "BOM视图数据对比工具 v1.0\n\n" +
            "功能：\n" +
            "• Oracle视图分页抽取\n" +
            "• 本地SQLite持久化存储\n" +
            "• 新旧视图全量对比\n" +
            "• 差异数据可视化\n" +
            "• 支持取消与断点续跑\n" +
            "• 夜间自动批处理执行\n\n" +
            "© 2026",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    #endregion
}
