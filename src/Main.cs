using Flow.Launcher.Plugin;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Flow.Launcher.Plugin.SpeedTest
{
    public class Main : IAsyncPlugin, IPluginI18n, ISettingProvider
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
        private string? _lastError;
        private DateTime _lastQueryTime;
        private bool _isDarkTheme;
        private string _currentQuery = string.Empty;
        private Timer? _refreshTimer;
        private Process? _runningProcess;
        private bool _userCancelled;

        public Task InitAsync(PluginInitContext context)
        {
            _context = context;
            _settings = context.API.LoadSettingJsonStorage<Settings>();

            UpdateIcon();
            context.API.ActualApplicationThemeChanged += (_, __) =>
            {
                UpdateIcon();
            };

            return Task.CompletedTask;
        }

        private void UpdateIcon()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher?.CheckAccess() == true)
                _isDarkTheme = _context.API.IsApplicationDarkTheme();
            else
                dispatcher?.Invoke(() => _isDarkTheme = _context.API.IsApplicationDarkTheme());
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

            var q = (query.Search ?? string.Empty).Trim();

            if (_isTestRunning && _currentQuery != query.RawQuery)
            {
                _userCancelled = true;
                try
                {
                    _runningProcess?.Kill(entireProcessTree: true);
                }
                catch
                {
                    try { _runningProcess?.Kill(); } catch { }
                }
                _runningProcess = null;
                _refreshTimer?.Dispose();
                _refreshTimer = null;
                _currentStatus = "User Cancelled";
            }

            if (string.IsNullOrWhiteSpace(q))
            {
                if (_lastResult != null)
                {
                    var timeSince = DateTime.Now - _lastTestTime;
                    var timeStr = timeSince.TotalMinutes < 60 ? $"{(int)timeSince.TotalMinutes}m ago" : $"{(int)timeSince.TotalHours}h ago";
                    results.Add(new Result
                    {
                        Title = $"↓ {_lastResult.DownloadSpeed:F1} Mbps  ↑ {_lastResult.UploadSpeed:F1} Mbps",
                        SubTitle = $"{timeStr} • start • history • ip",
                        IcoPath = GetIcon()
                    });
                }
                else
                {
                    results.Add(new Result
                    {
                        Title = "Type start to test your speed",
                        SubTitle = "start • history • ip",
                        IcoPath = GetIcon()
                    });
                }

                return results;
            }

            var cmd = q.ToLowerInvariant();
            if (cmd == "start")
            {
                if (!_isTestRunning)
                {
                    _currentQuery = query.RawQuery;
                    _currentStatus = "Connecting to server...";
                    RunTest();
                    
                    _refreshTimer?.Dispose();
                    _refreshTimer = new Timer(_ =>
                    {
                        if (_isTestRunning && _context != null)
                        {
                            try
                            {
                                _context.API.ChangeQuery(_currentQuery, true);
                            }
                            catch { }
                        }
                    }, null, 300, 300);
                    
                    results.Add(new Result { Title = "Testing your internet speed...", SubTitle = "Connecting to nearest server...", IcoPath = GetIcon() });
                    return results;
                }
            }

            if (cmd == "history")
            {
                var hist = _settings?.History ?? new List<HistoryEntry>();
                if (hist.Count == 0)
                {
                    results.Add(new Result { Title = "No history", SubTitle = "No previous tests recorded", IcoPath = GetIcon() });
                    return results;
                }

                foreach (var entry in hist.AsReadOnly().Reverse())
                {
                    results.Add(new Result
                    {
                        Title = $"↓ {entry.DownloadSpeed:F1} Mbps  ↑ {entry.UploadSpeed:F1} Mbps",
                        SubTitle = $"{entry.Time:g} • Ping: {entry.Ping:F0} ms",
                        IcoPath = GetIcon()
                    });
                }

                return results;
            }

            if (cmd == "ip")
            {
                var internalIp = await GetInternalIpAsync();
                results.Add(new Result 
                { 
                    Title = "Internal IP", 
                    SubTitle = $"{internalIp} • Press Enter to copy",
                    IcoPath = GetIcon(),
                    Action = _ =>
                    {
                        try { System.Windows.Clipboard.SetText(internalIp); } catch { }
                        return true;
                    }
                });
                
                var externalIp = await GetExternalIpAsync();
                results.Add(new Result 
                { 
                    Title = "External IP", 
                    SubTitle = $"{externalIp} • Press Enter to copy",
                    IcoPath = GetIcon(),
                    Action = _ =>
                    {
                        try { System.Windows.Clipboard.SetText(externalIp); } catch { }
                        return true;
                    }
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
                results.Add(new Result
                {
                    Title = $"↓ {_lastResult.DownloadSpeed:F1} Mbps  ↑ {_lastResult.UploadSpeed:F1} Mbps",
                    SubTitle = $"Ping: {_lastResult.Ping:F0} ms • {_lastResult.ServerName} • Enter to retest",
                    IcoPath = GetIcon(),
                    Action = _ =>
                    {
                        _lastResult = null;
                        _lastError = null;
                        _context?.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeyword + " start", true);
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

                    var cliPath = await SpeedTestCLI.DownloadIfMissing(_context!);

                    var result = await SpeedTestCLI.Run(
                        cliPath,
                        proc =>
                        {
                            _runningProcess = proc;
                            try { proc.EnableRaisingEvents = true; proc.Exited += (_, __) => _runningProcess = null; } catch { }
                        },
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

                    var entry = new HistoryEntry
                    {
                        Time = DateTime.Now,
                        DownloadSpeed = result?.DownloadSpeed ?? 0,
                        UploadSpeed = result?.UploadSpeed ?? 0,
                        Ping = result?.Ping ?? 0,
                        ServerName = result?.ServerName ?? string.Empty,
                        ResultUrl = result?.ResultUrl ?? string.Empty
                    };

                    _settings ??= new Settings();
                    _settings.History.Add(entry);
                    var max = _settings.MaxHistoryEntries > 0 ? _settings.MaxHistoryEntries : 20;
                    if (_settings.History.Count > max)
                        _settings.History.RemoveRange(0, _settings.History.Count - max);

                    try { _context?.API.SaveSettingJsonStorage<Settings>(); } catch { }

                }
                catch (Exception ex)
                {
                    if (_userCancelled)
                    {
                        _lastError = null;
                        _currentStatus = "User Cancelled";
                    }
                    else
                    {
                        _lastError = ex.Message;
                        _currentStatus = null;
                        _context?.API.LogException("SpeedTest", "Test failed", ex);
                    }
                }
                finally
                {
                    _isTestRunning = false;
                    var wasCancelled = _userCancelled;
                    _userCancelled = false;
                    _runningProcess = null;
                    _refreshTimer?.Dispose();
                    _refreshTimer = null;
                    
                    if (!wasCancelled && _context != null)
                    {
                        await Task.Delay(100);
                        try
                        {
                            _context.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeyword + " result", true);
                        }
                        catch { }
                    }
                }
            });
        }

        private async Task<string> GetExternalIpAsync()
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                return (await http.GetStringAsync("https://api.ipify.org")).Trim();
            }
            catch { return "unknown"; }
        }

        private Task<string> GetInternalIpAsync()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                        return Task.FromResult(ip.ToString());
                }
                return Task.FromResult("unknown");
            }
            catch
            {
                return Task.FromResult("unknown");
            }
        }

        public string GetTranslatedPluginTitle() => "Speed Test";
        public string GetTranslatedPluginDescription() => "Test your internet connection speed";

        public System.Windows.Controls.Control CreateSettingPanel()
        {
            return new SettingsControl(_context!);
        }
    }

    public class Settings {
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
        public int MaxHistoryEntries { get; set; } = 20;
    }

    public class HistoryEntry
    {
        public DateTime Time { get; set; }
        public double DownloadSpeed { get; set; }
        public double UploadSpeed { get; set; }
        public double Ping { get; set; }
        public string ServerName { get; set; } = "";
        public string ResultUrl { get; set; } = "";
        public string InternalIP { get; set; } = "";
        public string ExternalIP { get; set; } = "";
    }
}