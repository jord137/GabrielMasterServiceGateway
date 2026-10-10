using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GabrielGateway.Models;
using GabrielGateway.Services;

namespace GabrielGateway.Plugins;

public class MySqlAddonPlugin : IGatewayPlugin
{
    public PluginConfig Config { get; private set; }
    private Process? _managedMySqlProcess;

    public MySqlAddonPlugin()
    {
        Config = new PluginConfig
        {
            Id = "mysql01",
            Name = "MySQL DB Bridge",
            Type = "MySQL",
            TargetPort = 3306,
            Host = "127.0.0.1",
            IsEnabled = true,
            RoutePrefix = "/mysql"
        };
    }

    public MySqlAddonPlugin(PluginConfig config)
    {
        Config = config;
    }

    public async Task InitializeAsync()
    {
        // 1. Check if an existing MySQL instance is active
        var probe = await ProbeMySqlInstanceAsync(Config.Host, Config.TargetPort, 500);
        if (!probe.IsOnline)
        {
            // 2. Auto-provision portable MariaDB/MySQL if missing
            string mysqlExe = await RuntimeProvisioner.EnsureMySqlAsync(Config.TargetPort);

            // 3. Auto-start database server daemon
            TrySpawnMySqlDaemon(mysqlExe);
        }

        Console.WriteLine($"[MySQL Bridge] Active on route '{Config.RoutePrefix}' -> {Config.Host}:{Config.TargetPort}");
    }

    private void TrySpawnMySqlDaemon(string mysqlExePath)
    {
        try
        {
            if (File.Exists(mysqlExePath))
            {
                string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(mysqlExePath))!;
                string dataDir = Path.Combine(baseDir, "data");

                var psi = new ProcessStartInfo
                {
                    FileName = mysqlExePath,
                    Arguments = $"--datadir=\"{dataDir}\" --port={Config.TargetPort} --console",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _managedMySqlProcess = Process.Start(psi);
                Console.WriteLine($"[MySQL Bridge] Spawned local MySQL daemon (PID: {_managedMySqlProcess?.Id}) on port {Config.TargetPort}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MySQL Bridge] Unable to start daemon: {ex.Message}");
        }
    }

    public async Task HotReloadAsync()
    {
        await ShutdownAsync();
        await InitializeAsync();
    }

    public Task<bool> CanHandleAsync(HttpListenerRequest request)
    {
        if (!Config.IsEnabled) return Task.FromResult(false);
        string path = request.Url?.AbsolutePath ?? "/";
        return Task.FromResult(path.StartsWith(Config.RoutePrefix, StringComparison.OrdinalIgnoreCase));
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        string prefix = Config.RoutePrefix.TrimEnd('/');

        var probe = await ProbeMySqlInstanceAsync(Config.Host, Config.TargetPort, 2000);

        if (path.Equals($"{prefix}/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals($"{prefix}/ping", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = probe.IsOnline ? 200 : 503;
            context.Response.ContentType = "application/json";
            var payload = new
            {
                service = "MySQL Service Gateway",
                status = probe.IsOnline ? "UP" : "DOWN",
                latency_ms = probe.LatencyMs,
                server_version = probe.ServerVersion,
                thread_id = probe.ThreadId,
                details = probe.Message
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
            context.Response.ContentLength64 = json.Length;
            await context.Response.OutputStream.WriteAsync(json);
            return;
        }

        // Diagnostics Dashboard
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        string html = $@"<!DOCTYPE html><html><head><title>MySQL Bridge</title>
<style>body{{font-family:sans-serif;background:#0d1117;color:#c9d1d9;padding:40px;}}
.card{{background:#161b22;border:1px solid #30363d;padding:24px;border-radius:8px;max-width:600px;margin:auto;}}</style>
</head><body><div class='card'>
<h2>MySQL Service Bridge</h2>
<p>Status: <strong style='color:{(probe.IsOnline ? "#3fb950" : "#f85149")}'>{(probe.IsOnline ? "ONLINE" : "OFFLINE")}</strong></p>
<p>Target: <code>{Config.Host}:{Config.TargetPort}</code></p>
<p>Version: <code>{probe.ServerVersion}</code></p>
<p>Details: <code>{probe.Message}</code></p>
</div></body></html>";
        byte[] data = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data);
    }

    private static async Task<MySqlProbeResult> ProbeMySqlInstanceAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            using var stream = client.GetStream();

            byte[] header = new byte[4];
            int readHeader = await stream.ReadAsync(header.AsMemory(0, 4), cts.Token);
            if (readHeader < 4) return new MySqlProbeResult(false, (int)sw.ElapsedMilliseconds, "Header truncated");

            int payloadLength = header[0] | (header[1] << 8) | (header[2] << 16);
            byte[] payload = new byte[payloadLength];
            await stream.ReadAsync(payload.AsMemory(0, payloadLength), cts.Token);

            int nullTerm = Array.IndexOf(payload, (byte)0, 1);
            string version = nullTerm > 1 ? Encoding.ASCII.GetString(payload, 1, nullTerm - 1) : "Unknown";

            return new MySqlProbeResult(true, (int)sw.ElapsedMilliseconds, "Connected", ServerVersion: version);
        }
        catch (Exception ex)
        {
            return new MySqlProbeResult(false, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public Task ShutdownAsync()
    {
        if (_managedMySqlProcess is { HasExited: false })
        {
            try
            {
                _managedMySqlProcess.Kill(true);
                _managedMySqlProcess.Dispose();
                _managedMySqlProcess = null;
            }
            catch { }
        }
        return Task.CompletedTask;
    }
}


/// <summary>
/// Diagnostic report returned after inspecting a MySQL/MariaDB endpoint handshake.
/// </summary>
public record MySqlProbeResult(
    bool IsOnline,
    int LatencyMs,
    string Message,
    byte ProtocolVersion = 0,
    string ServerVersion = "N/A",
    uint ThreadId = 0);