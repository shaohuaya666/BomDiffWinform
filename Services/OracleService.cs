using System.Configuration;
using BomDiffWinform.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace BomDiffWinform.Services;

/// <summary>
/// Oracle数据库访问服务（分页拉取BOM视图数据）
/// </summary>
public class OracleService
{
    private readonly string _oldViewName;
    private readonly string _newViewName;
    private readonly int _pageSize;
    private readonly ILogger _logger;

    public OracleService()
    {
        _logger = LogService.GetLogger<OracleService>();

        _oldViewName = ConfigurationManager.AppSettings["OldViewName"] ?? "PVS_BOM";
        _newViewName = ConfigurationManager.AppSettings["NewViewName"] ?? "PVS_BOM2";
        _pageSize = int.TryParse(ConfigurationManager.AppSettings["PageSize"], out var ps) ? ps : 5000;

        _logger.LogInformation(
            "OracleService 初始化: 旧视图={OldView}, 新视图={NewView}, 分页大小={PageSize}",
            _oldViewName, _newViewName, _pageSize);
    }

    public int PageSize => _pageSize;
    public string OldViewName => _oldViewName;
    public string NewViewName => _newViewName;

    private string ConnectionString =>
        ConfigurationManager.AppSettings["OracleConnectionString"] ?? string.Empty;

    /// <summary>
    /// 获取视图总行数
    /// </summary>
    public long GetTotalCount(string viewName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = new OracleConnection(ConnectionString);
        conn.Open();

        var sql = $"SELECT COUNT(*) FROM {EscapeIdentifier(viewName)}";
        var count = conn.ExecuteScalar<long>(sql);

        _logger.LogInformation("视图 {ViewName} 总行数: {Count:N0}", viewName, count);
        return count;
    }

    /// <summary>
    /// 分页读取视图数据（中文列名 → 英文属性名映射）
    /// </summary>
    public List<BomSnapshot> GetPageData(string viewName, long startRow, long endRow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = new OracleConnection(ConnectionString);
        conn.Open();

        _logger.LogDebug("拉取 {ViewName}: ROW {StartRow} ~ {EndRow}", viewName, startRow, endRow);

        // 使用 ROW_NUMBER() 分页，并别名映射中文列名到英文属性名
        var sql = $@"
            SELECT 父项图号 AS ParentPartNo,
                   父项名称 AS ParentPartName,
                   父项源 AS ParentSource,
                   子项图号 AS ChildPartNo,
                   子项名称 AS ChildPartName,
                   子项源 AS ChildSource,
                   数量 AS Quantity
            FROM (
                SELECT t.*, ROW_NUMBER() OVER (ORDER BY 父项图号, 子项图号) rn
                FROM {EscapeIdentifier(viewName)} t
            )
            WHERE rn BETWEEN :startRow AND :endRow
        ";

        var result = conn.Query<BomSnapshot>(sql, new { startRow, endRow }).ToList();
        _logger.LogDebug("拉取完成 {ViewName}: {Count} 条", viewName, result.Count);
        return result;
    }

    /// <summary>
    /// 测试Oracle连接
    /// </summary>
    public bool TestConnection(out string error)
    {
        try
        {
            _logger.LogInformation("测试 Oracle 连接...");
            using var conn = new OracleConnection(ConnectionString);
            conn.Open();
            error = string.Empty;
            _logger.LogInformation("Oracle 连接成功");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Oracle 连接失败");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 获取视图总页数
    /// </summary>
    public long GetTotalPages(string viewName, CancellationToken ct)
    {
        var total = GetTotalCount(viewName, ct);
        var pages = (total + _pageSize - 1) / _pageSize;
        _logger.LogInformation("视图 {ViewName} 总页数: {Pages}", viewName, pages);
        return pages;
    }

    private static string EscapeIdentifier(string name)
    {
        // 防止SQL注入：只允许字母、数字、下划线
        if (string.IsNullOrWhiteSpace(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_]+$"))
        {
            throw new ArgumentException($"无效的视图名称: {name}");
        }
        return name;
    }
}
