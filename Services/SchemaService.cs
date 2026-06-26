using System.Data.SQLite;
using BomDiffWinform.Models;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// 全动态 Schema 服务 —— 所有 SQLite DDL/DML 由 ComparisonConfig 驱动生成。
/// 
/// v2 核心改进：
/// - 表结构完全由配置文件中的 FieldDefinitions 决定（不再依赖固定模型）
/// - 支持不同对比配置拥有不同字段集的快照表和差异表
/// - V1→V2 自动迁移（中文列名 → 动态逻辑列名）
/// - Schema 完整性校验
/// </summary>
public class SchemaService
{
    private readonly ViewMappingConfigService _configService;
    private readonly ILogger _logger;

    // 缓存的动态 Schema
    private volatile List<ColumnDef>? _snapshotCols;
    private volatile List<ColumnDef>? _diffCols;
    private volatile List<IndexDef>? _snapshotIndexes;
    private volatile List<IndexDef>? _diffIndexes;
    private volatile string _configHash = string.Empty;

    public SchemaService(ViewMappingConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = LogService.GetLogger<SchemaService>();
    }

    // ==================== 动态 Schema 生成 ====================

    /// <summary>刷新并返回快照表列定义</summary>
    public List<ColumnDef> GetSnapshotColumns()
    {
        return _snapshotCols ??= BuildSnapshotColumns();
    }

    /// <summary>刷新并返回差异表列定义</summary>
    public List<ColumnDef> GetDiffColumns()
    {
        return _diffCols ??= BuildDiffColumns();
    }

    /// <summary>返回快照表索引定义</summary>
    public List<IndexDef> GetSnapshotIndexes()
    {
        return _snapshotIndexes ??= BuildSnapshotIndexes();
    }

    /// <summary>返回差异表索引定义</summary>
    public List<IndexDef> GetDiffIndexes()
    {
        return _diffIndexes ??= BuildDiffIndexes();
    }

    /// <summary>重新生成所有 Schema（配置热更新后调用）</summary>
    public void RefreshSchemas()
    {
        _snapshotCols = null;
        _diffCols = null;
        _snapshotIndexes = null;
        _diffIndexes = null;
        _configHash = string.Empty;
        _logger.LogInformation("动态 Schema 已刷新");
    }

    // ==================== DDL 生成 ====================

    public void InitializeDatabase(SQLiteConnection conn, ComparisonConfig cfg)
    {
        _logger.LogInformation("========== 动态 Schema 初始化 (对比: {Id}) ==========", cfg.Id);

        RefreshSchemas();
        InitializeTable(conn, "BOM_SNAPSHOT", GetSnapshotColumns(), GetSnapshotIndexes());
        InitializeTable(conn, "BOM_DIFF", GetDiffColumns(), GetDiffIndexes());

        _logger.LogInformation("========== 动态 Schema 初始化完成 ==========");
    }

    public string GenerateCreateTableDdl(string tableName, List<ColumnDef> columns)
    {
        var colDefs = new List<string>(columns.Count);
        foreach (var col in columns)
        {
            var parts = new List<string> { col.ColumnName, col.DataType };
            if (col.IsPrimaryKey) parts.Add("PRIMARY KEY");
            if (col.IsAutoIncrement) parts.Add("AUTOINCREMENT");
            if (col.Required) parts.Add("NOT NULL");
            colDefs.Add(string.Join(" ", parts));
        }
        return $"CREATE TABLE IF NOT EXISTS {tableName} (\n    {string.Join(",\n    ", colDefs)}\n);";
    }

    public string GenerateCreateIndexDdl(string tableName, IndexDef index)
    {
        var cols = string.Join(", ", index.ColumnNames);
        return $"CREATE INDEX IF NOT EXISTS {index.Name} ON {tableName}({cols});";
    }

    // ==================== DML 生成 ====================

    /// <summary>生成 INSERT 列名列表（跳过自增ID）</summary>
    public string BuildInsertColumns(List<ColumnDef> columns)
    {
        var businessCols = columns.Where(c => !c.IsAutoIncrement).Select(c => c.ColumnName);
        return string.Join(", ", businessCols);
    }

    /// <summary>生成 INSERT 参数列表：@Col1, @Col2, ...</summary>
    public string BuildInsertParameters(List<ColumnDef> columns)
    {
        var businessCols = columns.Where(c => !c.IsAutoIncrement).Select(c => $"@{c.ColumnName}");
        return string.Join(", ", businessCols);
    }

    /// <summary>生成完整 INSERT 语句</summary>
    public string BuildInsertSql(string tableName, List<ColumnDef> columns)
    {
        var cols = BuildInsertColumns(columns);
        var pars = BuildInsertParameters(columns);
        return $"INSERT INTO {tableName} ({cols}) VALUES ({pars})";
    }

