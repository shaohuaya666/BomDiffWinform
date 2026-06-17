namespace BomDiffWinform.Models;

/// <summary>
/// BOM快照明细（存储到SQLite）
/// </summary>
public class BomSnapshot
{
    public long Id { get; set; }
    /// <summary>快照类型：OLD / NEW</summary>
    public string SnapshotType { get; set; } = string.Empty;
    /// <summary>父项图号</summary>
    public string ParentPartNo { get; set; } = string.Empty;
    /// <summary>父项名称</summary>
    public string ParentPartName { get; set; } = string.Empty;
    /// <summary>父项源</summary>
    public string ParentSource { get; set; } = string.Empty;
    /// <summary>子项图号</summary>
    public string ChildPartNo { get; set; } = string.Empty;
    /// <summary>子项名称</summary>
    public string ChildPartName { get; set; } = string.Empty;
    /// <summary>子项源</summary>
    public string ChildSource { get; set; } = string.Empty;
    /// <summary>数量</summary>
    public double Quantity { get; set; }

    /// <summary>组合键（父项图号 + 子项图号）</summary>
    public string CompositeKey => $"{ParentPartNo}|{ChildPartNo}";
}
