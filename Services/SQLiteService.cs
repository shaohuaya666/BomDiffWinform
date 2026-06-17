using System.Data.SQLite;
using BomDiffWinform.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// SQLite本地存储服务
/// </summary>
public class SQLiteService
{
    private readonly DatabaseHelper _dbHelper;
    private readonly ILogger _logger;

    public SQLiteService(DatabaseHelper dbHelper)
    {
        _dbHelper = dbHelper;
        _logger = LogService.GetLogger<SQLiteService>();
    }

    /// <summary>
    /// 清空指定类型的快照数据
    /// </summary>
    public void ClearSnapshot(string snapshotType)
    {
        _logger.LogInformation("清空快照数据: {Type}", snapshotType);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var deleted = conn.Execute("DELETE FROM BOM_SNAPSHOT WHERE SNAPSHOT_TYPE = @Type", new { Type = snapshotType });

        _logger.LogInformation("已清空 {Type} 快照数据 {Count} 条", snapshotType, deleted);
    }

    /// <summary>
    /// 清空所有差异数据
    /// </summary>
    public void ClearDiff()
    {
        _logger.LogInformation("清空差异数据");

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var deleted = conn.Execute("DELETE FROM BOM_DIFF");

        _logger.LogInformation("已清空差异数据 {Count} 条", deleted);
    }

    /// <summary>
    /// 批量插入快照数据（使用事务提升性能）
    /// </summary>
    public void BulkInsertSnapshots(List<BomSnapshot> snapshots, string snapshotType, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;

        _logger.LogDebug("批量插入快照 {Type}: {Count} 条", snapshotType, snapshots.Count);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        using var transaction = conn.BeginTransaction();
        try
        {
            var sql = @"
                INSERT INTO BOM_SNAPSHOT (SNAPSHOT_TYPE, 父项图号, 父项名称, 父项源, 子项图号, 子项名称, 子项源, 数量, SOURCE)
                VALUES (@SnapshotType, @ParentPartNo, @ParentPartName, @ParentSource, @ChildPartNo, @ChildPartName, @ChildSource, @Quantity, @Source)
            ";

            var inserted = 0;
            foreach (var item in snapshots)
            {
                ct.ThrowIfCancellationRequested();

                conn.Execute(sql, new
                {
                    item.SnapshotType,
                    item.ParentPartNo,
                    item.ParentPartName,
                    item.ParentSource,
                    item.ChildPartNo,
                    item.ChildPartName,
                    item.ChildSource,
                    item.Quantity,
                    Source = snapshotType
                }, transaction);
                inserted++;
            }

            transaction.Commit();
            _logger.LogDebug("快照批量插入完成: {Count} 条已提交", inserted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "快照批量插入事务回滚: {Type}", snapshotType);
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 检查指定类型的快照是否已有数据（用于断点续跑）
    /// </summary>
    public bool HasSnapshotData(string snapshotType)
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        var count = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SNAPSHOT_TYPE = @Type",
            new { Type = snapshotType });
        var hasData = count > 0;
        _logger.LogDebug("检查快照 {Type} 数据: {HasData}, 行数={Count}", snapshotType, hasData, count);
        return hasData;
    }

