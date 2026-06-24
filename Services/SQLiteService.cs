using System.Data.SQLite;
using BomDiffWinform.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// SQLite 本地存储服务 (v2 全动态)
/// 
/// v2 核心改进：
/// - 所有 SQL 由 SchemaService + ComparisonConfig 动态生成
/// - 操作对象统一为 DynamicBomRow
/// - 快照明细、差异记录、父项聚合全部动态字段
/// </summary>
public class SQLiteService
{
    private readonly DatabaseHelper _dbHelper;
    private readonly SchemaService _schemaService;
    private readonly ViewMappingConfigService _configService;
    private readonly ComparisonConfig _cfg;
    private readonly ILogger _logger;

    // 缓存的动态 SQL 模板
    private readonly string _insertSnapshotSql;
    private readonly string _selectSnapshotCols;
    private readonly string _insertDiffSql;
    private readonly string _selectDiffCols;

    public SQLiteService(DatabaseHelper dbHelper, SchemaService schemaService,
        ViewMappingConfigService configService)
    {
        _dbHelper = dbHelper ?? throw new ArgumentNullException(nameof(dbHelper));
        _schemaService = schemaService ?? throw new ArgumentNullException(nameof(schemaService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _cfg = _configService.GetRequiredComparison();
        _logger = LogService.GetLogger<SQLiteService>();

        var snapshotCols = _schemaService.GetSnapshotColumns();
        var diffCols = _schemaService.GetDiffColumns();

        _insertSnapshotSql = _schemaService.BuildInsertSql("BOM_SNAPSHOT", snapshotCols);
        _selectSnapshotCols = _schemaService.BuildSelectColumns(snapshotCols);
        _insertDiffSql = _schemaService.BuildInsertSql("BOM_DIFF", diffCols);
        _selectDiffCols = _schemaService.BuildSelectColumns(diffCols);

        _logger.LogDebug("SQLiteService SQL 模板预生成完成 (快照={SnapCols}列, 差异={DiffCols}列)",
            snapshotCols.Count, diffCols.Count);
    }

    // ==================== 快照操作 ====================

    /// <summary>清空指定类型快照</summary>
    public void ClearSnapshot(string snapshotType)
    {
        _logger.LogInformation("清空快照: {Type}", snapshotType);
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var deleted = conn.Execute("DELETE FROM BOM_SNAPSHOT WHERE SnapshotType = @Type", new { Type = snapshotType });
        _logger.LogInformation("已清空 {Type} 快照 {Count} 条", snapshotType, deleted);
    }

    /// <summary>清空差异数据</summary>
    public void ClearDiff()
    {
        _logger.LogInformation("清空差异数据");
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var deleted = conn.Execute("DELETE FROM BOM_DIFF");
        _logger.LogInformation("已清空差异 {Count} 条", deleted);
    }

    /// <summary>批量插入快照明细（事务）</summary>
    public void BulkInsertSnapshots(List<DynamicBomRow> snapshots, string snapshotType, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;
        _logger.LogDebug("批量插入快照 {Type}: {Count} 条", snapshotType, snapshots.Count);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            var inserted = 0;
            var nullKeyWarned = false;
            foreach (var row in snapshots)
            {
                ct.ThrowIfCancellationRequested();

                // 构建 Dapper 参数对象
                var parameters = new DynamicParameters();
                parameters.Add("SnapshotType", snapshotType);
                foreach (var field in _cfg.FieldDefinitions)
                {
                    var val = row.GetValue(field.LogicalName);
                    parameters.Add(field.LogicalName, val);

                    // 键字段为 NULL 时记录警告（仅首次）
                    if (!nullKeyWarned && val == null && _cfg.KeyFields.Contains(field.LogicalName))
                    {
                        nullKeyWarned = true;
                        _logger.LogWarning("快照 {Type} 中存在 NULL 键字段 '{Field}'，该行复合键将使用空字符串参与匹配",
                            snapshotType, field.LogicalName);
                    }
                }

                conn.Execute(_insertSnapshotSql, parameters, transaction);
                inserted++;
            }
            transaction.Commit();
            _logger.LogDebug("快照批量完成: {Count} 条", inserted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "快照批量插入回滚: {Type}", snapshotType);
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>检查快照是否已有数据（断点续跑）</summary>
    public bool HasSnapshotData(string snapshotType)
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var count = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SnapshotType = @Type", new { Type = snapshotType });
        return count > 0;
    }

    /// <summary>获取快照总行数</summary>
    public long GetSnapshotCount(string snapshotType)
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SnapshotType = @Type", new { Type = snapshotType });
    }

    /// <summary>加载全量快照到 Dictionary（用 KeyFields 构建复合键）</summary>
    public Dictionary<string, DynamicBomRow> LoadSnapshotsToDictionary(string snapshotType, CancellationToken ct)
    {
        _logger.LogInformation("加载快照到内存: {Type}", snapshotType);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var sql = $"SELECT {_selectSnapshotCols} FROM BOM_SNAPSHOT WHERE SnapshotType = @Type";
        var dynamicRows = conn.Query(sql, new { Type = snapshotType });

        var dict = new Dictionary<string, DynamicBomRow>();
        var duplicateCount = 0;
        foreach (var dRow in dynamicRows)
        {
            ct.ThrowIfCancellationRequested();
            var row = new DynamicBomRow();
            var dDict = (IDictionary<string, object>)dRow;
            row.Id = Convert.ToInt64(dDict["Id"]);
            row.SnapshotType = dDict["SnapshotType"]?.ToString() ?? snapshotType;
            foreach (var field in _cfg.FieldDefinitions)
            {
                if (dDict.TryGetValue(field.LogicalName, out var val))
                    row.SetValue(field.LogicalName, val);
            }

            var key = row.BuildKey(_cfg.KeyFields);
            if (!dict.ContainsKey(key))
                dict[key] = row;
            else
                duplicateCount++;
        }

        if (duplicateCount > 0)
            _logger.LogWarning("快照 {Type} 存在 {Count} 条重复键", snapshotType, duplicateCount);

        _logger.LogInformation("快照 {Type} 加载完成: {Count} 条", snapshotType, dict.Count);
        return dict;
    }

    // ==================== 差异操作 ====================

    /// <summary>批量插入差异记录（事务）</summary>
    public void BulkInsertDiffs(List<DynamicBomRow> diffs, CancellationToken ct)
    {
        if (diffs.Count == 0) return;
        _logger.LogInformation("批量插入差异: {Count} 条", diffs.Count);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            foreach (var row in diffs)
            {
                ct.ThrowIfCancellationRequested();
                var parameters = new DynamicParameters();
                foreach (var kf in _cfg.KeyFields)
                    parameters.Add(kf, row.GetValue(kf));
                parameters.Add("DiffType", row.DiffType);
                parameters.Add("OldValue", row.OldValue);
                parameters.Add("NewValue", row.NewValue);

                conn.Execute(_insertDiffSql, parameters, transaction);
            }
            transaction.Commit();
            _logger.LogInformation("差异记录写入完成: {Count} 条", diffs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "差异批量插入回滚");
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>分页查询差异记录</summary>
    public List<DynamicBomRow> GetDiffsByType(string? diffType = null, int page = 1, int pageSize = 1000)
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        var whereClause = string.IsNullOrEmpty(diffType) ? "" : "WHERE DiffType = @Type";
        var offset = (page - 1) * pageSize;
        var sql = $@"
            SELECT {_selectDiffCols}
            FROM BOM_DIFF
            {whereClause}
            ORDER BY DiffType, {string.Join(", ", _cfg.KeyFields)}
            LIMIT @PageSize OFFSET @Offset
        ";

        var dynamicRows = conn.Query(sql, new { Type = diffType, PageSize = pageSize, Offset = offset });
        var result = new List<DynamicBomRow>();
        foreach (var dRow in dynamicRows)
        {
            var dDict = (IDictionary<string, object>)dRow;
            var row = new DynamicBomRow
            {
                Id = Convert.ToInt64(dDict["Id"]),
                DiffType = dDict["DiffType"]?.ToString() ?? "",
                OldValue = dDict.TryGetValue("OldValue", out var ov) ? ov : null,
                NewValue = dDict.TryGetValue("NewValue", out var nv) ? nv : null
            };
            foreach (var kf in _cfg.KeyFields)
                if (dDict.TryGetValue(kf, out var val))
                    row.SetValue(kf, val);
            result.Add(row);
        }
        return result;
    }

    // ==================== 父项聚合 ====================

    /// <summary>
    /// 获取指定快照类型的父项聚合统计（分组字段 + 显示字段由配置驱动）
    /// </summary>
    public Dictionary<string, (string DisplayValue, int ChildCount)> GetParentAggregation(string snapshotType)
    {
        _logger.LogInformation("获取父项聚合: {Type}, 分组={Group}", snapshotType, _cfg.ParentGroupField);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        var p = _cfg.ParentGroupField;
        var displaySelect = _cfg.ParentDisplayField != null
            ? $"MAX({_cfg.ParentDisplayField})"
            : $"{p}";  // 无显示字段就用分组字段值

        var sql = $@"
            SELECT {p} AS GroupKey,
                   {displaySelect} AS DisplayValue,
                   COUNT(*) AS ChildCount
            FROM BOM_SNAPSHOT
            WHERE SnapshotType = @Type
            GROUP BY {p}
            ORDER BY {p}
        ";

        var rows = conn.Query(sql, new { Type = snapshotType });
        var dict = new Dictionary<string, (string, int)>();
        foreach (var r in rows)
        {
            var d = (IDictionary<string, object>)r;
            var key = d["GroupKey"]?.ToString() ?? "";
            if (!dict.ContainsKey(key))
                dict[key] = (d["DisplayValue"]?.ToString() ?? key, Convert.ToInt32(d["ChildCount"]));
        }

        _logger.LogInformation("父项聚合 {Type} 完成: {Count} 个父项", snapshotType, dict.Count);
        return dict;
    }

    // ==================== 统计 ====================

    /// <summary>获取差异统计摘要</summary>
    public DiffStatistics GetDiffStatistics()
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        return new DiffStatistics
        {
            TotalOld = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SnapshotType = 'OLD'"),
            TotalNew = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SnapshotType = 'NEW'"),
            AddedCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_DIFF WHERE DiffType = 'ADD'"),
            DeletedCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_DIFF WHERE DiffType = 'DELETE'"),
            ModifiedCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_DIFF WHERE DiffType = 'MODIFY'"),
        };
    }
}

// ==================== 差异统计模型 ====================

public class DiffStatistics
{
    public long TotalOld { get; set; }
    public long TotalNew { get; set; }
    public long AddedCount { get; set; }
    public long DeletedCount { get; set; }
    public long ModifiedCount { get; set; }
    public long TotalDiff => AddedCount + DeletedCount + ModifiedCount;
    public long ParentAggDiffCount { get; set; }

    public override string ToString()
    {
        var p = ParentAggDiffCount > 0 ? $" | 父项差异: {ParentAggDiffCount:N0} 个" : "";
        return $"旧: {TotalOld:N0} | 新: {TotalNew:N0} | 新增: {AddedCount:N0} | 删除: {DeletedCount:N0} | 修改: {ModifiedCount:N0} | 差异合计: {TotalDiff:N0}{p}";
    }
}