    /// <summary>生成 SELECT 列名列表</summary>
    public string BuildSelectColumns(List<ColumnDef> columns)
    {
        return string.Join(", ", columns.Select(c => c.ColumnName));
    }

    // ==================== 数据迁移 ====================

    public bool NeedsMigration(SQLiteConnection conn, string tableName, ComparisonConfig cfg)
    {
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            if ((long)checkCmd.ExecuteScalar()! == 0) return false; // 表不存在，无需迁移

            // 收集当前表所有列信息：name → (notnull, type)
            var actualCols = new Dictionary<string, (bool notnull, string type)>(StringComparer.OrdinalIgnoreCase);
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = $"PRAGMA table_info('{tableName}')";
                using var reader = pragmaCmd.ExecuteReader();
                while (reader.Read())
                {
                    // PRAGMA table_info: cid=0, name=1, type=2, notnull=3, dflt_value=4, pk=5
                    actualCols[reader.GetString(1)] = (reader.GetInt32(3) == 1, reader.GetString(2));
                }
            }

            // 对比所有期望列：列缺失 或 NOT NULL 约束不一致 都需要迁移
            var expectedCols = tableName == "BOM_SNAPSHOT"
                ? GetSnapshotColumns()
                : GetDiffColumns();
            foreach (var col in expectedCols)
            {
                if (!actualCols.TryGetValue(col.ColumnName, out var actual))
                {
                    _logger.LogInformation("表 {Table} 需要迁移（缺列 '{Col}'）", tableName, col.ColumnName);
                    return true;
                }

                // NOT NULL 约束不匹配：旧表有 NOT NULL 但新 Schema 不需要 → 会导致插入报错
                if (actual.notnull != col.Required)
                {
                    _logger.LogInformation("表 {Table} 需要迁移（列 '{Col}' NOT NULL 约束不匹配: 旧={Old}, 新={New}）",
                        tableName, col.ColumnName, actual.notnull, col.Required);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查表 {Table} 迁移状态异常，视为需要迁移", tableName);
            return true;
        }
    }

    /// <summary>生成迁移 INSERT ... SELECT 语句</summary>
    public string BuildMigrationSql(string tableName, string tempTableName, List<ColumnDef> columns)
    {
        var newCols = new List<string>();
        var oldCols = new List<string>();
        foreach (var col in columns.Where(c => !c.IsAutoIncrement && !string.IsNullOrWhiteSpace(c.OldColumnName)))
        {
            newCols.Add(col.ColumnName);
            oldCols.Add(col.OldColumnName!);
        }
        if (newCols.Count == 0) return string.Empty;
        return $"INSERT INTO {tempTableName} ({string.Join(", ", newCols)}) SELECT {string.Join(", ", oldCols)} FROM {tableName}";
    }

    public void MigrateTable(SQLiteConnection conn, string tableName, List<ColumnDef> columns, List<IndexDef> indexes, ComparisonConfig cfg)
    {
        var tempTable = $"{tableName}_V2_MIG";
        using var transaction = conn.BeginTransaction();
        try
        {
            // 删除残留
            using (var dropCmd = conn.CreateCommand())
            { dropCmd.CommandText = $"DROP TABLE IF EXISTS {tempTable}"; dropCmd.ExecuteNonQuery(); }

            // 创建新表
            var createDdl = GenerateCreateTableDdl(tempTable, columns);
            using (var createCmd = conn.CreateCommand())
            { createCmd.CommandText = createDdl; createCmd.ExecuteNonQuery(); }

            // 复制数据
            var migrationSql = BuildMigrationSql(tableName, tempTable, columns);
            if (!string.IsNullOrEmpty(migrationSql))
            {
                using var copyCmd = conn.CreateCommand();
                copyCmd.CommandText = migrationSql;
                var rows = copyCmd.ExecuteNonQuery();
                _logger.LogInformation("迁移 {Old}→{New}: {Rows} 条", tableName, tempTable, rows);
            }

            // 删旧 → 重命名
            using (var dropCmd = conn.CreateCommand())
            { dropCmd.CommandText = $"DROP TABLE {tableName}"; dropCmd.ExecuteNonQuery(); }
            using (var renameCmd = conn.CreateCommand())
            { renameCmd.CommandText = $"ALTER TABLE {tempTable} RENAME TO {tableName}"; renameCmd.ExecuteNonQuery(); }

            transaction.Commit();
            _logger.LogInformation("表 {Table} 迁移提交成功", tableName);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "表 {Table} 迁移失败，已回滚", tableName);
            throw;
        }

