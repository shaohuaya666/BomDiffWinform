namespace BomDiffWinform.Models;

// ==================== 完全动态BOM数据模型 ====================

/// <summary>
/// 动态BOM数据行 —— 替代固定属性的 BomSnapshot / BomDiffRecord。
/// 
/// 核心设计：
/// - Fields 字典存储所有业务字段值（key = 逻辑字段名）
/// - 对比键、差异字段、父项分组字段均由配置文件动态指定
/// - 同时承载快照明细、差异记录、父项聚合三种用途
/// </summary>
public class DynamicBomRow
{
    /// <summary>自增主键（SQLite）</summary>
    public long Id { get; set; }

    /// <summary>快照类型：OLD / NEW（仅快照明细使用）</summary>
    public string SnapshotType { get; set; } = string.Empty;

    /// <summary>差异类型：ADD / DELETE / MODIFY（仅差异记录使用）</summary>
    public string DiffType { get; set; } = string.Empty;

    /// <summary>旧值（差异比较字段，仅差异记录使用）</summary>
    public object? OldValue { get; set; }

    /// <summary>新值（差异比较字段，仅差异记录使用）</summary>
    public object? NewValue { get; set; }

    // ---------- 父项聚合 ----------
    /// <summary>旧视图子项数（父项聚合使用）</summary>
    public int OldChildCount { get; set; }
    /// <summary>新视图子项数（父项聚合使用）</summary>
    public int NewChildCount { get; set; }

    // ==================== 核心：动态字段值 ====================

    /// <summary>
    /// 动态字段值字典。
    /// Key = 逻辑字段名（来自字段定义），Value = 字段值。
    /// </summary>
    public Dictionary<string, object?> Fields { get; set; } = new();

    // ==================== 便捷访问 ====================

    public object? GetValue(string fieldName) =>
        Fields.TryGetValue(fieldName, out var v) ? v : null;

    public string? GetString(string fieldName) =>
        Fields.TryGetValue(fieldName, out var v) ? v?.ToString() : null;

    public double GetDouble(string fieldName) =>
        Fields.TryGetValue(fieldName, out var v) && v != null && double.TryParse(v.ToString(), out var d) ? d : 0;

    public void SetValue(string fieldName, object? value) =>
        Fields[fieldName] = value;

    /// <summary>从逻辑字段列表构建复合键（用于明细行去重/对比匹配）</summary>
    public string BuildKey(IEnumerable<string> keyFields, string separator = "|")
    {
        return string.Join(separator, keyFields.Select(f =>
            Fields.TryGetValue(f, out var v) ? (v?.ToString() ?? string.Empty) : string.Empty));
    }

    /// <summary>差异类型的中文显示</summary>
    public string DiffTypeDisplay => DiffType switch
    {
        "ADD" => "新增",
        "DELETE" => "删除",
        "MODIFY" => "修改",
        _ => DiffType
    };

    /// <summary>父项聚合差异状态</summary>
    public string AggDiffStatus => (OldChildCount, NewChildCount) switch
    {
        (0, > 0) => "仅新视图存在",
        (> 0, 0) => "仅旧视图存在",
        _ when NewChildCount > OldChildCount => $"增加 +{NewChildCount - OldChildCount}",
        _ when NewChildCount < OldChildCount => $"减少 {NewChildCount - OldChildCount}",
        _ => "无变化"
    };

    public int AggCountDiff => NewChildCount - OldChildCount;
}


// ==================== 动态字段定义 ====================

/// <summary>单个字段定义</summary>
public class FieldDefinition
{
    /// <summary>逻辑字段名（SQLite列名、C#程序内标识符）</summary>
    public string LogicalName { get; set; } = string.Empty;

    /// <summary>UI显示名（DataGridView列头）</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>数据类型：TEXT / REAL / INTEGER</summary>
    public string DataType { get; set; } = "TEXT";
}


// ==================== 对比配置 ====================

/// <summary>单组对比的完整配置</summary>
public class ComparisonConfig
{
    /// <summary>对比标识（用于日志/UI区分）</summary>
    public string Id { get; set; } = "default";

    /// <summary>旧视图名（Oracle）</summary>
    public string OldViewName { get; set; } = "PVS_BOM";

    /// <summary>新视图名（Oracle）</summary>
    public string NewViewName { get; set; } = "PVS_BOM2";

    /// <summary>组成唯一键的逻辑字段名列表（用于明细匹配）</summary>
    public List<string> KeyFields { get; set; } = new() { "ParentPartNo", "ChildPartNo" };

    /// <summary>父项聚合分组字段（逻辑字段名）</summary>
    public string ParentGroupField { get; set; } = "ParentPartNo";

    /// <summary>父项聚合显示名称字段（可选，逻辑字段名）</summary>
    public string? ParentDisplayField { get; set; } = "ParentPartName";

    /// <summary>差异比较字段（逻辑字段名，对比值变化）</summary>
    public string CompareField { get; set; } = "Quantity";

    /// <summary>默认排序字段列表（逻辑字段名）</summary>
    public List<string> DefaultOrderBy { get; set; } = new() { "ParentPartNo", "ChildPartNo" };

    /// <summary>所有字段定义（逻辑名+显示名+类型）</summary>
    public List<FieldDefinition> FieldDefinitions { get; set; } = new();

    /// <summary>各视图物理列名→逻辑字段名映射</summary>
    public Dictionary<string, Dictionary<string, string>> ViewMappings { get; set; } = new();
}


// ==================== 动态表 Schema 类型 ====================

/// <summary>数据库列定义</summary>
public class ColumnDef
{
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = "TEXT";
    public bool Required { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsAutoIncrement { get; set; }
    public string? OldColumnName { get; set; }
}

/// <summary>索引定义</summary>
public class IndexDef
{
    public string Name { get; set; } = string.Empty;
    public List<string> ColumnNames { get; set; } = new();
}


// ==================== 配置文件根对象 ====================

/// <summary>映射配置根对象 —— 对应 bom_view_mappings.json</summary>
public class ViewFieldMappingConfig
{
    public string Version { get; set; } = "2.0";
    public string Description { get; set; } = "BOM视图对比配置";

    /// <summary>对比配置列表（支持多组对比）</summary>
    public List<ComparisonConfig> Comparisons { get; set; } = new();

    /// <summary>获取默认对比配置</summary>
    public ComparisonConfig? GetDefault() =>
        Comparisons.FirstOrDefault(c => c.Id == "default") ?? Comparisons.FirstOrDefault();
}
