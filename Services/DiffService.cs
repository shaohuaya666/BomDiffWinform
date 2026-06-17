using BomDiffWinform.Models;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// BOM视图差异对比核心服务（Dictionary O(1) 对比）
/// </summary>
public class DiffService
{
    private readonly ILogger _logger;

    public DiffService()
    {
        _logger = LogService.GetLogger<DiffService>();
    }

    /// <summary>
    /// 执行新旧视图全量对比
    /// 算法：Dictionary O(1) 查找，时间复杂度 O(n+m)
    /// </summary>
    /// <returns>差异记录列表</returns>
    public List<BomDiffRecord> Compare(
        Dictionary<string, BomSnapshot> oldDict,
        Dictionary<string, BomSnapshot> newDict,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "开始明细差异对比: 旧={OldCount:N0}, 新={NewCount:N0}",
            oldDict.Count, newDict.Count);

        var startTime = DateTime.Now;
        var diffs = new List<BomDiffRecord>();
        var addedCount = 0;
        var deletedCount = 0;
        var modifiedCount = 0;
        var unchangedCount = 0;

        // 标记已匹配的旧视图记录
        var matchedOldKeys = new HashSet<string>();

        // 遍历新视图：检测新增和修改
        foreach (var kvp in newDict)
        {
            ct.ThrowIfCancellationRequested();

            var key = kvp.Key;
            var newItem = kvp.Value;

            if (oldDict.TryGetValue(key, out var oldItem))
            {
                // 存在于旧视图，检查数量是否变化
                if (Math.Abs(oldItem.Quantity - newItem.Quantity) > 0.0001)
                {
                    diffs.Add(new BomDiffRecord
                    {
                        ParentPartNo = newItem.ParentPartNo,
                        ChildPartNo = newItem.ChildPartNo,
                        DiffType = "MODIFY",
                        OldQty = oldItem.Quantity,
                        NewQty = newItem.Quantity
                    });
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
                // 不存在于旧视图 → 新增
                diffs.Add(new BomDiffRecord
                {
                    ParentPartNo = newItem.ParentPartNo,
                    ChildPartNo = newItem.ChildPartNo,
                    DiffType = "ADD",
                    OldQty = null,
                    NewQty = newItem.Quantity
                });
                addedCount++;
            }
        }

        // 旧视图中未被匹配的 → 删除
        foreach (var kvp in oldDict)
        {
            ct.ThrowIfCancellationRequested();

            if (!matchedOldKeys.Contains(kvp.Key))
            {
                var oldItem = kvp.Value;
                diffs.Add(new BomDiffRecord
                {
                    ParentPartNo = oldItem.ParentPartNo,
                    ChildPartNo = oldItem.ChildPartNo,
                    DiffType = "DELETE",
                    OldQty = oldItem.Quantity,
                    NewQty = null
                });
                deletedCount++;
            }
        }

        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        _logger.LogInformation(
            "明细差异对比完成: 新增={Added}, 删除={Deleted}, 修改={Modified}, 无变化={Unchanged}, 总差异={TotalDiff:N0}, 耗时 {Elapsed:F2}s",
            addedCount, deletedCount, modifiedCount, unchangedCount, diffs.Count, elapsed);

        return diffs;
    }

    /// <summary>
    /// 按父项图号聚合对比子项数量差异
    /// 场景：视图改写（JOIN→NOT EXISTS）后某些父项子项数量变化
    /// </summary>
    /// <returns>有差异的父项聚合记录列表</returns>
    public List<ParentAggDiffRecord> CompareParentAggregation(
        Dictionary<string, ParentAggInfo> oldAgg,
        Dictionary<string, ParentAggInfo> newAgg,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "开始父项聚合对比: 旧={OldCount:N0}, 新={NewCount:N0}",
            oldAgg.Count, newAgg.Count);

        var startTime = DateTime.Now;
        var result = new List<ParentAggDiffRecord>();
        var allParentKeys = new HashSet<string>(oldAgg.Keys);
        allParentKeys.UnionWith(newAgg.Keys);

        foreach (var key in allParentKeys)
        {
            ct.ThrowIfCancellationRequested();

            oldAgg.TryGetValue(key, out var oldInfo);
            newAgg.TryGetValue(key, out var newInfo);

            var oldCount = oldInfo?.ChildCount ?? 0;
            var newCount = newInfo?.ChildCount ?? 0;

            if (oldCount != newCount)
            {
                result.Add(new ParentAggDiffRecord
                {
                    ParentPartNo = key,
                    ParentPartName = oldInfo?.ParentPartName ?? newInfo?.ParentPartName ?? "",
                    OldChildCount = oldCount,
                    NewChildCount = newCount
                });
            }
        }

        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        _logger.LogInformation(
            "父项聚合对比完成: {DiffCount:N0} 个父项存在差异, 耗时 {Elapsed:F2}s",
            result.Count, elapsed);

        return result;
    }
}
