using GabrielGateway.Models;
using GabrielGateway.Plugins;
using GabrielGateway.Security;

namespace GabrielGateway.Cli;

public class AdminCli
{
    private readonly PluginManager _pluginManager;
    private readonly AuthManager _authManager;
    private readonly Func<Task> _shutdownCallback;

    public AdminCli(PluginManager pluginManager, AuthManager authManager, Func<Task> shutdownCallback)
    {
        _pluginManager = pluginManager;
        _authManager = authManager;
        _shutdownCallback = shutdownCallback;
    }

    public async Task RunAsync()
    {
        Console.WriteLine("[Gabriel CLI] Type 'help' for administrative commands.\n");

        while (true)
        {
            Console.Write("gabriel> ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case "help":
                    Console.WriteLine("Available CLI Commands:");
                    Console.WriteLine("  status                         - Show server status and registered addons");
                    Console.WriteLine("  plugin list                    - View all active/inactive plugins");
                    Console.WriteLine("  plugin scan                    - Scan ./plugins folder for new DLL assemblies");
                    Console.WriteLine("  plugin install <url>           - Download and hot-load a plugin DLL from URL");
                    Console.WriteLine("  plugin toggle <id> <true|false>- Enable or disable a service");
                    Console.WriteLine("  plugin reload <id> [port]      - Hot reload service with optional port update");
                    Console.WriteLine("  user add <user> <pass> <role>  - Create Moderator or Admin");
                    Console.WriteLine("  user list                      - View all registered accounts");
                    Console.WriteLine("  exit                           - Gracefully shutdown the gateway");
                    break;

                case "status":
                case "plugin" when parts.Length > 1 && parts[1] == "list":
                    Console.WriteLine("\nRegistered Addons:");
                    foreach (var p in _pluginManager.GetAll())
                    {
                        Console.WriteLine($"  [{p.Config.Id}] {p.Config.Name,-20} ({p.Config.Type}) Port: {p.Config.TargetPort,-5} Enabled: {p.Config.IsEnabled}");
                    }
                    break;

                case "plugin" when parts.Length > 1 && parts[1] == "scan":
                    int count = _pluginManager.ScanAndLoadPluginsFromFolder("./plugins");
                    Console.WriteLine($"Scan complete. Activated {count} new plugin(s).");
                    break;

                case "plugin" when parts.Length >= 3 && parts[1] == "install":
                    string url = parts[2];
                    Console.WriteLine($"Downloading from {url}...");
                    var (success, message) = await _pluginManager.DownloadAndInstallPluginAsync(url);
                    Console.WriteLine(success ? $"Success: {message}" : $"Failed: {message}");
                    break;

                case "plugin" when parts.Length >= 4 && parts[1] == "toggle":
                    string toggleId = parts[2];
                    if (bool.TryParse(parts[3], out bool state))
                    {
                        bool ok = _pluginManager.SetPluginState(toggleId, state);
                        Console.WriteLine(ok ? $"Service {toggleId} set to: {state}" : "Plugin ID not found.");
                    }
                    break;

                case "plugin" when parts.Length >= 3 && parts[1] == "reload":
                    string reloadId = parts[2];
                    int? newPort = parts.Length >= 4 && int.TryParse(parts[3], out int np) ? np : null;
                    bool reloaded = await _pluginManager.HotReloadPluginAsync(reloadId, newPort);
                    Console.WriteLine(reloaded ? $"Service {reloadId} reloaded." : "Plugin ID not found.");
                    break;

                case "user" when parts.Length >= 5 && parts[1] == "add":
                    string uName = parts[2];
                    string uPass = parts[3];
                    if (Enum.TryParse<UserRole>(parts[4], true, out var role))
                    {
                        bool added = _authManager.CreateUser(uName, uPass, role);
                        Console.WriteLine(added ? $"Account '{uName}' [{role}] created." : "Username already exists.");
                    }
                    else
                    {
                        Console.WriteLine("Invalid role. Specify 'Admin' or 'Moderator'.");
                    }
                    break;

                case "user" when parts.Length > 1 && parts[1] == "list":
                    Console.WriteLine("\nRegistered Users:");
                    foreach (var u in _authManager.GetAllUsers())
                    {
                        Console.WriteLine($"  {u.Username,-15} Role: {u.Role,-10} Created: {u.CreatedAt}");
                    }
                    break;

                case "exit":
                case "quit":
                    await _shutdownCallback();
                    return;

                default:
                    Console.WriteLine("Unknown command. Type 'help' for instructions.");
                    break;
            }
        }
    }
}