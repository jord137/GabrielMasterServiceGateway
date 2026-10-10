using System.Text.Json.Serialization;

namespace GabrielGateway.Models;

public class PluginConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int TargetPort { get; set; } = 9000;
    public string Host { get; set; } = "127.0.0.1";
    public bool IsEnabled { get; set; } = true;
    public string RoutePrefix { get; set; } = "/";

    // Self-healing PHP configurations
    public string PhpExecutablePath { get; set; } = "php"; // Path or name in system PATH
    public bool EnableLocalFallback { get; set; } = true;
    public bool AutoSpawnPhpDaemon { get; set; } = true;
}
public enum UserRole
{
    Admin,
    Moderator
}


public class UserAccount
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UserRole Role { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}



public class GatewaySettings
{
    public int GatewayPort { get; set; } = 8080;
    public int AdminPort { get; set; } = 9090;
    public bool UseHttps { get; set; } = false;
    public string WebRoot { get; set; } = "./wwwroot";
}