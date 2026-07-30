using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.SpeedTest
{
    public class ServerUnavailableException : Exception
    {
        public int ServerId { get; }
        public ServerUnavailableException(int serverId, string message) : base(message)
        {
            ServerId = serverId;
        }
    }

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
            PluginInitContext context,
            int serverId = 0)
        {
            var arguments = "--format=json --progress=yes --accept-license --accept-gdpr";
            if (serverId > 0)
                arguments += $" --server-id={serverId}";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = arguments,
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
                            ServerId = json.Server?.Id ?? 0,
                            ServerName = json.Server?.Name ?? "Unknown",
                            ServerLocation = json.Server?.Location ?? "",
                            ISP = json.Isp ?? "",
                            ResultUrl = json.Result?.Url ?? "",
                            ExternalIP = json.Interface?.ExternalIp ?? "unknown",
                            InternalIP = json.Interface?.InternalIp ?? "unknown"
                        };
                    }
                }
                catch (Exception ex)
                {
                    context.API.LogException("SpeedTest", "Error parsing JSON", ex);
                }
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                context.API.LogException("SpeedTest", "Failed to start speedtest CLI process", ex);
                throw new Exception("Could not start the speedtest CLI - it may be missing or blocked by antivirus", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            processCallback(process);
            
            context.API.LogInfo("SpeedTest", "Process started, waiting for results...");
            await process.WaitForExitAsync();
            
            if (process.ExitCode != 0)
            {
                var errorMsg = errorOutput.ToString();
                context.API.LogException("SpeedTest", $"Process exited with code {process.ExitCode}", new Exception(errorMsg));

                if (serverId > 0 && (
                        errorMsg.IndexOf("cannot find server id", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        errorMsg.IndexOf("not specified server id", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        errorMsg.IndexOf("server id in configuration", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        errorMsg.IndexOf("unable to connect to the specified server", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        errorMsg.IndexOf("no matching servers", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        errorMsg.IndexOf("invalid server", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    throw new ServerUnavailableException(serverId, $"Server {serverId} is unavailable");
                }

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
                else if (string.IsNullOrWhiteSpace(errorMsg))
                {
                    throw new Exception($"Test failed unexpectedly (exit code {process.ExitCode})");
                }
                
                throw new Exception("Test failed - check your internet connection");
            }

            if (result == null)
            {
                throw new Exception("Test completed without producing a result - please try again");
            }

            return result;
        }

        public static async Task<List<ServerListEntry>> GetServers(
            string cliPath,
            PluginInitContext context,
            CancellationToken cancellationToken,
            int timeoutSeconds = 20)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "--servers --accept-license --accept-gdpr",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    stderr.AppendLine(e.Data);
                    context.API.LogWarn("SpeedTest", $"servers stderr: {e.Data}");
                }
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                context.API.LogException("SpeedTest", "Failed to start speedtest CLI process", ex);
                throw new Exception("Could not start the speedtest CLI - it may be missing or blocked by antivirus", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { try { process.Kill(); } catch { } }

                if (cancellationToken.IsCancellationRequested)
                    throw;

                throw new Exception("Timed out fetching server list - check your internet connection");
            }

            if (process.ExitCode != 0)
            {
                var errorMsg = stderr.ToString();
                context.API.LogException("SpeedTest", $"Server list process exited with code {process.ExitCode}", new Exception(errorMsg));

                if (errorMsg.Contains("Limit reached") || errorMsg.Contains("Too many requests"))
                    throw new Exception("Rate limit reached - wait a few minutes and try again");
                if (errorMsg.Contains("Configuration"))
                    throw new Exception("Cannot connect to Speedtest servers - check your connection");

                throw new Exception("Failed to fetch server list");
            }

            var servers = ParseServerListOutput(stdout.ToString());

            if (servers.Count == 0)
                throw new Exception("No nearby servers were found");

            return servers;
        }

        private static readonly Regex ServerLineRegex = new(@"^\s*(\d+)\s+(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex CollapseSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled);

        // Parses the human-readable table produced by `speedtest --servers`
        internal static List<ServerListEntry> ParseServerListOutput(string output)
        {
            var results = new List<ServerListEntry>();
            if (string.IsNullOrWhiteSpace(output))
                return results;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;

                var match = ServerLineRegex.Match(line);
                if (!match.Success) continue; // skips "Closest servers:", header row, "====" separator, etc.
                if (!int.TryParse(match.Groups[1].Value, out var id) || id <= 0) continue;

                var display = CollapseSpacesRegex.Replace(match.Groups[2].Value.Trim(), " — ");
                if (string.IsNullOrWhiteSpace(display)) continue;

                results.Add(new ServerListEntry { Id = id, Name = display });
            }

            return results;
        }
    }

    public class ServerListEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
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
        public int ServerId { get; set; }
        public string ServerName { get; set; } = "";
        public string ServerLocation { get; set; } = "";
        public string ISP { get; set; } = "";
        public string ResultUrl { get; set; } = "";
        public string ExternalIP { get; set; } = "";
        public string InternalIP { get; set; } = "";
        public bool UsedFallbackServer { get; set; }
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

        [JsonPropertyName("interface")]
        public InterfaceInfo? Interface { get; set; }
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
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }
    }

    /* Some speedtest CLI builds/versions emit numeric IDs as JSON numbers, others as strings.
     This converter accepts either so a schema quirk can't blow up parsing of the whole result. */
    public class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
                return value;
            if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out var parsed))
                return parsed;
            return 0;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    public class ResultInfo
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    public class InterfaceInfo
    {
        [JsonPropertyName("externalIp")]
        public string? ExternalIp { get; set; }

        [JsonPropertyName("internalIp")]
        public string? InternalIp { get; set; }
    }
}