    /// <summary>
    /// 获取指定类型快照的总行数
    /// </summary>
    public long GetSnapshotCount(string snapshotType)
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SNAPSHOT_TYPE = @Type",
            new { Type = snapshotType });
    }

    /// <summary>
    /// 加载指定类型的全量快照数据到Dictionary（用于内存对比）
    /// </summary>
    public Dictionary<string, BomSnapshot> LoadSnapshotsToDictionary(string snapshotType, CancellationToken ct)
    {
        _logger.LogInformation("开始加载快照到内存: {Type}", snapshotType);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        var sql = @"
            SELECT ID AS Id,
                   SNAPSHOT_TYPE AS SnapshotType,
                   父项图号 AS ParentPartNo,
                   父项名称 AS ParentPartName,
                   父项源 AS ParentSource,
                   子项图号 AS ChildPartNo,
                   子项名称 AS ChildPartName,
                   子项源 AS ChildSource,
                   数量 AS Quantity
            FROM BOM_SNAPSHOT WHERE SNAPSHOT_TYPE = @Type
        ";
        var list = conn.Query<BomSnapshot>(sql, new { Type = snapshotType });

        var dict = new Dictionary<string, BomSnapshot>();
        var duplicateCount = 0;
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            var key = item.CompositeKey;
            // 如果存在重复key，记录警告但不中断
            if (!dict.ContainsKey(key))
            {
                dict[key] = item;
            }
            else
            {
                duplicateCount++;
            }
        }

        if (duplicateCount > 0)
        {
            _logger.LogWarning("快照 {Type} 存在 {Count} 条重复键数据", snapshotType, duplicateCount);
        }

        _logger.LogInformation("快照 {Type} 加载完成: {Count} 条 (去重后)", snapshotType, dict.Count);
        return dict;
    }

    /// <summary>
    /// 批量插入差异记录
    /// </summary>
    public void BulkInsertDiffs(List<BomDiffRecord> diffs, CancellationToken ct)
    {
        if (diffs.Count == 0) return;

        _logger.LogInformation("批量插入差异记录: {Count} 条", diffs.Count);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        using var transaction = conn.BeginTransaction();
        try
        {
            var sql = @"
                INSERT INTO BOM_DIFF (父项图号, 子项图号, DIFF_TYPE, OLD_QTY, NEW_QTY)
                VALUES (@ParentPartNo, @ChildPartNo, @DiffType, @OldQty, @NewQty)
            ";

            foreach (var item in diffs)
            {
                ct.ThrowIfCancellationRequested();
                conn.Execute(sql, item, transaction);
            }

            transaction.Commit();
            _logger.LogInformation("差异记录写入完成: {Count} 条", diffs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "差异记录批量插入事务回滚");
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 分页查询差异记录（用于UI展示）
    /// </summary>
    public List<BomDiffRecord> GetDiffsByType(string? diffType = null, int page = 1, int pageSize = 1000)
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        var whereClause = string.IsNullOrEmpty(diffType) ? "" : "WHERE DIFF_TYPE = @Type";
        var offset = (page - 1) * pageSize;

        var sql = $@"
            SELECT ID AS Id,
                   父项图号 AS ParentPartNo,
                   子项图号 AS ChildPartNo,
                   DIFF_TYPE AS DiffType,
                   OLD_QTY AS OldQty,
                   NEW_QTY AS NewQty
            FROM BOM_DIFF
            {whereClause}
            ORDER BY DIFF_TYPE, 父项图号, 子项图号
            LIMIT @PageSize OFFSET @Offset
        ";

        var result = conn.Query<BomDiffRecord>(sql, new { Type = diffType, PageSize = pageSize, Offset = offset }).ToList();
        _logger.LogDebug("查询差异: Type={DiffType}, Page={Page}, 返回 {Count} 条", diffType ?? "ALL", page, result.Count);
        return result;
    }

    /// <summary>
    /// 获取指定快照类型的父项聚合统计（按父项图号分组，统计子项数量）
    /// </summary>
    public Dictionary<string, ParentAggInfo> GetParentAggregation(string snapshotType)
    {
        _logger.LogInformation("获取父项聚合数据: {Type}", snapshotType);

        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        var sql = @"
            SELECT 父项图号 AS ParentPartNo,
                   MAX(父项名称) AS ParentPartName,
                   COUNT(*) AS ChildCount
            FROM BOM_SNAPSHOT
            WHERE SNAPSHOT_TYPE = @Type
            GROUP BY 父项图号
            ORDER BY 父项图号
        ";
        var list = conn.Query<ParentAggInfo>(sql, new { Type = snapshotType });

        var dict = new Dictionary<string, ParentAggInfo>();
        foreach (var item in list)
        {
            if (!dict.ContainsKey(item.ParentPartNo))
                dict[item.ParentPartNo] = item;
        }

        _logger.LogInformation("父项聚合 {Type} 完成: {Count} 个父项", snapshotType, dict.Count);
        return dict;
    }

    /// <summary>
    /// 获取差异统计
    /// </summary>
    public DiffStatistics GetDiffStatistics()
    {
        using var conn = new SQLiteConnection(_dbHelper.ConnectionString);
        conn.Open();

        var stats = new DiffStatistics
        {
            TotalOld = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SNAPSHOT_TYPE = 'OLD'"),
            TotalNew = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_SNAPSHOT WHERE SNAPSHOT_TYPE = 'NEW'"),
            AddedCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_DIFF WHERE DIFF_TYPE = 'ADD'"),
            DeletedCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_DIFF WHERE DIFF_TYPE = 'DELETE'"),
            ModifiedCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM BOM_DIFF WHERE DIFF_TYPE = 'MODIFY'"),
        };

        _logger.LogDebug("统计结果: {Stats}", stats);
        return stats;
    }
}

/// <summary>
/// 差异统计信息
/// </summary>
public class DiffStatistics
{
    public long TotalOld { get; set; }
    public long TotalNew { get; set; }
    public long AddedCount { get; set; }
    public long DeletedCount { get; set; }
    public long ModifiedCount { get; set; }
    public long TotalDiff => AddedCount + DeletedCount + ModifiedCount;

    /// <summary>父项聚合差异数量（有子项数量变化的父项个数）</summary>
    public long ParentAggDiffCount { get; set; }

    public override string ToString()
    {
        var parentInfo = ParentAggDiffCount > 0
            ? $" | 父项差异: {ParentAggDiffCount:N0} 个"
            : "";
        return $"旧视图: {TotalOld:N0} 条 | 新视图: {TotalNew:N0} 条 | 新增: {AddedCount:N0} | 删除: {DeletedCount:N0} | 修改: {ModifiedCount:N0} | 差异合计: {TotalDiff:N0}{parentInfo}";
    }
}
