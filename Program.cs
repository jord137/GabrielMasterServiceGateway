using GabrielGateway.Cli;
using GabrielGateway.Core;
using GabrielGateway.Models;
using GabrielGateway.Plugins;
using GabrielGateway.Security;

Console.Title = "Gabriel Master Service Gateway";
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=================================================");
Console.WriteLine("         GABRIEL MASTER SERVICE GATEWAY          ");
Console.WriteLine("=================================================");
Console.ResetColor();

// 1. Core Configuration & Directory Setup
var settings = new GatewaySettings
{
    GatewayPort = 8080,
    AdminPort = 9090,
    WebRoot = "./wwwroot"
};

string pluginsDir = "./plugins";
Directory.CreateDirectory(settings.WebRoot);
Directory.CreateDirectory(pluginsDir);

// 2. Initialize Core Subsystems
var authManager = new AuthManager();
var pluginManager = new PluginManager();

// 3. Register Standard Built-In Addons
pluginManager.RegisterPlugin(new PhpAddonPlugin(new PluginConfig
{
    Id = "php01",
    Name = "PHP-FPM Engine",
    Type = "PHP",
    TargetPort = 9000,
    Host = "127.0.0.1",
    IsEnabled = true,
    RoutePrefix = "/"
}, settings.WebRoot));

pluginManager.RegisterPlugin(new MySqlAddonPlugin(new PluginConfig
{
    Id = "mysql01",
    Name = "MySQL DB Bridge",
    Type = "MySQL",
    TargetPort = 3306,
    Host = "127.0.0.1",
    IsEnabled = true,
    RoutePrefix = "/mysql"
}));

// 4. Dynamically Load Any Existing Plugin DLLs from ./plugins
int dynamicallyLoaded = pluginManager.ScanAndLoadPluginsFromFolder(pluginsDir);
if (dynamicallyLoaded > 0)
{
    Console.WriteLine($"[Boot] Dynamically discovered {dynamicallyLoaded} external plugin(s) from {pluginsDir}.");
}

// 5. Start HTTP Listeners
var gatewayServer = new GatewayServer(settings, pluginManager);
gatewayServer.Start();

var adminServer = new AdminServer(settings, pluginManager, authManager);
adminServer.Start();

Console.WriteLine($"\n[Ready] Gateway listening at: http://localhost:{settings.GatewayPort}");
Console.WriteLine($"[Ready] Admin UI listening at: http://localhost:{settings.AdminPort}\n");

// 6. Graceful Shutdown Registration
var shutdownCts = new CancellationTokenSource();
var shutdownCompleted = new ManualResetEventSlim(false);

async Task PerformGracefulShutdownAsync()
{
    if (shutdownCts.IsCancellationRequested) return;
    shutdownCts.Cancel();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\n[Shutdown] Graceful shutdown signal received. Stopping listeners and services...");
    Console.ResetColor();

    gatewayServer.Stop();
    adminServer.Stop();
    await pluginManager.ShutdownAllAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("[Shutdown] All servers stopped and plugin resources disposed safely.");
    Console.ResetColor();
    shutdownCompleted.Set();
}

// Handle Ctrl+C (SIGINT)
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    Task.Run(async () =>
    {
        await PerformGracefulShutdownAsync();
        Environment.Exit(0);
    });
};

// Handle SIGTERM / Process Termination
AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) =>
{
    PerformGracefulShutdownAsync().GetAwaiter().GetResult();
};

// 7. Interactive CLI Loop
var cli = new AdminCli(pluginManager, authManager, PerformGracefulShutdownAsync);
await cli.RunAsync();