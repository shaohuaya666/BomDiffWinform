using System.Configuration;
using BomDiffWinform.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace BomDiffWinform.Services;

/// <summary>
/// Oracle数据访问服务 (v2 全动态)
/// 
/// v2 改进：
/// - 返回 DynamicBomRow 替代固定的 BomSnapshot
/// - SQL 由 ViewMappingConfigService 动态生成
/// - 完全解耦 Oracle 物理列名与后续处理
/// </summary>
public class OracleService
{
    private readonly string _oldViewName;
    private readonly string _newViewName;
    private readonly int _pageSize;
    private readonly ViewMappingConfigService _mappingService;
    private readonly ComparisonConfig _comparisonCfg;
    private readonly ILogger _logger;

    public OracleService(ViewMappingConfigService mappingService)
    {
        _mappingService = mappingService ?? throw new ArgumentNullException(nameof(mappingService));
        _logger = LogService.GetLogger<OracleService>();

        _comparisonCfg = _mappingService.GetRequiredComparison();
        _oldViewName = _comparisonCfg.OldViewName;
        _newViewName = _comparisonCfg.NewViewName;
        _pageSize = int.TryParse(ConfigurationManager.AppSettings["PageSize"], out var ps) ? ps : 5000;

        _logger.LogInformation(
            "OracleService 初始化: 旧={OldView}, 新={NewView}, 分页={PageSize}, 字段数={FieldCount}",
            _oldViewName, _newViewName, _pageSize, _comparisonCfg.FieldDefinitions.Count);
    }

    public int PageSize => _pageSize;
    public string OldViewName => _oldViewName;
    public string NewViewName => _newViewName;
    public ViewMappingConfigService MappingService => _mappingService;

    private string ConnectionString =>
        ConfigurationManager.AppSettings["OracleConnectionString"] ?? string.Empty;

    /// <summary>获取视图总行数</summary>
    public async Task<long> GetTotalCountAsync(string viewName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = new OracleConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var sql = _mappingService.BuildCountSql(viewName);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct));
        _logger.LogInformation("视图 {View} 总行数: {Count:N0}", viewName, count);
        return count;
    }

    /// <summary>
    /// 分页读取视图数据，返回 DynamicBomRow 列表。
    /// Oracle 物理列名 → 逻辑字段名已由 SQL 中的 AS 别名完成，Dapper 动态映射。
    /// </summary>
    public async Task<List<DynamicBomRow>> GetPageDataAsync(string viewName, long startRow, long endRow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = new OracleConnection(ConnectionString);
        await conn.OpenAsync(ct);
        _logger.LogDebug("拉取 {View}: ROW {Start} ~ {End}", viewName, startRow, endRow);

        var sql = _mappingService.BuildPagedQuerySql(_comparisonCfg, viewName);

        // Dapper Query<dynamic> → 转换为 DynamicBomRow
        var dynamicRows = await conn.QueryAsync(
            new CommandDefinition(sql, new { startRow, endRow }, cancellationToken: ct));

        var result = new List<DynamicBomRow>();
        foreach (var dRow in dynamicRows)
        {
            var bomRow = new DynamicBomRow();
            var dict = (IDictionary<string, object>)dRow;
            foreach (var field in _comparisonCfg.FieldDefinitions)
            {
                if (dict.TryGetValue(field.LogicalName, out var value))
                    bomRow.SetValue(field.LogicalName, value);
            }
            result.Add(bomRow);
        }

        _logger.LogDebug("拉取完成 {View}: {Count} 条", viewName, result.Count);
        return result;
    }

    public async Task<(bool Success, string Error)> TestConnectionAsync()
    {
        try
        {
            using var conn = new OracleConnection(ConnectionString);
            await conn.OpenAsync();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Oracle 连接失败");
            return (false, ex.Message);
        }
    }

    public async Task<long> GetTotalPagesAsync(string viewName, CancellationToken ct)
    {
        var total = await GetTotalCountAsync(viewName, ct);
        return (total + _pageSize - 1) / _pageSize;
    }
}
