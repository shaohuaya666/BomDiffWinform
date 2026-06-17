using System.Configuration;
using System.Data.SQLite;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// SQLite数据库初始化与管理
/// </summary>
public class DatabaseHelper
{
    private readonly string _connectionString;
    private readonly string _dbFilePath;
    private readonly ILogger _logger;

    public DatabaseHelper()
    {
        _logger = LogService.GetLogger<DatabaseHelper>();

        var dbPath = ConfigurationManager.AppSettings["SQLiteDbPath"] ?? "BomDiffData.db";
        // 确保使用绝对路径
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);
        }
        _dbFilePath = dbPath;
        _connectionString = $"Data Source={dbPath};Version=3;";

        _logger.LogDebug("SQLite 数据库路径: {DbPath}", _dbFilePath);
    }

    public string ConnectionString => _connectionString;

    /// <summary>
    /// 初始化数据库表结构
    /// </summary>
    public void InitializeDatabase()
    {
        _logger.LogInformation("开始初始化 SQLite 数据库...");

        try
        {
            using var conn = new SQLiteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();

            // BOM快照表
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS BOM_SNAPSHOT (
                    ID INTEGER PRIMARY KEY AUTOINCREMENT,
                    SNAPSHOT_TYPE TEXT NOT NULL,
                    父项图号 TEXT NOT NULL,
                    父项名称 TEXT,
                    父项源 TEXT,
                    子项图号 TEXT NOT NULL,
                    子项名称 TEXT,
                    子项源 TEXT,
                    数量 REAL,
                    SOURCE TEXT
                );
            ";
            cmd.ExecuteNonQuery();
            _logger.LogDebug("BOM_SNAPSHOT 表就绪");

            // 快照表索引
            cmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS IDX_SNAPSHOT_TYPE ON BOM_SNAPSHOT(SNAPSHOT_TYPE);
                CREATE INDEX IF NOT EXISTS IDX_SNAPSHOT_KEY ON BOM_SNAPSHOT(父项图号, 子项图号);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogDebug("BOM_SNAPSHOT 索引就绪");

            // 差异表
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS BOM_DIFF (
                    ID INTEGER PRIMARY KEY AUTOINCREMENT,
                    父项图号 TEXT,
                    子项图号 TEXT,
                    DIFF_TYPE TEXT NOT NULL,
                    OLD_QTY REAL,
                    NEW_QTY REAL
                );
            ";
            cmd.ExecuteNonQuery();
            _logger.LogDebug("BOM_DIFF 表就绪");

            // 差异表索引
            cmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS IDX_DIFF_TYPE ON BOM_DIFF(DIFF_TYPE);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogDebug("BOM_DIFF 索引就绪");

            _logger.LogInformation("SQLite 数据库初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite 数据库初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 获取数据库文件大小（MB）
    /// </summary>
    public double GetDbFileSizeMB()
    {
        if (File.Exists(_dbFilePath))
        {
            var size = new FileInfo(_dbFilePath).Length / (1024.0 * 1024.0);
            _logger.LogDebug("数据库文件大小: {Size:F2} MB", size);
            return size;
        }
        return 0;
    }
}
