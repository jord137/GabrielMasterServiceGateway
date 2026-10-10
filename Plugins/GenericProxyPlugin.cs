using System.Net;
using System.Text;
using GabrielGateway.Models;

namespace GabrielGateway.Plugins;

/// <summary>
/// Universal gateway forwarder capable of proxying HTTP traffic to any backend process.
/// </summary>
public class GenericProxyPlugin : IGatewayPlugin
{
    public PluginConfig Config { get; private set; }
    private readonly HttpClient _httpClient;

    public GenericProxyPlugin(PluginConfig config)
    {
        Config = config;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task InitializeAsync()
    {
        Console.WriteLine($"[Universal Proxy] Routed '{Config.RoutePrefix}' -> {Config.Host}:{Config.TargetPort} ({Config.Name})");
        return Task.CompletedTask;
    }

    public Task HotReloadAsync()
    {
        Console.WriteLine($"[Universal Proxy] Hot reloaded endpoint: {Config.Host}:{Config.TargetPort}");
        return Task.CompletedTask;
    }

    public Task<bool> CanHandleAsync(HttpListenerRequest request)
    {
        if (!Config.IsEnabled) return Task.FromResult(false);
        string path = request.Url?.AbsolutePath ?? "/";
        return Task.FromResult(path.StartsWith(Config.RoutePrefix, StringComparison.OrdinalIgnoreCase));
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // Build target upstream URL
        string pathAndQuery = request.Url?.PathAndQuery ?? "/";
        string targetUrl = $"http://{Config.Host}:{Config.TargetPort}{pathAndQuery}";

        try
        {
            using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUrl);

            // Forward incoming headers
            foreach (string headerKey in request.Headers.AllKeys)
            {
                if (string.IsNullOrEmpty(headerKey)) continue;
                if (headerKey.StartsWith("Host", StringComparison.OrdinalIgnoreCase)) continue;

                string[] values = request.Headers.GetValues(headerKey) ?? Array.Empty<string>();
                upstreamRequest.Headers.TryAddWithoutValidation(headerKey, values);
            }

            // Forward entity body if present
            if (request.HasEntityBody)
            {
                upstreamRequest.Content = new StreamContent(request.InputStream);
                if (!string.IsNullOrEmpty(request.ContentType))
                {
                    upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
                }
            }

            // Send upstream
            using var upstreamResponse = await _httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode = (int)upstreamResponse.StatusCode;

            // Copy response headers back
            foreach (var header in upstreamResponse.Headers)
            {
                response.Headers[header.Key] = string.Join(", ", header.Value);
            }
            foreach (var header in upstreamResponse.Content.Headers)
            {
                response.Headers[header.Key] = string.Join(", ", header.Value);
            }

            await upstreamResponse.Content.CopyToAsync(response.OutputStream);
        }
        catch (HttpRequestException ex)
        {
            response.StatusCode = 502;
            byte[] err = Encoding.UTF8.GetBytes(
                $"<h3>502 Bad Gateway</h3>" +
                $"<p>Service <strong>{Config.Name}</strong> is unreachable on <code>{Config.Host}:{Config.TargetPort}</code>.</p>" +
                $"<small>{ex.Message}</small>");
            response.ContentType = "text/html";
            response.ContentLength64 = err.Length;
            await response.OutputStream.WriteAsync(err);
        }
    }

    public Task ShutdownAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }
}