using System.Configuration;
using System.Text.Json;
using BomDiffWinform.Models;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// 动态视图字段映射配置服务 (v2.0)
/// 
/// 核心变更（v1 → v2）：
/// - 字段集合完全由配置驱动，不再依赖固定的 BomStandardFields
/// - 每项 ComparisonConfig 定义自己需要的字段集、对比键、差异字段
/// - 动态生成 Oracle 查询SQL（物理列名 AS 逻辑列名）
/// - 动态生成 SQLite DDL（列名 = 逻辑字段名）
/// - 内置默认配置兜底，向后兼容 PVS_BOM/PVS_BOM2
/// </summary>
public class ViewMappingConfigService
{
    private readonly ILogger _logger;
    private readonly string _configFilePath;
    private ViewFieldMappingConfig? _config;
    private bool _loaded;

    public ViewMappingConfigService(string? configFilePath = null)
    {
        _logger = LogService.GetLogger<ViewMappingConfigService>();

        _configFilePath = configFilePath
            ?? ConfigurationManager.AppSettings["ViewMappingConfigPath"]
            ?? "bom_view_mappings.json";

        if (!Path.IsPathRooted(_configFilePath))
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _configFilePath);
    }

    public string ConfigFilePath => _configFilePath;
    public bool IsLoaded => _loaded;

    // ==================== 配置加载 ====================

    public void LoadConfig()
    {
        _logger.LogInformation("开始加载动态视图配置 (v2): {Path}", _configFilePath);

        if (!File.Exists(_configFilePath))
        {
            _logger.LogWarning("配置文件不存在，创建内置默认配置");
            _config = CreateDefaultConfig();
            SaveConfig();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(_configFilePath, System.Text.Encoding.UTF8);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                _config = JsonSerializer.Deserialize<ViewFieldMappingConfig>(json, options)
                    ?? CreateDefaultConfig();
                _logger.LogInformation("加载完成: {Count} 组对比配置", _config.Comparisons.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析配置文件失败，使用默认配置");
                _config = CreateDefaultConfig();
            }
        }

        _loaded = true;
    }

    public void ReloadConfig()
    {
        _config = null;
        _loaded = false;
        LoadConfig();
    }

    // ==================== 对比配置查询 ====================

    /// <summary>获取指定ID的对比配置</summary>
    public ComparisonConfig? GetComparison(string id = "default")
    {
        EnsureLoaded();
        return _config?.GetDefault();
    }

    /// <summary>获取默认对比配置（不存在则抛异常）</summary>
    public ComparisonConfig GetRequiredComparison(string id = "default")
    {
        var cfg = GetComparison(id);
        if (cfg == null)
            throw new InvalidOperationException($"对比配置 '{id}' 不存在");
        return cfg;
    }

    /// <summary>获取所有对比配置</summary>
    public IReadOnlyList<ComparisonConfig> GetAllComparisons()
    {
        EnsureLoaded();
        return _config?.Comparisons.AsReadOnly() ?? new List<ComparisonConfig>().AsReadOnly();
    }

    // ==================== 字段映射查询 ====================

    /// <summary>获取视图的物理列名→逻辑字段名映射字典</summary>
    public Dictionary<string, string> GetViewMapping(ComparisonConfig cfg, string viewName)
    {
        if (cfg.ViewMappings.TryGetValue(viewName, out var mapping))
            return mapping;

        _logger.LogWarning("视图 '{View}' 无映射配置，尝试自动推断", viewName);
        // 兜底：尝试用中文默认映射
        var fallback = new Dictionary<string, string>();
        foreach (var field in cfg.FieldDefinitions)
            fallback[field.DisplayName] = field.LogicalName; // 假设中文名 = displayName
        return fallback;
    }

    /// <summary>获取逻辑字段名→显示名映射</summary>
    public Dictionary<string, string> GetDisplayNames(ComparisonConfig cfg)
    {
        return cfg.FieldDefinitions.ToDictionary(f => f.LogicalName, f => f.DisplayName);
    }

    /// <summary>获取逻辑字段名→SQLite数据类型映射</summary>
    public Dictionary<string, string> GetDataTypes(ComparisonConfig cfg)
    {
        return cfg.FieldDefinitions.ToDictionary(f => f.LogicalName, f => f.DataType);
    }

    // ==================== Oracle SQL动态生成 ====================

    /// <summary>构建 SELECT 子句：物理列名 AS 逻辑列名</summary>
    public string BuildSelectClause(ComparisonConfig cfg, string viewName, int indent = 20)
    {
        var mapping = GetViewMapping(cfg, viewName);
        var indentStr = new string(' ', indent);
        var parts = new List<string>(cfg.FieldDefinitions.Count);

        foreach (var field in cfg.FieldDefinitions)
        {
            if (mapping.TryGetValue(field.LogicalName, out var physicalName))
            {
                var col = EscapeIdentifier(physicalName);
                var alias = EscapeIdentifier(field.LogicalName);
                parts.Add($"{col} AS {alias}");
            }
            else
            {
                _logger.LogWarning("字段 '{Logical}' 在视图 '{View}' 中无映射，将使用 NULL 占位", field.LogicalName, viewName);
                parts.Add($"NULL AS {EscapeIdentifier(field.LogicalName)}");
            }
        }

        return string.Join($",\n{indentStr}", parts);
    }

    /// <summary>构建 ORDER BY 子句</summary>
    public string BuildOrderByClause(ComparisonConfig cfg, string viewName)
    {
        var mapping = GetViewMapping(cfg, viewName);
        var orderFields = cfg.DefaultOrderBy.Count > 0 ? cfg.DefaultOrderBy : cfg.KeyFields;
        var parts = new List<string>();

        foreach (var logicalField in orderFields)
        {
            if (mapping.TryGetValue(logicalField, out var physicalName))
                parts.Add(EscapeIdentifier(physicalName));
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "1";
    }

    /// <summary>构建完整 Oracle 分页查询SQL</summary>
    public string BuildPagedQuerySql(ComparisonConfig cfg, string viewName)
    {
        var selectClause = BuildSelectClause(cfg, viewName, indent: 30);
        var orderBy = BuildOrderByClause(cfg, viewName);

        var sql = $@"
SELECT {selectClause}
FROM (
    SELECT t.*, ROW_NUMBER() OVER (ORDER BY {orderBy}) rn
    FROM {EscapeIdentifier(viewName)} t
)
WHERE rn BETWEEN :startRow AND :endRow";

        // 首次输出完整 SQL（方便排查 Oracle 列名映射是否正确）
        _logger.LogInformation("Oracle 分页SQL [{View}]:\n{Sql}", viewName, sql);
        return sql;
    }

    /// <summary>构建 COUNT SQL</summary>
    public string BuildCountSql(string viewName)
    {
        return $"SELECT COUNT(*) FROM {EscapeIdentifier(viewName)}";
    }

    // ==================== 校验 ====================

    /// <summary>校验对比配置完整性</summary>
    public (bool Valid, List<string> Issues) ValidateComparison(ComparisonConfig cfg)
    {
        var issues = new List<string>();

        if (cfg.FieldDefinitions.Count == 0)
            issues.Add("字段定义为空");

        if (cfg.KeyFields.Count == 0)
            issues.Add("对比键字段为空");

        // 检查 keyFields 是否在 fieldDefinitions 中
        var logicalNames = new HashSet<string>(cfg.FieldDefinitions.Select(f => f.LogicalName));
        foreach (var kf in cfg.KeyFields)
            if (!logicalNames.Contains(kf))
                issues.Add($"对比键字段 '{kf}' 不在字段定义中");

        // 检查 compareField
        if (!logicalNames.Contains(cfg.CompareField))
            issues.Add($"差异比较字段 '{cfg.CompareField}' 不在字段定义中");

        // 检查 parentGroupField
        if (!logicalNames.Contains(cfg.ParentGroupField))
            issues.Add($"父项分组字段 '{cfg.ParentGroupField}' 不在字段定义中");

        // 检查每个视图映射是否覆盖了所有逻辑字段
        foreach (var (viewName, mapping) in cfg.ViewMappings)
        {
            foreach (var field in cfg.FieldDefinitions)
            {
                if (!mapping.ContainsKey(field.LogicalName))
                    issues.Add($"视图 '{viewName}' 缺少逻辑字段 '{field.LogicalName}' 的映射");
            }
        }

        return (issues.Count == 0, issues);
    }

    public void ValidateAll()
    {
        EnsureLoaded();
        _logger.LogInformation("========== 校验所有对比配置 ==========");

        foreach (var cfg in _config!.Comparisons)
        {
            var (valid, issues) = ValidateComparison(cfg);
            if (valid)
                _logger.LogInformation("  [OK] '{Id}': {FieldCount}字段, 键={Keys}, 差异字段={Compare}",
                    cfg.Id, cfg.FieldDefinitions.Count,
                    string.Join(",", cfg.KeyFields), cfg.CompareField);
            else
                foreach (var issue in issues)
                    _logger.LogWarning("  [WARN] '{Id}': {Issue}", cfg.Id, issue);
        }

        _logger.LogInformation("========== 校验完成 ==========");
    }

    // ==================== 持久化 ====================

    public void SaveConfig()
    {
        var toSave = _config ?? CreateDefaultConfig();
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(_configFilePath, JsonSerializer.Serialize(toSave, options), System.Text.Encoding.UTF8);
            _logger.LogInformation("配置已保存: {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败");
        }
    }

    // ==================== 私有 ====================

    private static ViewFieldMappingConfig CreateDefaultConfig()
    {
        return new ViewFieldMappingConfig
        {
            Version = "2.0",
            Description = "BOM视图对比配置（内置默认 — 中文列名PVS_BOM/PVS_BOM2）",
            Comparisons = new List<ComparisonConfig>
            {
                new()
                {
                    Id = "default",
                    OldViewName = "PVS_BOM",
                    NewViewName = "PVS_BOM2",
                    KeyFields = new() { "ParentPartNo", "ChildPartNo" },
                    ParentGroupField = "ParentPartNo",
                    ParentDisplayField = "ParentPartName",
                    CompareField = "Quantity",
                    DefaultOrderBy = new() { "ParentPartNo", "ChildPartNo" },
                    FieldDefinitions = new()
                    {
                        new() { LogicalName = "ParentPartNo",  DisplayName = "父项图号", DataType = "TEXT" },
                        new() { LogicalName = "ParentPartName",DisplayName = "父项名称", DataType = "TEXT" },
                        new() { LogicalName = "ParentSource",  DisplayName = "父项源",   DataType = "TEXT" },
                        new() { LogicalName = "ChildPartNo",   DisplayName = "子项图号", DataType = "TEXT" },
                        new() { LogicalName = "ChildPartName", DisplayName = "子项名称", DataType = "TEXT" },
                        new() { LogicalName = "ChildSource",   DisplayName = "子项源",   DataType = "TEXT" },
                        new() { LogicalName = "Quantity",      DisplayName = "数量",     DataType = "REAL" },
                    },
                    ViewMappings = new()
                    {
                        ["PVS_BOM"] = new()
                        {
                            ["ParentPartNo"] = "父项图号", ["ParentPartName"] = "父项名称",
                            ["ParentSource"] = "父项源",   ["ChildPartNo"] = "子项图号",
                            ["ChildPartName"] = "子项名称", ["ChildSource"] = "子项源",
                            ["Quantity"] = "数量"
                        },
                        ["PVS_BOM2"] = new()
                        {
                            ["ParentPartNo"] = "父项图号", ["ParentPartName"] = "父项名称",
                            ["ParentSource"] = "父项源",   ["ChildPartNo"] = "子项图号",
                            ["ChildPartName"] = "子项名称", ["ChildSource"] = "子项源",
                            ["Quantity"] = "数量"
                        }
                    }
                }
            }
        };
    }

    private void EnsureLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("配置未加载，请先调用 LoadConfig()");
    }

    /// <summary>安全脱敏标识符（防SQL注入）</summary>
    private static string EscapeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("标识符不能为空");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\u4e00-\u9fa5a-zA-Z0-9_]+$"))
            throw new ArgumentException($"无效标识符: {name}");
        return name;
    }
}
