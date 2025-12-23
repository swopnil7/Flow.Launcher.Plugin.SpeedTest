using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.SpeedTest
{
    public static class SpeedTestCLI
    {
        private const string CLI_VERSION = "1.2.0";
        private const string CLI_NAME = "speedtest.exe";
        private const string DOWNLOAD_URL = $"https://install.speedtest.net/app/cli/ookla-speedtest-{CLI_VERSION}-win64.zip";

        public static async Task<string> DownloadIfMissing(PluginInitContext context)
        {
            var cliDir = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, "cli");
            var cliPath = Path.Combine(cliDir, CLI_NAME);

            if (File.Exists(cliPath))
                return cliPath;

            Directory.CreateDirectory(cliDir);
            
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var zipPath = Path.Combine(cliDir, "speedtest.zip");
            
            context.API.LogInfo("SpeedTest", $"Downloading CLI from {DOWNLOAD_URL}");
            
            var response = await client.GetAsync(DOWNLOAD_URL);
            response.EnsureSuccessStatusCode();
            
            await using (var fs = new FileStream(zipPath, FileMode.Create))
            {
                await response.Content.CopyToAsync(fs);
            }

            context.API.LogInfo("SpeedTest", "Extracting CLI...");
            ZipFile.ExtractToDirectory(zipPath, cliDir, true);
            File.Delete(zipPath);
            
            context.API.LogInfo("SpeedTest", "CLI ready");

            return cliPath;
        }

        public static async Task<SpeedTestResult?> Run(
            string cliPath,
            Action<Process> processCallback,
            Action<string, double, double, double, double> onProgress,
            PluginInitContext context)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "--format=json --progress=yes --accept-license --accept-gdpr",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            SpeedTestResult? result = null;
            double downloadProg = 0, uploadProg = 0;
            double downloadSpeed = 0, uploadSpeed = 0;
            var errorOutput = new System.Text.StringBuilder();

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    errorOutput.AppendLine(e.Data);
                    context.API.LogWarn("SpeedTest", $"stderr: {e.Data}");
                    
                    if (e.Data.Contains("Limit reached") || e.Data.Contains("Too many requests"))
                    {
                        onProgress("⚠️ Rate limit reached - wait a few minutes", 0, 0, 0, 0);
                    }
                }
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                try
                {
                    var json = JsonSerializer.Deserialize<SpeedTestJsonResponse>(e.Data);
                    if (json == null) return;

                    if (json.Type == "testStart")
                    {
                        onProgress($"Testing with {json.Server?.Name ?? "server"}", 0, 0, 0, 0);
                    }
                    else if (json.Type == "ping")
                    {
                        onProgress("Testing ping...", 0, 0, 0, 0);
                    }
                    else if (json.Type == "download")
                    {
                        downloadProg = json.Download?.Progress ?? 0;
                        downloadSpeed = (json.Download?.Bandwidth ?? 0) / 125000.0;
                        onProgress("Testing download...", downloadProg * 100, 0, downloadSpeed, 0);
                    }
                    else if (json.Type == "upload")
                    {
                        uploadProg = json.Upload?.Progress ?? 0;
                        uploadSpeed = (json.Upload?.Bandwidth ?? 0) / 125000.0;
                        onProgress("Testing upload...", 100, uploadProg * 100, downloadSpeed, uploadSpeed);
                    }
                    else if (json.Type == "result")
                    {
                        result = new SpeedTestResult
                        {
                            DownloadSpeed = (json.Download?.Bandwidth ?? 0) / 125000.0, // bytes/s to Mbps
                            UploadSpeed = (json.Upload?.Bandwidth ?? 0) / 125000.0,
                            Ping = json.Ping?.Latency ?? 0,
                            DownloadJitter = json.Download?.Latency?.Jitter ?? 0,
                            DownloadLatency = json.Download?.Latency?.Iqm ?? 0,
                            UploadJitter = json.Upload?.Latency?.Jitter ?? 0,
                            UploadLatency = json.Upload?.Latency?.Iqm ?? 0,
                            ServerName = json.Server?.Name ?? "Unknown",
                            ServerLocation = json.Server?.Location ?? "",
                            ISP = json.Isp ?? "",
                            ResultUrl = json.Result?.Url ?? ""
                        };
                    }
                }
                catch (Exception ex)
                {
                    context.API.LogException("SpeedTest", "Error parsing JSON", ex);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            processCallback(process);
            
            context.API.LogInfo("SpeedTest", "Process started, waiting for results...");
            await process.WaitForExitAsync();
            
            if (process.ExitCode != 0)
            {
                var errorMsg = errorOutput.ToString();
                context.API.LogException("SpeedTest", $"Process exited with code {process.ExitCode}", new Exception(errorMsg));
                
                if (errorMsg.Contains("Limit reached") || errorMsg.Contains("Too many requests"))
                {
                    throw new Exception("Rate limit reached - wait a few minutes or change your IP");
                }
                else if (errorMsg.Contains("Configuration") && errorMsg.Contains("Timeout"))
                {
                    throw new Exception("Connection timeout - check your internet or try again");
                }
                else if (errorMsg.Contains("Configuration"))
                {
                    throw new Exception("Cannot connect to Speedtest servers - check your connection");
                }
                
                throw new Exception("Test failed - check your internet connection");
            }

            return result;
        }
    }

    public class SpeedTestResult
    {
        public double DownloadSpeed { get; set; }
        public double UploadSpeed { get; set; }
        public double Ping { get; set; }
        public double DownloadJitter { get; set; }
        public double DownloadLatency { get; set; }
        public double UploadJitter { get; set; }
        public double UploadLatency { get; set; }
        public string ServerName { get; set; } = "";
        public string ServerLocation { get; set; } = "";
        public string ISP { get; set; } = "";
        public string ResultUrl { get; set; } = "";
    }

    public class SpeedTestJsonResponse
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("ping")]
        public PingInfo? Ping { get; set; }

        [JsonPropertyName("download")]
        public SpeedInfo? Download { get; set; }

        [JsonPropertyName("upload")]
        public SpeedInfo? Upload { get; set; }

        [JsonPropertyName("server")]
        public ServerInfo? Server { get; set; }

        [JsonPropertyName("result")]
        public ResultInfo? Result { get; set; }

        [JsonPropertyName("isp")]
        public string? Isp { get; set; }
    }

    public class PingInfo
    {
        [JsonPropertyName("latency")]
        public double Latency { get; set; }
    }

    public class SpeedInfo
    {
        [JsonPropertyName("bandwidth")]
        public long Bandwidth { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("latency")]
        public LatencyInfo? Latency { get; set; }
    }

    public class LatencyInfo
    {
        [JsonPropertyName("jitter")]
        public double Jitter { get; set; }

        [JsonPropertyName("iqm")]
        public double Iqm { get; set; }
    }

    public class ServerInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }
    }

    public class ResultInfo
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
