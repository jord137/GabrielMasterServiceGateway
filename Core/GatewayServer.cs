using System.Net;
using System.Text;
using GabrielGateway.Models;
using GabrielGateway.Plugins;

namespace GabrielGateway.Core;

public class GatewayServer
{
    private readonly HttpListener _listener = new();
    private readonly GatewaySettings _settings;
    private readonly PluginManager _pluginManager;
    private CancellationTokenSource? _cts;

    public GatewayServer(GatewaySettings settings, PluginManager pluginManager)
    {
        _settings = settings;
        _pluginManager = pluginManager;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        string protocol = _settings.UseHttps ? "https" : "http";
        _listener.Prefixes.Add($"{protocol}://*:{_settings.GatewayPort}/");
        _listener.Start();

        Task.Run(() => ListenLoopAsync(_cts.Token));
        Console.WriteLine($"[Gateway] Public Server running on port {_settings.GatewayPort}");
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequestAsync(context));
            }
            catch (HttpListenerException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Gateway Error] {ex.Message}");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            var plugin = await _pluginManager.MatchPluginAsync(context.Request);
            if (plugin != null)
            {
                await plugin.HandleAsync(context);
                return;
            }

            await ServeStaticFileOrWelcomeAsync(context);
        }
        catch (Exception ex)
        {
            try
            {
                context.Response.StatusCode = 500;
                byte[] err = Encoding.UTF8.GetBytes($"500 Gateway Fault: {ex.Message}");
                context.Response.ContentLength64 = err.Length;
                await context.Response.OutputStream.WriteAsync(err);
            }
            catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task ServeStaticFileOrWelcomeAsync(HttpListenerContext context)
    {
        string rawPath = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "";
        if (string.IsNullOrEmpty(rawPath)) rawPath = "index.html";

        string filePath = Path.Combine(_settings.WebRoot, rawPath);

        if (File.Exists(filePath))
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            context.Response.StatusCode = 200;
            context.Response.ContentType = GetMimeType(filePath);
            await context.Response.OutputStream.WriteAsync(fileBytes);
        }
        else
        {
            string html = $@"
<!DOCTYPE html>
<html>
<head><title>Gabriel Master Service Gateway</title><style>body{{font-family:sans-serif;margin:40px;background:#0d1117;color:#c9d1d9;}}h1{{color:#58a6ff;}}</style></head>
<body>
    <h1>Gabriel Master Service Gateway</h1>
    <p>Status: <strong>Online</strong></p>
    <p>Serving on Port: <strong>{_settings.GatewayPort}</strong></p>
    <hr/>
    <p>Place your static assets in <code>{_settings.WebRoot}</code>.</p>
</body>
</html>";
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html";
            await context.Response.OutputStream.WriteAsync(htmlBytes);
        }
    }

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLower() switch
    {
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
    }
}