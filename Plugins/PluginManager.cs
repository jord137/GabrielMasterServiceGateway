using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using GabrielGateway.Models;

namespace GabrielGateway.Plugins;

public interface IGatewayPlugin : IAsyncDisposable
{
    PluginConfig Config { get; }
    Task InitializeAsync();
    Task HotReloadAsync();
    Task<bool> CanHandleAsync(HttpListenerRequest request);
    Task HandleAsync(HttpListenerContext context);
    Task ShutdownAsync();

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await ShutdownAsync();
        GC.SuppressFinalize(this);
    }
}

public class PluginManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IGatewayPlugin> _activePlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Type> _discoveredPluginTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Assembly> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public PluginManager()
    {
        // Register standard core types in the factory catalog
        _discoveredPluginTypes["PHP"] = typeof(PhpAddonPlugin);
        _discoveredPluginTypes["MYSQL"] = typeof(MySqlAddonPlugin);
        _discoveredPluginTypes["PROXY"] = typeof(GenericProxyPlugin);
    }

    public IEnumerable<IGatewayPlugin> GetAll() => _activePlugins.Values;

    public IEnumerable<string> GetAvailablePluginTypes() => _discoveredPluginTypes.Keys;

    public bool RegisterPlugin(IGatewayPlugin plugin)
    {
        if (plugin?.Config == null || string.IsNullOrWhiteSpace(plugin.Config.Id))
            return false;

        bool added = _activePlugins.TryAdd(plugin.Config.Id, plugin);
        if (added)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await plugin.InitializeAsync();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Plugin Manager] Failed to initialize '{plugin.Config.Name}': {ex.Message}");
                    Console.ResetColor();
                }
            });
        }
        return added;
    }

    /// <summary>
    /// Factory creating any plugin type dynamically, whether built-in or loaded from an external DLL.
    /// </summary>
    public IGatewayPlugin CreatePluginInstance(PluginConfig config, string webRoot)
    {
        string typeKey = config.Type.Trim();

        if (_discoveredPluginTypes.TryGetValue(typeKey, out var pluginType))
        {
            try
            {
                // 1. Try constructor: (PluginConfig config, string webRoot)
                var ctorWithWebRoot = pluginType.GetConstructor(new[] { typeof(PluginConfig), typeof(string) });
                if (ctorWithWebRoot != null)
                {
                    return (IGatewayPlugin)ctorWithWebRoot.Invoke(new object[] { config, webRoot });
                }

                // 2. Try constructor: (PluginConfig config)
                var ctorWithConfig = pluginType.GetConstructor(new[] { typeof(PluginConfig) });
                if (ctorWithConfig != null)
                {
                    return (IGatewayPlugin)ctorWithConfig.Invoke(new object[] { config });
                }

                // 3. Try parameterless constructor: ()
                var emptyCtor = pluginType.GetConstructor(Type.EmptyTypes);
                if (emptyCtor != null)
                {
                    var instance = (IGatewayPlugin)emptyCtor.Invoke(null);
                    if (instance.Config != null)
                    {
                        if (!string.IsNullOrWhiteSpace(config.Id)) instance.Config.Id = config.Id;
                        if (!string.IsNullOrWhiteSpace(config.Name)) instance.Config.Name = config.Name;
                        if (config.TargetPort > 0) instance.Config.TargetPort = config.TargetPort;
                        instance.Config.Host = config.Host;
                        instance.Config.RoutePrefix = config.RoutePrefix;
                        instance.Config.IsEnabled = config.IsEnabled;
                    }
                    return instance;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plugin Factory] Could not instantiate type '{pluginType.FullName}': {ex.Message}. Falling back to proxy.");
            }
        }

        // Universal fallback for any custom service
        return new GenericProxyPlugin(config);
    }

    public bool SetPluginState(string id, bool isEnabled)
    {
        if (_activePlugins.TryGetValue(id, out var plugin))
        {
            plugin.Config.IsEnabled = isEnabled;
            Console.WriteLine($"[Plugin Manager] Service '{plugin.Config.Name}' is now {(isEnabled ? "ENABLED" : "DISABLED")}.");
            return true;
        }
        return false;
    }

    public async Task<bool> HotReloadPluginAsync(string id, int? newPort = null)
    {
        if (!_activePlugins.TryGetValue(id, out var plugin))
            return false;

        try
        {
            if (newPort.HasValue && newPort.Value > 0)
            {
                plugin.Config.TargetPort = newPort.Value;
            }

            await plugin.HotReloadAsync();
            Console.WriteLine($"[Plugin Manager] Hot reloaded '{plugin.Config.Name}' on port {plugin.Config.TargetPort}.");
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Plugin Manager] Hot reload failed for '{plugin.Config.Name}': {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    public async Task<bool> UnregisterPluginAsync(string id)
    {
        if (_activePlugins.TryRemove(id, out var plugin))
        {
            try
            {
                await plugin.ShutdownAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plugin Manager] Shutdown error for '{plugin.Config.Name}': {ex.Message}");
            }
        }
        return false;
    }

    public async Task<IGatewayPlugin?> MatchPluginAsync(HttpListenerRequest request)
    {
        foreach (var plugin in _activePlugins.Values)
        {
            try
            {
                if (plugin.Config.IsEnabled && await plugin.CanHandleAsync(request))
                {
                    return plugin;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Route Match Error] {plugin.Config.Name}: {ex.Message}");
            }
        }
        return null;
    }

    // --- Dynamic External DLL Discovery ---

    public int ScanAndLoadPluginsFromFolder(string folderPath = "./plugins")
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            return 0;
        }

        int loadedCount = 0;
        string[] dllFiles = Directory.GetFiles(folderPath, "*.dll", SearchOption.TopDirectoryOnly);

        foreach (var dllPath in dllFiles)
        {
            try
            {
                loadedCount += LoadPluginAssembly(dllPath);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Dynamic Loader] Failed to load assembly '{Path.GetFileName(dllPath)}': {ex.Message}");
                Console.ResetColor();
            }
        }

        return loadedCount;
    }

    public int LoadPluginAssembly(string assemblyPath)
    {
        string fullPath = Path.GetFullPath(assemblyPath);
        if (_loadedAssemblies.ContainsKey(fullPath))
            return 0;

        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        _loadedAssemblies[fullPath] = assembly;

        int registered = 0;
        Type[] exportedTypes;

        try
        {
            exportedTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            exportedTypes = ex.Types.Where(t => t != null)!.ToArray()!;
        }

        var pluginTypes = exportedTypes.Where(t =>
            typeof(IGatewayPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in pluginTypes)
        {
            // Register in the dynamic type registry so users can pick it from Web UI & CLI
            _discoveredPluginTypes[type.Name] = type;
            Console.WriteLine($"[Dynamic Loader] Registered plugin type: '{type.Name}' from {Path.GetFileName(fullPath)}");

            // If it provides a parameterless constructor, automatically instantiate an initial instance
            var defaultCtor = type.GetConstructor(Type.EmptyTypes);
            if (defaultCtor != null)
            {
                try
                {
                    var instance = (IGatewayPlugin)defaultCtor.Invoke(null);
                    RegisterPlugin(instance);
                    registered++;
                    Console.WriteLine($"[Dynamic Loader] Auto-activated default instance: '{instance.Config.Name}' [{instance.Config.Id}]");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Dynamic Loader] Auto-activation skipped for {type.Name}: {ex.Message}");
                }
            }
            else
            {
                registered++;
            }
        }

        return registered;
    }

    public async Task<(bool Success, string Message)> DownloadAndInstallPluginAsync(string downloadUrl, string targetFolder = "./plugins")
    {
        try
        {
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
                return (false, "Invalid URL format.");

            string fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                fileName = $"plugin_{Guid.NewGuid():N}[..8].dll";
            }

            Directory.CreateDirectory(targetFolder);
            string destinationPath = Path.Combine(targetFolder, fileName);

            Console.WriteLine($"[Plugin Installer] Downloading: {downloadUrl}");
            byte[] fileBytes = await _httpClient.GetByteArrayAsync(uri);
            await File.WriteAllBytesAsync(destinationPath, fileBytes);

            int loaded = LoadPluginAssembly(destinationPath);
            if (loaded > 0)
            {
                return (true, $"Installed and activated {loaded} service/type(s) from {fileName}.");
            }

            return (false, $"Downloaded {fileName}, but no valid IGatewayPlugin types were found inside.");
        }
        catch (Exception ex)
        {
            return (false, $"Install Error: {ex.Message}");
        }
    }

    public async Task ShutdownAllAsync()
    {
        Console.WriteLine("[Plugin Manager] Terminating all registered services...");
        foreach (var plugin in _activePlugins.Values)
        {
            try
            {
                await plugin.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Shutdown Error] {plugin.Config.Name}: {ex.Message}");
            }
        }
        _activePlugins.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAllAsync();
        GC.SuppressFinalize(this);
    }
}