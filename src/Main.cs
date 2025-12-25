using Flow.Launcher.Plugin;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Flow.Launcher.Plugin.SpeedTest
{
    public class Main : IAsyncPlugin, IPluginI18n
    {
        private PluginInitContext? _context;
        private Settings? _settings;
        private bool _isTestRunning;
        private SpeedTestResult? _lastResult;
        private DateTime _lastTestTime;
        private string? _currentStatus;
        private double _downloadProgress;
        private double _uploadProgress;
        private double _currentDownloadSpeed;
        private double _currentUploadSpeed;
        private Timer? _updateTimer;
        private string? _lastError;
        private DateTime _lastQueryTime;
        private bool _isDarkTheme;

        public Task InitAsync(PluginInitContext context)
        {
            _context = context;
            _settings = context.API.LoadSettingJsonStorage<Settings>();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                if (dispatcher.CheckAccess())
                    _isDarkTheme = context.API.IsApplicationDarkTheme();
                else
                    dispatcher.Invoke(() => _isDarkTheme = context.API.IsApplicationDarkTheme());
            }

            context.API.ActualApplicationThemeChanged += (_, __) =>
            {
                var disp = Application.Current?.Dispatcher;
                if (disp != null)
                {
                    if (disp.CheckAccess())
                        _isDarkTheme = _context.API.IsApplicationDarkTheme();
                    else
                        disp.Invoke(() => _isDarkTheme = _context.API.IsApplicationDarkTheme());
                }
            };

            return Task.CompletedTask;
        }

        private string GetIcon() => _isDarkTheme ? "icon-dark.png" : "icon-light.png";

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            var results = new List<Result>();

            var timeSinceLastQuery = (DateTime.Now - _lastQueryTime).TotalSeconds;
            if (timeSinceLastQuery > 2 && !_isTestRunning)
            {
                _lastResult = null;
                _lastError = null;
            }
            _lastQueryTime = DateTime.Now;

            if (string.IsNullOrWhiteSpace(query.Search) && !_isTestRunning && _lastResult == null && _lastError == null)
            {
                _currentStatus = "Connecting to server...";
                RunTest();

                results.Add(new Result
                {
                    Title = "Testing your internet speed...",
                    SubTitle = "Connecting to nearest server...",
                    IcoPath = GetIcon()
                });

                return results;
            }

            if (_isTestRunning)
            {
                results.Add(new Result
                {
                    Title = _currentStatus ?? "Connecting to server...",
                    SubTitle = BuildProgressText(),
                    IcoPath = GetIcon()
                });
            }
            else if (_lastResult != null)
            {
                var timeSince = DateTime.Now - _lastTestTime;
                var timeStr = timeSince.TotalMinutes < 60
                    ? $"{(int)timeSince.TotalMinutes}m ago"
                    : $"{(int)timeSince.TotalHours}h ago";

                results.Add(new Result
                {
                    Title = $"↓ {_lastResult.DownloadSpeed:F1} Mbps  ↑ {_lastResult.UploadSpeed:F1} Mbps",
                    SubTitle = $"Ping: {_lastResult.Ping:F0} ms • {_lastResult.ServerName} • {timeStr} • Enter to retest",
                    IcoPath = GetIcon(),
                    Action = _ =>
                    {
                        _lastResult = null;
                        _lastError = null;
                        RunTest();
                        return false;
                    }
                });

                results.Add(new Result
                {
                    Title = $"↓ Download: {_lastResult.DownloadSpeed:F2} Mbps",
                    SubTitle = $"Jitter: {_lastResult.DownloadJitter:F1} ms • Latency: {_lastResult.DownloadLatency:F1} ms",
                    IcoPath = GetIcon()
                });

                results.Add(new Result
                {
                    Title = $"↑ Upload: {_lastResult.UploadSpeed:F2} Mbps",
                    SubTitle = $"Jitter: {_lastResult.UploadJitter:F1} ms • Latency: {_lastResult.UploadLatency:F1} ms",
                    IcoPath = GetIcon()
                });

                results.Add(new Result
                {
                    Title = $"📍 {_lastResult.ServerName}",
                    SubTitle = $"{_lastResult.ServerLocation} • ISP: {_lastResult.ISP}",
                    IcoPath = GetIcon()
                });

                if (!string.IsNullOrEmpty(_lastResult.ResultUrl))
                {
                    results.Add(new Result
                    {
                        Title = "View detailed results online",
                        SubTitle = _lastResult.ResultUrl,
                        IcoPath = GetIcon(),
                        Action = _ =>
                        {
                            Process.Start(new ProcessStartInfo(_lastResult.ResultUrl) { UseShellExecute = true });
                            return true;
                        }
                    });
                }
            }
            else if (_lastError != null)
            {
                results.Add(new Result
                {
                    Title = "⚠️ Speed test failed",
                    SubTitle = _lastError + " • Enter to retry",
                    IcoPath = GetIcon(),
                    Action = _ =>
                    {
                        _lastError = null;
                        RunTest();
                        return false;
                    }
                });
            }

            return results;
        }

        private string BuildProgressText()
        {
            if (_uploadProgress > 0)
                return $"↑ Upload: {_currentUploadSpeed:F1} Mbps ({_uploadProgress:F0}%)";
            if (_downloadProgress > 0)
                return $"↓ Download: {_currentDownloadSpeed:F1} Mbps ({_downloadProgress:F0}%)";
            return "Finding best server...";
        }

        private void RunTest()
        {
            if (_isTestRunning)
            {
                _context?.API.ShowMsg("Speed test is already running");
                return;
            }

            _isTestRunning = true;

            Task.Run(async () =>
            {
                try
                {
                    _downloadProgress = 0;
                    _uploadProgress = 0;
                    _currentDownloadSpeed = 0;
                    _currentUploadSpeed = 0;

                    _updateTimer = new Timer(_ =>
                    {
                        if (_isTestRunning && _context != null)
                        {
                            try
                            {
                                _context.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeyword + " ", true);
                            }
                            catch { }
                        }
                    }, null, 300, 300);

                    var cliPath = await SpeedTestCLI.DownloadIfMissing(_context!);

                    var result = await SpeedTestCLI.Run(
                        cliPath,
                        _ => { },
                        (status, download, upload, downloadSpeed, uploadSpeed) =>
                        {
                            _currentStatus = status;
                            _downloadProgress = download;
                            _uploadProgress = upload;
                            _currentDownloadSpeed = downloadSpeed;
                            _currentUploadSpeed = uploadSpeed;
                        },
                        _context!
                    );

                    _lastResult = result;
                    _lastTestTime = DateTime.Now;
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _currentStatus = null;
                    _context?.API.LogException("SpeedTest", "Test failed", ex);
                }
                finally
                {
                    _isTestRunning = false;
                    _updateTimer?.Dispose();
                    _updateTimer = null;

                    await Task.Delay(50);
                    if (_context != null)
                    {
                        try
                        {
                            _context.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeyword, false);
                        }
                        catch { }
                    }
                }
            });
        }

        public string GetTranslatedPluginTitle() => "Speed Test";
        public string GetTranslatedPluginDescription() => "Test your internet connection speed";
    }

    public class Settings { }
}