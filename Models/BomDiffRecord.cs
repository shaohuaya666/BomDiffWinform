namespace BomDiffWinform.Models;

/// <summary>
/// BOM差异记录
/// </summary>
public class BomDiffRecord
{
    public long Id { get; set; }
    /// <summary>父项图号</summary>
    public string ParentPartNo { get; set; } = string.Empty;
    /// <summary>子项图号</summary>
    public string ChildPartNo { get; set; } = string.Empty;
    /// <summary>差异类型：ADD / DELETE / MODIFY</summary>
    public string DiffType { get; set; } = string.Empty;
    /// <summary>旧数量</summary>
    public double? OldQty { get; set; }
    /// <summary>新数量</summary>
    public double? NewQty { get; set; }

    public string DiffTypeDisplay => DiffType switch
    {
        "ADD" => "新增",
        "DELETE" => "删除",
        "MODIFY" => "修改",
        _ => DiffType
    };
}
