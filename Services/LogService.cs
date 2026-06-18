using System.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace BomDiffWinform.Services;

/// <summary>
/// 日志服务（基于 Serilog + Microsoft.Extensions.Logging）
/// 输出目标：文件（logs/ 目录滚动）+ 控制台
/// </summary>
public static class LogService
{
    private static ILoggerFactory? _loggerFactory;
    private static readonly object _lock = new();
    private static bool _initialized;

    /// <summary>日志文件路径</summary>
    public static string LogDirectory { get; private set; } = string.Empty;

    /// <summary>当前日志级别</summary>
    public static LogEventLevel MinimumLevel { get; private set; } = LogEventLevel.Information;

    /// <summary>日志保留天数</summary>
    public static int RetentionDays { get; private set; } = 30;

    /// <summary>
    /// 初始化日志系统（必须在程序入口最先调用）
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            // 从配置读取日志参数
            var logLevelStr = ConfigurationManager.AppSettings["LogLevel"] ?? "Information";
            MinimumLevel = Enum.TryParse<LogEventLevel>(logLevelStr, true, out var level)
                ? level
                : LogEventLevel.Information;

            RetentionDays = int.TryParse(ConfigurationManager.AppSettings["LogRetentionDays"], out var days)
                ? Math.Max(1, days)
                : 30;

            // 日志目录：程序目录/logs/
            LogDirectory = ConfigurationManager.AppSettings["LogDirectory"];
            if (string.IsNullOrWhiteSpace(LogDirectory) || !Path.IsPathRooted(LogDirectory))
            {
                LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            }

            // 确保日志目录存在
            Directory.CreateDirectory(LogDirectory);

            // 配置 Serilog
            var logFilePath = Path.Combine(LogDirectory, "bomdiff-.log");
            var errorLogPath = Path.Combine(LogDirectory, "bomdiff-error-.log");

            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Is(MinimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "BomDiffWinform")
                .Enrich.WithProperty("Version", "1.0.0")
                // 异步文件输出：后台线程批量写入，不阻塞调用线程
                .WriteTo.Async(a => a.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetentionDays,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    encoding: System.Text.Encoding.UTF8,
                    fileSizeLimitBytes: 10 * 1024 * 1024  // 10MB
                ), bufferSize: 1000)
                // 错误日志异步输出
                .WriteTo.Async(a => a.File(
                    path: errorLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetentionDays,
                    restrictedToMinimumLevel: LogEventLevel.Error,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    encoding: System.Text.Encoding.UTF8,
                    fileSizeLimitBytes: 10 * 1024 * 1024
                ), bufferSize: 1000)
                // 控制台输出（调试时可在VS输出窗口查看）
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
                );

            var serilogLogger = loggerConfig.CreateLogger();

            // 创建 ILoggerFactory
            _loggerFactory = new SerilogLoggerFactory(serilogLogger);

            _initialized = true;

            // 写入启动日志
            var startupLogger = GetLogger("LogService");
            startupLogger.LogInformation(
                "========== 日志系统初始化完成 ==========");
            startupLogger.LogInformation(
                "日志目录: {LogDirectory}, 级别: {Level}, 保留: {RetentionDays}天",
                LogDirectory, MinimumLevel, RetentionDays);

            // 清理过期日志文件
            CleanOldLogs();
        }
    }

    /// <summary>
    /// 获取指定类型的 ILogger
    /// </summary>
    public static Microsoft.Extensions.Logging.ILogger GetLogger<T>()
    {
        return GetLogger(typeof(T).Name);
    }

    /// <summary>
    /// 获取指定名称的 ILogger
    /// </summary>
    public static Microsoft.Extensions.Logging.ILogger GetLogger(string categoryName)
    {
        EnsureInitialized();
        return _loggerFactory!.CreateLogger(categoryName);
    }

    /// <summary>
    /// 获取日志文件列表（按修改时间降序）
    /// </summary>
    public static List<LogFileInfo> GetLogFiles()
    {
        if (!Directory.Exists(LogDirectory))
            return new List<LogFileInfo>();

        var files = Directory.GetFiles(LogDirectory, "bomdiff-*.log");
        return files
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LogFileInfo
            {
                FileName = f.Name,
                FullPath = f.FullName,
                SizeKB = f.Length / 1024.0,
                LastWriteTime = f.LastWriteTime
            })
            .ToList();
    }

    /// <summary>
    /// 获取日志目录总大小（MB）
    /// </summary>
    public static double GetLogDirectorySizeMB()
    {
        if (!Directory.Exists(LogDirectory)) return 0;
        var files = Directory.GetFiles(LogDirectory, "*", SearchOption.AllDirectories);
        return files.Sum(f =>
        {
            try { return new FileInfo(f).Length; }
            catch { return 0; }
        }) / (1024.0 * 1024.0);
    }

    /// <summary>
    /// 打开日志目录
    /// </summary>
    public static void OpenLogDirectory()
    {
        if (Directory.Exists(LogDirectory))
        {
            System.Diagnostics.Process.Start("explorer.exe", LogDirectory);
        }
    }

    /// <summary>
    /// 打开最新的日志文件
    /// </summary>
    public static void OpenLatestLogFile()
    {
        var files = GetLogFiles();
        var latestError = files.FirstOrDefault(f => f.FileName.Contains("error"));
        var latest = files.FirstOrDefault();
        var target = latestError ?? latest;
        if (target != null && File.Exists(target.FullPath))
        {
            System.Diagnostics.Process.Start("notepad.exe", target.FullPath);
        }
    }

    /// <summary>
    /// 关闭日志系统
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized) return;

            try
            {
                GetLogger("LogService").LogInformation("========== 日志系统关闭 ==========");
                _loggerFactory?.Dispose();
                _loggerFactory = null;
            }
            catch { }
            finally
            {
                _initialized = false;
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("日志系统未初始化，请先调用 LogService.Initialize()");
    }

    /// <summary>
    /// 清理超过保留天数的日志文件
    /// </summary>
    private static void CleanOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            var oldFiles = Directory.GetFiles(LogDirectory, "bomdiff-*.log")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime < cutoff);

            foreach (var file in oldFiles)
            {
                try { file.Delete(); }
                catch { /* 删除失败不阻塞 */ }
            }
        }
        catch { /* 清理异常不阻塞 */ }
    }
}

/// <summary>
/// 日志文件信息
/// </summary>
public class LogFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public double SizeKB { get; set; }
    public DateTime LastWriteTime { get; set; }

    public override string ToString() => $"{FileName} ({SizeKB:F0} KB) - {LastWriteTime:yyyy-MM-dd HH:mm}";
}
