using System.Net;
using System.Text;
using System.Text.Json;
using GabrielGateway.Models;
using GabrielGateway.Plugins;
using GabrielGateway.Security;

namespace GabrielGateway.Core;

public class AdminServer
{
    private readonly HttpListener _listener = new();
    private readonly GatewaySettings _settings;
    private readonly PluginManager _pluginManager;
    private readonly AuthManager _authManager;
    private CancellationTokenSource? _cts;

    public AdminServer(GatewaySettings settings, PluginManager pluginManager, AuthManager authManager)
    {
        _settings = settings;
        _pluginManager = pluginManager;
        _authManager = authManager;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        string protocol = _settings.UseHttps ? "https" : "http";
        _listener.Prefixes.Add($"{protocol}://*:{_settings.AdminPort}/");
        _listener.Start();

        Task.Run(() => ListenLoopAsync(_cts.Token));
        Console.WriteLine($"[Admin UI] Management Dashboard running on port {_settings.AdminPort}");
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessAdminRequestAsync(context));
            }
            catch (HttpListenerException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Admin Server Error] {ex.Message}");
            }
        }
    }

    private async Task ProcessAdminRequestAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        string method = context.Request.HttpMethod;

        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

        if (method == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        try
        {
            if (path == "/api/login" && method == "POST")
            {
                await HandleLoginAsync(context);
                return;
            }

            if (path == "/" || path == "/index.html")
            {
                await ServeAdminDashboardAsync(context);
                return;
            }

            // Authenticate token
            string? token = context.Request.Headers["Authorization"]?.Replace("Bearer ", "");
            var user = _authManager.ValidateSession(token);

            if (user == null)
            {
                context.Response.StatusCode = 401;
                await WriteJsonAsync(context, new { error = "Unauthorized access." });
                return;
            }

            switch (path)
            {
                case "/api/plugins" when method == "GET":
                    await WriteJsonAsync(context, _pluginManager.GetAll().Select(p => p.Config));
                    break;

                case "/api/plugins/types" when method == "GET":
                    await WriteJsonAsync(context, _pluginManager.GetAvailablePluginTypes());
                    break;

                case "/api/plugins/toggle" when method == "POST":
                    await HandlePluginToggleAsync(context);
                    break;

                case "/api/plugins/reload" when method == "POST":
                    await HandlePluginReloadAsync(context);
                    break;

                case "/api/plugins/add" when method == "POST":
                    await HandlePluginAddAsync(context);
                    break;

                case "/api/plugins/install" when method == "POST":
                    await HandlePluginInstallAsync(context);
                    break;

                case "/api/plugins/scan" when method == "POST":
                    int count = _pluginManager.ScanAndLoadPluginsFromFolder("./plugins");
                    await WriteJsonAsync(context, new { success = true, newPluginsLoaded = count });
                    break;

                case "/api/users" when method == "GET":
                    await WriteJsonAsync(context, _authManager.GetAllUsers().Select(u => new { u.Username, u.Role, u.CreatedAt }));
                    break;

                case "/api/users/add" when method == "POST":
                    await HandleUserAddAsync(context, user);
                    break;

                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, new { error = ex.Message });
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleLoginAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var json = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        string user = doc.RootElement.GetProperty("username").GetString() ?? "";
        string pass = doc.RootElement.GetProperty("password").GetString() ?? "";

        if (_authManager.Authenticate(user, pass, out var account))
        {
            string token = _authManager.CreateSession(user);
            await WriteJsonAsync(context, new { token, role = account!.Role.ToString(), username = account.Username });
        }
        else
        {
            context.Response.StatusCode = 401;
            await WriteJsonAsync(context, new { error = "Invalid credentials" });
        }
    }

    private async Task HandlePluginToggleAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        string id = doc.RootElement.GetProperty("id").GetString()!;
        bool state = doc.RootElement.GetProperty("enabled").GetBoolean();

        bool success = _pluginManager.SetPluginState(id, state);
        await WriteJsonAsync(context, new { success });
    }

    private async Task HandlePluginReloadAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        string id = doc.RootElement.GetProperty("id").GetString()!;
        int? port = doc.RootElement.TryGetProperty("port", out var pVal) ? pVal.GetInt32() : null;

        bool success = await _pluginManager.HotReloadPluginAsync(id, port);
        await WriteJsonAsync(context, new { success });
    }

    private async Task HandlePluginAddAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var config = JsonSerializer.Deserialize<PluginConfig>(await reader.ReadToEndAsync());

        if (config == null || string.IsNullOrWhiteSpace(config.Name))
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { error = "Invalid plugin payload." });
            return;
        }

        // Dynamically instantiate ANY plugin type using the PluginManager factory
        IGatewayPlugin plugin = _pluginManager.CreatePluginInstance(config, _settings.WebRoot);
        _pluginManager.RegisterPlugin(plugin);

        await WriteJsonAsync(context, new { success = true, plugin = config });
    }

    private async Task HandlePluginInstallAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        string url = doc.RootElement.GetProperty("url").GetString()!;

        var (success, message) = await _pluginManager.DownloadAndInstallPluginAsync(url);
        if (!success) context.Response.StatusCode = 400;
        await WriteJsonAsync(context, new { success, message });
    }

    private async Task HandleUserAddAsync(HttpListenerContext context, UserAccount currentUser)
    {
        if (currentUser.Role != UserRole.Admin)
        {
            context.Response.StatusCode = 403;
            await WriteJsonAsync(context, new { error = "Only Admins can add accounts." });
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream);
        var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        string username = doc.RootElement.GetProperty("username").GetString()!;
        string password = doc.RootElement.GetProperty("password").GetString()!;
        string roleStr = doc.RootElement.GetProperty("role").GetString()!;

        if (Enum.TryParse<UserRole>(roleStr, true, out var role))
        {
            bool added = _authManager.CreateUser(username, password, role);
            await WriteJsonAsync(context, new { success = added });
        }
        else
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { error = "Invalid role specified." });
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object obj)
    {
        context.Response.ContentType = "application/json";
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(obj, new JsonSerializerOptions { WriteIndented = true });
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data);
    }

    private async Task ServeAdminDashboardAsync(HttpListenerContext context)
    {
        string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <title>Gabriel Gateway Control Center</title>
    <style>
        :root { --bg: #0d1117; --card: #161b22; --border: #30363d; --text: #c9d1d9; --accent: #238636; --blue: #58a6ff; --red: #da3633; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 25px; }
        .container { max-width: 1100px; margin: auto; }
        h1, h2 { color: var(--blue); }
        .card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 20px; margin-bottom: 24px; }
        table { width: 100%; border-collapse: collapse; margin-top: 10px; }
        th, td { text-align: left; padding: 12px; border-bottom: 1px solid var(--border); }
        input, select, button { background: #21262d; border: 1px solid var(--border); color: var(--text); padding: 8px 12px; border-radius: 6px; }
        button { cursor: pointer; font-weight: bold; }
        button.btn-green { background: var(--accent); color: white; border: none; }
        button.btn-blue { background: #1f6feb; color: white; border: none; }
        .badge { padding: 4px 8px; border-radius: 12px; font-size: 11px; }
        .badge-on { background: #1f6feb; color: white; }
        .badge-off { background: #484f58; color: white; }
        .form-row { display: flex; gap: 10px; margin-top: 10px; flex-wrap: wrap; }
    </style>
</head>
<body>
<div class=""container"">
    <h1>Gabriel Master Service Gateway</h1>
    <p>Admin Operations & Dynamic Extensions Console</p>

    <!-- Auth Card -->
    <div class=""card"" id=""authCard"">
        <h2>Authentication</h2>
        <div class=""form-row"">
            <input type=""text"" id=""loginUser"" placeholder=""Username (default: admin)"">
            <input type=""password"" id=""loginPass"" placeholder=""Password (default: GabrielAdmin2026!)"">
            <button class=""btn-green"" onclick=""login()"">Sign In</button>
        </div>
        <p id=""authStatus"" style=""margin-top: 10px;""></p>
    </div>

    <div id=""dashboard"" style=""display:none;"">
        <!-- Services Manager -->
        <div class=""card"">
            <h2>Active Services & Plugins</h2>
            <table id=""pluginTable"">
                <thead>
                    <tr><th>Name</th><th>Type</th><th>Host:Port</th><th>Route</th><th>Status</th><th>Actions</th></tr>
                </thead>
                <tbody></tbody>
            </table>

            <h3 style=""margin-top: 25px;"">Install External Plugin (.DLL) from Web</h3>
            <div class=""form-row"">
                <input type=""text"" id=""installUrl"" placeholder=""https://example.com/plugins/CustomPlugin.dll"" style=""flex: 1; min-width: 320px;"">
                <button class=""btn-blue"" onclick=""installFromUrl()"">Download & Hot Load</button>
                <button onclick=""scanFolder()"">Rescan ./plugins Directory</button>
            </div>
            <p id=""installMsg"" style=""font-size: 13px; margin-top: 8px;""></p>

            <h3 style=""margin-top: 25px;"">Attach Any Service or Loaded Plugin Type</h3>
            <div class=""form-row"">
                <input type=""text"" id=""pName"" placeholder=""Service Name (e.g. Node API, Python ML)"">
                <select id=""pType""></select>
                <input type=""text"" id=""pHost"" placeholder=""Host (127.0.0.1)"" value=""127.0.0.1"" style=""width:120px"">
                <input type=""number"" id=""pPort"" placeholder=""Port (e.g. 5000, 8000)"" style=""width:100px"">
                <input type=""text"" id=""pPrefix"" placeholder=""Route Prefix (/api)"" style=""width:140px"">
                <button class=""btn-green"" onclick=""createPlugin()"">Attach Service</button>
            </div>
        </div>

        <!-- User Accounts & Roles -->
        <div class=""card"">
            <h2>Gateway Access Management</h2>
            <table id=""userTable"">
                <thead>
                    <tr><th>Username</th><th>Role</th><th>Created</th></tr>
                </thead>
                <tbody></tbody>
            </table>

            <h3 style=""margin-top: 20px;"">Add Moderator / Admin</h3>
            <div class=""form-row"">
                <input type=""text"" id=""uName"" placeholder=""Username"">
                <input type=""password"" id=""uPass"" placeholder=""Password"">
                <select id=""uRole""><option value=""Moderator"">Moderator</option><option value=""Admin"">Admin</option></select>
                <button class=""btn-green"" onclick=""createUser()"">Add Account</button>
            </div>
        </div>
    </div>
</div>

<script>
    let token = '';

    async function login() {
        const u = document.getElementById('loginUser').value;
        const p = document.getElementById('loginPass').value;
        const res = await fetch('/api/login', {
            method: 'POST',
            body: JSON.stringify({ username: u, password: p })
        });
        const data = await res.json();
        if(res.ok) {
            token = data.token;
            document.getElementById('authStatus').innerText = `Logged in as ${data.username} (${data.role})`;
            document.getElementById('dashboard').style.display = 'block';
            refreshData();
        } else {
            alert(data.error || 'Login failed');
        }
    }

    async function refreshData() {
        // Load active plugins
        const pRes = await fetch('/api/plugins', { headers: { 'Authorization': 'Bearer ' + token } });
        const plugins = await pRes.json();
        const pBody = document.querySelector('#pluginTable tbody');
        pBody.innerHTML = '';
        plugins.forEach(p => {
            pBody.innerHTML += `<tr>
                <td><strong>${p.Name}</strong></td>
                <td><code>${p.Type}</code></td>
                <td>${p.Host}:<input type='number' id='port-${p.Id}' value='${p.TargetPort}' style='width:80px'></td>
                <td><code>${p.RoutePrefix}</code></td>
                <td><span class='badge ${p.IsEnabled ? 'badge-on':'badge-off'}'>${p.IsEnabled ? 'ACTIVE':'DISABLED'}</span></td>
                <td>
                    <button onclick='togglePlugin(""${p.Id}"", ${!p.IsEnabled})'>${p.IsEnabled ? 'Disable':'Enable'}</button>
                    <button class='btn-green' onclick='reloadPlugin(""${p.Id}"")'>Hot Reload</button>
                </td>
            </tr>`;
        });

        // Load dynamically available plugin types into select dropdown
        const tRes = await fetch('/api/plugins/types', { headers: { 'Authorization': 'Bearer ' + token } });
        const types = await tRes.json();
        const typeSelect = document.getElementById('pType');
        typeSelect.innerHTML = '';
        types.forEach(t => {
            typeSelect.innerHTML += `<option value=""${t}"">${t}</option>`;
        });

        // Load users
        const uRes = await fetch('/api/users', { headers: { 'Authorization': 'Bearer ' + token } });
        const users = await uRes.json();
        const uBody = document.querySelector('#userTable tbody');
        uBody.innerHTML = '';
        users.forEach(u => {
            uBody.innerHTML += `<tr><td>${u.Username}</td><td><strong>${u.Role}</strong></td><td>${new Date(u.CreatedAt).toLocaleString()}</td></tr>`;
        });
    }

    async function togglePlugin(id, state) {
        await fetch('/api/plugins/toggle', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token },
            body: JSON.stringify({ id, enabled: state })
        });
        refreshData();
    }

    async function reloadPlugin(id) {
        const port = parseInt(document.getElementById('port-' + id).value);
        await fetch('/api/plugins/reload', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token },
            body: JSON.stringify({ id, port })
        });
        alert('Service hot reloaded with target port: ' + port);
        refreshData();
    }

    async function installFromUrl() {
        const url = document.getElementById('installUrl').value;
        if(!url) return alert('Enter a valid URL to a .dll');
        const status = document.getElementById('installMsg');
        status.innerText = 'Downloading and loading plugin assembly...';

        const res = await fetch('/api/plugins/install', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token },
            body: JSON.stringify({ url })
        });
        const data = await res.json();
        status.innerText = data.message;
        refreshData();
    }

    async function scanFolder() {
        const res = await fetch('/api/plugins/scan', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token }
        });
        const data = await res.json();
        alert(`Scan finished. Loaded ${data.newPluginsLoaded} new plugin type(s).`);
        refreshData();
    }

    async function createPlugin() {
        const payload = {
            Name: document.getElementById('pName').value,
            Type: document.getElementById('pType').value,
            Host: document.getElementById('pHost').value || '127.0.0.1',
            TargetPort: parseInt(document.getElementById('pPort').value) || 80,
            RoutePrefix: document.getElementById('pPrefix').value || '/',
            IsEnabled: true
        };
        await fetch('/api/plugins/add', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token },
            body: JSON.stringify(payload)
        });
        refreshData();
    }

    async function createUser() {
        const payload = {
            username: document.getElementById('uName').value,
            password: document.getElementById('uPass').value,
            role: document.getElementById('uRole').value
        };
        const res = await fetch('/api/users/add', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token },
            body: JSON.stringify(payload)
        });
        if(!res.ok) alert((await res.json()).error);
        refreshData();
    }
</script>
</body>
</html>";

        byte[] data = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
    }
}