        // 重建索引
        using var idxTransaction = conn.BeginTransaction();
        try
        {
            foreach (var idx in indexes)
            {
                using var idxCmd = conn.CreateCommand();
                idxCmd.CommandText = GenerateCreateIndexDdl(tableName, idx);
                idxCmd.ExecuteNonQuery();
            }
            idxTransaction.Commit();
        }
        catch (Exception ex)
        {
            idxTransaction.Rollback();
            _logger.LogError(ex, "表 {Table} 索引重建失败", tableName);
            throw;
        }
    }

    // ==================== 校验 ====================

    public (bool Valid, List<string> Issues) ValidateTable(SQLiteConnection conn, string tableName, List<ColumnDef> expectedCols)
    {
        var issues = new List<string>();
        try
        {
            var actualCols = new HashSet<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{tableName}')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) actualCols.Add(reader.GetString(1));

            foreach (var expected in expectedCols)
                if (!actualCols.Contains(expected.ColumnName))
                    issues.Add($"缺少列: {tableName}.{expected.ColumnName}");
        }
        catch (Exception ex) { issues.Add($"校验异常: {ex.Message}"); }
        return (issues.Count == 0, issues);
    }

    // ==================== 私有：构建列/索引 ====================

    private List<ColumnDef> BuildSnapshotColumns()
    {
        var cfg = _configService.GetRequiredComparison();
        var cols = new List<ColumnDef>
        {
            new() { ColumnName = "Id", DataType = "INTEGER", IsPrimaryKey = true, IsAutoIncrement = true, OldColumnName = "ID" },
            new() { ColumnName = "SnapshotType", DataType = "TEXT", Required = true, OldColumnName = "SNAPSHOT_TYPE" },
        };

        // 业务字段全部可为 NULL —— Oracle 视图无法保证任何字段非空
        foreach (var field in cfg.FieldDefinitions)
        {
            cols.Add(new ColumnDef
            {
                ColumnName = field.LogicalName,
                DataType = field.DataType,
                Required = false,
                OldColumnName = field.DisplayName // V1中文列名迁移
            });
        }

        return cols;
    }

    private List<ColumnDef> BuildDiffColumns()
    {
        var cfg = _configService.GetRequiredComparison();
        var cols = new List<ColumnDef>
        {
            new() { ColumnName = "Id", DataType = "INTEGER", IsPrimaryKey = true, IsAutoIncrement = true },
        };

        // 键字段列：可为 NULL（Oracle 源数据不保证非空）
        foreach (var kf in cfg.KeyFields)
        {
            var field = cfg.FieldDefinitions.FirstOrDefault(f => f.LogicalName == kf);
            cols.Add(new ColumnDef
            {
                ColumnName = kf,
                DataType = field?.DataType ?? "TEXT",
                Required = false
            });
        }

        // 差异元数据列（仅 DiffType 必填，由代码保证非空）
        cols.Add(new ColumnDef { ColumnName = "DiffType", DataType = "TEXT", Required = true });
        var cfDef = cfg.FieldDefinitions.FirstOrDefault(f => f.LogicalName == cfg.CompareField);
        var compareDataType = cfDef?.DataType ?? "TEXT";
        cols.Add(new ColumnDef { ColumnName = "OldValue", DataType = compareDataType });
        cols.Add(new ColumnDef { ColumnName = "NewValue", DataType = compareDataType });

        return cols;
    }

    private List<IndexDef> BuildSnapshotIndexes()
    {
        var cfg = _configService.GetRequiredComparison();
        return new()
        {
            new() { Name = "IDX_SNAPSHOT_TYPE", ColumnNames = new() { "SnapshotType" } },
            new() { Name = "IDX_SNAPSHOT_KEY",  ColumnNames = new(cfg.KeyFields) },
        };
    }

    private List<IndexDef> BuildDiffIndexes()
    {
        return new()
        {
            new() { Name = "IDX_DIFF_TYPE", ColumnNames = new() { "DiffType" } },
        };
    }

    // ==================== 内部初始化 ====================

    private void InitializeTable(SQLiteConnection conn, string tableName,
        List<ColumnDef> columns, List<IndexDef> indexes)
    {
        var cfg = _configService.GetRequiredComparison();

        if (NeedsMigration(conn, tableName, cfg))
        {
            _logger.LogInformation("表 {Table} 需要迁移，开始...", tableName);
            MigrateTable(conn, tableName, columns, indexes, cfg);
            return;
        }

        var ddl = GenerateCreateTableDdl(tableName, columns);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
        _logger.LogDebug("表 {Table} 就绪（{ColCount}列）", tableName, columns.Count);

        foreach (var idx in indexes)
        {
            cmd.CommandText = GenerateCreateIndexDdl(tableName, idx);
            cmd.ExecuteNonQuery();
        }
    }
}
