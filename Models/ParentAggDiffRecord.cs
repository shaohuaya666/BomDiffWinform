namespace BomDiffWinform.Models;

/// <summary>
/// 父项聚合对比记录（按父项图号分组统计子项数量差异）
/// </summary>
public class ParentAggDiffRecord
{
    /// <summary>父项图号</summary>
    public string ParentPartNo { get; set; } = string.Empty;
    /// <summary>父项名称</summary>
    public string ParentPartName { get; set; } = string.Empty;
    /// <summary>旧视图子项数量</summary>
    public int OldChildCount { get; set; }
    /// <summary>新视图子项数量</summary>
    public int NewChildCount { get; set; }
    /// <summary>数量差异（新 - 旧）</summary>
    public int CountDiff => NewChildCount - OldChildCount;
    /// <summary>差异状态描述</summary>
    public string DiffStatus => (OldChildCount, NewChildCount) switch
    {
        (0, > 0) => "仅新视图存在",
        (> 0, 0) => "仅旧视图存在",
        _ when CountDiff > 0 => $"增加 +{CountDiff}",
        _ when CountDiff < 0 => $"减少 {CountDiff}",
        _ => "无变化"
    };
}

/// <summary>
/// 父项聚合信息（内部使用）
/// </summary>
public class ParentAggInfo
{
    public string ParentPartNo { get; set; } = string.Empty;
    public string ParentPartName { get; set; } = string.Empty;
    public int ChildCount { get; set; }
}
