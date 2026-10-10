using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GabrielGateway.Models;
using GabrielGateway.Services;

namespace GabrielGateway.Plugins;

public class PhpAddonPlugin : IGatewayPlugin
{
    public PluginConfig Config { get; private set; }
    private readonly string _webRoot;
    private Process? _managedPhpProcess;
    private readonly SemaphoreSlim _spawnLock = new(1, 1);

    public PhpAddonPlugin(PluginConfig config, string webRoot = "./wwwroot")
    {
        Config = config;
        _webRoot = Path.GetFullPath(webRoot);
    }

    public async Task InitializeAsync()
    {
        // 1. Resolve or auto-download a valid PHP executable
        Config.PhpExecutablePath = await RuntimeProvisioner.EnsurePhpAsync();

        // 2. Spawn local PHP server worker if port is inactive
        if (Config.AutoSpawnPhpDaemon && !await IsPortOpenAsync(Config.Host, Config.TargetPort, 200))
        {
            await TrySpawnPhpWorkerAsync();
        }

        Console.WriteLine($"[PHP Plugin] Active on port {Config.TargetPort} using engine: {Config.PhpExecutablePath}");
    }

    public async Task HotReloadAsync()
    {
        await ShutdownAsync();
        await InitializeAsync();
        Console.WriteLine($"[PHP Plugin] Reloaded on {Config.Host}:{Config.TargetPort}");
    }

    public Task<bool> CanHandleAsync(HttpListenerRequest request)
    {
        if (!Config.IsEnabled) return Task.FromResult(false);
        string path = request.Url?.AbsolutePath ?? "";
        return Task.FromResult(path.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ||
                               (Config.RoutePrefix != "/" && path.StartsWith(Config.RoutePrefix, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        string rawPath = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "";
        string scriptPath = Path.GetFullPath(Path.Combine(_webRoot, rawPath));

        if (!File.Exists(scriptPath))
        {
            Directory.CreateDirectory(_webRoot);
            scriptPath = Path.Combine(_webRoot, "info.php");
            if (!File.Exists(scriptPath))
            {
                await File.WriteAllTextAsync(scriptPath, "<?php phpinfo(); ?>");
            }
        }

        // FastCGI / Local PHP server socket verification
        if (await IsPortOpenAsync(Config.Host, Config.TargetPort, 500))
        {
            await ProxyToPhpServerAsync(context, scriptPath);
            return;
        }

        // CLI fallback
        if (Config.EnableLocalFallback && TryExecuteLocalPhpCgi(context, scriptPath))
        {
            return;
        }

        // Re-spawn worker if it exited unexpectedly
        if (Config.AutoSpawnPhpDaemon)
        {
            bool spawned = await TrySpawnPhpWorkerAsync();
            if (spawned && await IsPortOpenAsync(Config.Host, Config.TargetPort, 1500))
            {
                await ProxyToPhpServerAsync(context, scriptPath);
                return;
            }
        }

        context.Response.StatusCode = 502;
        byte[] err = Encoding.UTF8.GetBytes("502 Bad Gateway: PHP worker could not be spawned.");
        context.Response.ContentLength64 = err.Length;
        await context.Response.OutputStream.WriteAsync(err);
    }

    private async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task ProxyToPhpServerAsync(HttpListenerContext context, string scriptPath)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            string query = context.Request.Url?.Query ?? "";
            string targetUrl = $"http://{Config.Host}:{Config.TargetPort}/{Path.GetFileName(scriptPath)}{query}";

            using var upstreamReq = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), targetUrl);

            if (context.Request.HasEntityBody)
            {
                upstreamReq.Content = new StreamContent(context.Request.InputStream);
                if (!string.IsNullOrEmpty(context.Request.ContentType))
                    upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }

            using var upstreamRes = await client.SendAsync(upstreamReq);
            context.Response.StatusCode = (int)upstreamRes.StatusCode;
            context.Response.ContentType = upstreamRes.Content.Headers.ContentType?.ToString() ?? "text/html";

            await upstreamRes.Content.CopyToAsync(context.Response.OutputStream);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 502;
            byte[] err = Encoding.UTF8.GetBytes($"502 Bad Gateway: PHP proxy communication failed ({ex.Message})");
            context.Response.ContentLength64 = err.Length;
            await context.Response.OutputStream.WriteAsync(err);
        }
    }

    private bool TryExecuteLocalPhpCgi(HttpListenerContext context, string scriptPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Config.PhpExecutablePath,
                Arguments = $"-f \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.Environment["REQUEST_METHOD"] = context.Request.HttpMethod;
            psi.Environment["SCRIPT_FILENAME"] = scriptPath;
            psi.Environment["DOCUMENT_ROOT"] = _webRoot;

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            if (context.Request.HasEntityBody)
            {
                context.Request.InputStream.CopyTo(proc.StandardInput.BaseStream);
                proc.StandardInput.BaseStream.Flush();
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            byte[] outputBytes = Encoding.UTF8.GetBytes(stdout);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = outputBytes.Length;
            context.Response.OutputStream.Write(outputBytes, 0, outputBytes.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySpawnPhpWorkerAsync()
    {
        await _spawnLock.WaitAsync();
        try
        {
            if (_managedPhpProcess is { HasExited: false })
                return true;

            // Ensure executable exists before calling Process.Start
            if (!File.Exists(Config.PhpExecutablePath) && Config.PhpExecutablePath.Contains(Path.DirectorySeparatorChar))
            {
                Config.PhpExecutablePath = await RuntimeProvisioner.EnsurePhpAsync();
            }

            var psi = new ProcessStartInfo
            {
                FileName = Config.PhpExecutablePath,
                Arguments = $"-S {Config.Host}:{Config.TargetPort} -t \"{_webRoot}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            _managedPhpProcess = Process.Start(psi);
            if (_managedPhpProcess != null)
            {
                Console.WriteLine($"[PHP Gateway] Auto-spawned background PHP instance on {Config.Host}:{Config.TargetPort} (PID: {_managedPhpProcess.Id})");
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PHP Gateway] Could not spawn local PHP instance: {ex.Message}");
        }
        finally
        {
            _spawnLock.Release();
        }
        return false;
    }

    public Task ShutdownAsync()
    {
        if (_managedPhpProcess is { HasExited: false })
        {
            try
            {
                _managedPhpProcess.Kill(true);
                _managedPhpProcess.Dispose();
                _managedPhpProcess = null;
            }
            catch { }
        }
        return Task.CompletedTask;
    }
}