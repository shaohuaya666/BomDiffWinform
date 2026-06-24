using BomDiffWinform.Models;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// BOM差异对比核心服务 (v2 全动态)
/// 
/// v2 核心改进：
/// - 对比键(KeyFields)、差异字段(CompareField)均由配置驱动
/// - 不再依赖固定的 BomSnapshot/BomDiffRecord 类型
/// - 支持任意字段集的对比，纯 Dictionary O(1) 查找算法
/// </summary>
public class DiffService
{
    private readonly ViewMappingConfigService _configService;
    private readonly ILogger _logger;

    public DiffService(ViewMappingConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = LogService.GetLogger<DiffService>();
    }

    /// <summary>
    /// 执行新旧视图全量明细对比（动态字段，O(1) 算法）
    /// </summary>
    public List<DynamicBomRow> CompareDetails(
        Dictionary<string, DynamicBomRow> oldDict,
        Dictionary<string, DynamicBomRow> newDict,
        ComparisonConfig cfg,
        CancellationToken ct)
    {
        _logger.LogInformation("开始明细对比: 旧={OldCount:N0}, 新={NewCount:N0}, 键={Keys}",
            oldDict.Count, newDict.Count, string.Join(",", cfg.KeyFields));

        var startTime = DateTime.Now;
        var diffs = new List<DynamicBomRow>();
        var addedCount = 0;
        var deletedCount = 0;
        var modifiedCount = 0;
        var unchangedCount = 0;
        var matchedOldKeys = new HashSet<string>();

        var compareField = cfg.CompareField;

        // 遍历新视图：检测新增和修改
        foreach (var (key, newRow) in newDict)
        {
            ct.ThrowIfCancellationRequested();

            if (oldDict.TryGetValue(key, out var oldRow))
            {
                // 比较差异字段的值
                var oldVal = oldRow.GetValue(compareField);
                var newVal = newRow.GetValue(compareField);

                if (!ValuesEqual(oldVal, newVal))
                {
                    diffs.Add(BuildDiffRow(cfg, "MODIFY", newRow, oldVal, newVal));
                    modifiedCount++;
                }
                else
                {
                    unchangedCount++;
                }
                matchedOldKeys.Add(key);
            }
            else
            {
                // 新增
                diffs.Add(BuildDiffRow(cfg, "ADD", newRow, null, newRow.GetValue(compareField)));
                addedCount++;
            }
        }

        // 旧视图未匹配 → 删除
        foreach (var (key, oldRow) in oldDict)
        {
            ct.ThrowIfCancellationRequested();
            if (!matchedOldKeys.Contains(key))
            {
                diffs.Add(BuildDiffRow(cfg, "DELETE", oldRow, oldRow.GetValue(compareField), null));
                deletedCount++;
            }
        }

        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        _logger.LogInformation(
            "明细对比完成: 新增={Added}, 删除={Deleted}, 修改={Modified}, 无变化={Unchanged}, 总差异={Total}, 耗时 {Elapsed:F2}s",
            addedCount, deletedCount, modifiedCount, unchangedCount, diffs.Count, elapsed);

        return diffs;
    }

    /// <summary>
    /// 父项聚合对比（分组字段、显示字段由配置驱动）
    /// </summary>
    public List<DynamicBomRow> CompareParentAggregation(
        Dictionary<string, (string DisplayValue, int ChildCount)> oldAgg,
        Dictionary<string, (string DisplayValue, int ChildCount)> newAgg,
        ComparisonConfig cfg,
        CancellationToken ct)
    {
        _logger.LogInformation("开始父项聚合对比: 旧={OldCount:N0}, 新={NewCount:N0}, 分组={GroupField}",
            oldAgg.Count, newAgg.Count, cfg.ParentGroupField);

        var startTime = DateTime.Now;
        var result = new List<DynamicBomRow>();
        var allKeys = new HashSet<string>(oldAgg.Keys);
        allKeys.UnionWith(newAgg.Keys);

        foreach (var key in allKeys)
        {
            ct.ThrowIfCancellationRequested();

            oldAgg.TryGetValue(key, out var oldInfo);
            newAgg.TryGetValue(key, out var newInfo);
            var oldCount = oldInfo.ChildCount;
            var newCount = newInfo.ChildCount;

            if (oldCount != newCount)
            {
                var row = new DynamicBomRow();
                row.SetValue(cfg.ParentGroupField, key);
                if (cfg.ParentDisplayField != null)
                    row.SetValue(cfg.ParentDisplayField,
                        oldInfo.DisplayValue ?? newInfo.DisplayValue ?? key);
                row.OldChildCount = oldCount;
                row.NewChildCount = newCount;
                result.Add(row);
            }
        }

        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        _logger.LogInformation("父项聚合对比完成: {DiffCount} 个差异, 耗时 {Elapsed:F2}s", result.Count, elapsed);

        return result;
    }

    // ==================== 私有方法 ====================

    private static DynamicBomRow BuildDiffRow(ComparisonConfig cfg, string diffType,
        DynamicBomRow sourceRow, object? oldVal, object? newVal)
    {
        var row = new DynamicBomRow
        {
            DiffType = diffType,
            OldValue = oldVal,
            NewValue = newVal
        };
        // 复制键字段值
        foreach (var kf in cfg.KeyFields)
            row.SetValue(kf, sourceRow.GetValue(kf));
        return row;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // 数值比较容差
        if (double.TryParse(a.ToString(), out var da) && double.TryParse(b.ToString(), out var db))
            return Math.Abs(da - db) < 0.0001;

        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
