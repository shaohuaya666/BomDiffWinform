using System.Configuration;
using System.Timers;
using Microsoft.Extensions.Logging;

namespace BomDiffWinform.Services;

/// <summary>
/// 夜间自动批处理调度服务
/// </summary>
public class ScheduleService : IDisposable
{
    private readonly System.Timers.Timer _checkTimer;
    private readonly Func<CancellationToken, Task> _executeAction;
    private CancellationTokenSource? _cts;
    private DateTime _lastExecuteDate = DateTime.MinValue;
    private bool _disposed;
    private readonly ILogger _logger;

    public event Action<string>? OnStatusChanged;

    public ScheduleService(Func<CancellationToken, Task> executeAction)
    {
        _logger = LogService.GetLogger<ScheduleService>();
        _executeAction = executeAction;
        _checkTimer = new System.Timers.Timer(30000); // 每30秒检查一次
        _checkTimer.Elapsed += OnCheckTimerElapsed;
        _checkTimer.AutoReset = true;
    }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// 启动调度服务
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        var enabled = ConfigurationManager.AppSettings["AutoRunEnabled"];
        if (!bool.TryParse(enabled, out var autoRun) || !autoRun)
        {
            _logger.LogInformation("自动执行未启用");
            OnStatusChanged?.Invoke("自动执行未启用");
            return;
        }

        var autoRunTime = ConfigurationManager.AppSettings["AutoRunTime"] ?? "00:00";
        IsRunning = true;
        _checkTimer.Start();

        _logger.LogInformation("自动调度已启动，执行时间: {AutoRunTime}", autoRunTime);
        OnStatusChanged?.Invoke("自动调度已启动");
    }

    /// <summary>
    /// 停止调度服务
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        IsRunning = false;
        _checkTimer.Stop();

        _logger.LogInformation("自动调度已停止");
        OnStatusChanged?.Invoke("自动调度已停止");
    }

    private async void OnCheckTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            var autoRunTime = ConfigurationManager.AppSettings["AutoRunTime"] ?? "00:00";
            if (!TimeOnly.TryParse(autoRunTime, out var targetTime)) return;

            var now = TimeOnly.FromDateTime(DateTime.Now);
            var today = DateTime.Today;

            // 检查是否到达执行时间且今天尚未执行
            if (now.Hour == targetTime.Hour &&
                now.Minute == targetTime.Minute &&
                _lastExecuteDate < today)
            {
                _lastExecuteDate = today;

                _logger.LogInformation("触发定时自动执行: {AutoRunTime}", autoRunTime);
                OnStatusChanged?.Invoke($"触发自动执行 ({autoRunTime})...");

                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                try
                {
                    await _executeAction(_cts.Token);
                    _logger.LogInformation("定时自动执行完成");
                    OnStatusChanged?.Invoke($"自动执行完成 ({autoRunTime})");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("定时自动执行被取消");
                    OnStatusChanged?.Invoke("自动执行被取消");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "定时自动执行失败");
                    OnStatusChanged?.Invoke($"自动执行失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // 调度器异常静默处理，避免崩溃
            _logger.LogError(ex, "调度器检查异常（已拦截）");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("ScheduleService 释放资源");
        Stop();
        _checkTimer.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
