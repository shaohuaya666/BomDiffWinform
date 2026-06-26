using System.Configuration;
using System.Data.SQLite;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// SQLite 数据库初始化与管理 (v2 动态)
/// </summary>
public class DatabaseHelper
{
    private readonly string _connectionString;
    private readonly string _dbFilePath;
    private readonly SchemaService _schemaService;
    private readonly ViewMappingConfigService _configService;
    private readonly ILogger _logger;

    public DatabaseHelper(SchemaService schemaService, ViewMappingConfigService configService)
    {
        _schemaService = schemaService ?? throw new ArgumentNullException(nameof(schemaService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = LogService.GetLogger<DatabaseHelper>();

        var dbPath = ConfigurationManager.AppSettings["SQLiteDbPath"] ?? "BomDiffData.db";
        if (!Path.IsPathRooted(dbPath))
            dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);
        _dbFilePath = dbPath;
        _connectionString = $"Data Source={dbPath};Version=3;";

        _logger.LogDebug("SQLite 路径: {Path}", _dbFilePath);
    }

    public string ConnectionString => _connectionString;

    public void InitializeDatabase()
    {
        _logger.LogInformation("开始初始化 SQLite (v2 动态Schema)...");

        try
        {
            using var conn = new SQLiteConnection(_connectionString);
            conn.Open();

            // WAL 模式
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragmaCmd.ExecuteNonQuery();
            }

            var cfg = _configService.GetRequiredComparison();
            _schemaService.InitializeDatabase(conn, cfg);

            _logger.LogInformation("SQLite 初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite 初始化失败");
            throw;
        }
    }

    public List<string> ValidateAllSchemas()
    {
        var allIssues = new List<string>();
        try
        {
            using var conn = new SQLiteConnection(_connectionString);
            conn.Open();
            var (v1, i1) = _schemaService.ValidateTable(conn, "BOM_SNAPSHOT", _schemaService.GetSnapshotColumns());
            if (!v1) allIssues.AddRange(i1);
            var (v2, i2) = _schemaService.ValidateTable(conn, "BOM_DIFF", _schemaService.GetDiffColumns());
            if (!v2) allIssues.AddRange(i2);
        }
        catch (Exception ex) { allIssues.Add($"校验异常: {ex.Message}"); }
        return allIssues;
    }

    public double GetDbFileSizeMB()
    {
        if (File.Exists(_dbFilePath))
            return new FileInfo(_dbFilePath).Length / (1024.0 * 1024.0);
        return 0;
    }
}
