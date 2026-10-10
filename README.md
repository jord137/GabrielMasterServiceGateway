<div align="center">

# 🛡️ Gabriel Master Service Gateway

![Alt text of the image](https://github.com/jord137/GabrielMasterServiceGateway/blob/master/Images/Gabriel.jpg)

**A lightweight, self-healing reverse proxy, static file server, and microservices orchestrator built on modern .NET.**

[![.NET Version](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](#prerequisites)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=for-the-badge)](#license)
[![Architecture](https://img.shields.io/badge/Architecture-Dual--Port%20Isolated-blue?style=for-the-badge)](#architecture-overview)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey?style=for-the-badge)](#installation-guide)

<p align="center">
  <a href="#overview">Overview</a> •
  <a href="#key-features">Key Features</a> •
  <a href="#architecture-overview">Architecture</a> •
  <a href="#installation-guide">Installation</a> •
  <a href="#how-to-guide">How-To Guide</a> •
  <a href="#plugin-sdk">Plugin SDK</a> •
  <a href="#frequently-asked-questions">FAQ</a>
</p>

---

</div>

## Overview

**Gabriel Master Service Gateway** is an extensible application server and reverse proxy designed to eliminate operational friction when hosting mixed-stack web environments. It physically isolates public web traffic from administrative endpoints across separate network ports, supports zero-downtime hot-reloading of external C# assemblies (`.dll`), and features a self-healing runtime provisioner that automatically detects, fetches, configures, and boots missing dependencies like PHP and MySQL/MariaDB.

---

## Key Features

* **Dual-Port Network Isolation:** Public client traffic (`:8080`) and sensitive admin consoles (`:9090`) execute on independent `HttpListener` loops.
* **Self-Healing Runtime Provisioner:** Detects missing PHP or MySQL binaries at boot, downloads portable runtimes automatically on Windows, initializes configuration files (`php.ini`, MySQL system databases), and spins up managed daemons.
* **Dynamic Plugin & DLL Engine:** Drop compiled C# assemblies into `./plugins` or install them live over HTTP/HTTPS from the Web Console or CLI without restarting the server.
* **Universal Reverse Proxy (`PROXY`):** Effortlessly route requests to any backend service (Node.js, Python Flask/FastAPI, Go, Rust, Ruby) with transparent header and payload forwarding.
* **Native Wire Protocol Sniffing:** Zero-dependency MySQL `HandshakeV10` inspection queries database instances directly over raw TCP sockets to report version strings, connection latency, and thread IDs.
* **Role-Based Access Control (RBAC):** Built-in user management with PBKDF2 password hashing (100,000 iterations, SHA-256), cryptographic constant-time token comparison, and granular Admin/Moderator permission enforcement.
* **Interactive Hybrid Management:** Manage your entire infrastructure through either the embedded Single Page Application (SPA) dashboard or the terminal REPL (`gabriel>`).

---

## Architecture Overview

                              +------------------------------------+
                              |   Gabriel Master Service Gateway   |
                              +------------------------------------+
                                          |            |
                  +-----------------------+            +-----------------------+
                  |                                                            |
                  v                                                            v
        [ Public Port: 8080 ]                                        [ Admin Port: 9090 ]
                  |                                                            |
    +-------------+-------------+                                +-------------+-------------+
    |                           |                                |                           |
    v                           v                                v                           v
Static Web Root (./wwwroot)   Plugin Pipeline                 Admin Single Page           Admin REST API
                  +-------------------------+                 Dashboard                   (Token-Auth)
| • PhpAddonPlugin        |   (Embedded UI)                       |
| • MySqlAddonPlugin      |                                       v
| • GenericProxyPlugin    |                                     • User Management
| • External Dynamic DLLs |                                     • Remote DLL Fetch
+-------------------------+                                     • Port Hot-Reload
             | 
             v
Upstream Services & Workers
(PHP-FPM, MySQL, Node, Python)


---

## Installation Guide

### Prerequisites

* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
* Any modern OS: **Windows 10/11 / Windows Server**, **Ubuntu/Debian/RHEL**, or **macOS 12+**

> [!NOTE]
> The automated runtime download engine currently targets Windows x64. On Linux or macOS, simply ensure `php` or `mariadb`/`mysql` are installed via your package manager (`apt`, `dnf`, or `brew`); Gabriel will discover them on your system `PATH` automatically.

---

### Step 1: Clone and Build
bash
# Clone the repository
git clone [https://github.com/your-username/GabrielMasterServiceGateway.git](https://github.com/your-username/GabrielMasterServiceGateway.git)
cd GabrielMasterServiceGateway

# Restore dependencies and compile in Release mode
dotnet build -c Release

---
### Step 2: Run the Server
Bash
# Run directly from the CLI
dotnet run -c Release
Upon launching, Gabriel initializes the directories, verifies local runtimes, and displays:

Plaintext
=================================================
         GABRIEL MASTER SERVICE GATEWAY          
=================================================
[Gateway] Public Server running on port 8080
[Admin UI] Management Dashboard running on port 9090

[Ready] Gateway listening at: http://localhost:8080
[Ready] Admin UI listening at: http://localhost:9090

[Gabriel CLI] Type 'help' for administrative commands.

gabriel>

Field,Default Value,Notes
Admin UI URL,http://localhost:9090,Accessible via web browser
Username,admin,Superadmin account
Password,GabrielAdmin2026!,Can be changed after boot
Role,Admin,"Full permissions (Users, DLLs, Proxies)"


### How-To Guide
1. Using the Web Administration Console
Navigate to http://localhost:9090 in your browser.

Enter the administrator credentials (admin / GabrielAdmin2026!) and click Sign In.

Control Active Services:

Toggle States: Click Disable or Enable next to any plugin to pause or resume routing without dropping connections.

Hot Reload Ports: Change the target port (e.g., set PHP from 9000 to 9054) and click Hot Reload. The mapping updates immediately without restarting the gateway.

Install Remote DLLs:

Enter a direct URL pointing to a compiled plugin assembly in the Download & Install External Plugin Assembly field:

Plaintext
[https://raw.githubusercontent.com/your-repo/plugins/main/CustomMetricsPlugin.dll](https://raw.githubusercontent.com/your-repo/plugins/main/CustomMetricsPlugin.dll)
Click Download & Hot Load. Gabriel will fetch the binary, verify the IGatewayPlugin interface, save it into ./plugins, and initialize it immediately.

2. Using the Interactive Terminal CLI
The host console provides a non-blocking interactive command loop (gabriel>):

Plaintext
gabriel> help
Available CLI Commands:
  status                         - Show server status and registered addons
  plugin list                    - View all active/inactive plugins
  plugin scan                    - Scan ./plugins folder for new DLL assemblies
  plugin install <url>           - Download and hot-load a plugin DLL from URL
  plugin toggle <id> <true|false>- Enable or disable a service
  plugin reload <id> [port]      - Hot reload service with optional port update
  user add <user> <pass> <role>  - Create Moderator or Admin
  user list                      - View all registered accounts
  exit                           - Gracefully shutdown the gateway
CLI Examples
Scan for manually dropped DLLs:

Plaintext
gabriel> plugin scan
Scan complete. Activated 2 new plugin(s).
Hot-reload a service port:

Plaintext
gabriel> plugin reload php01 9050
[Plugin Manager] Successfully reloaded 'PHP-FPM Engine' on port 9050.
Create a moderator account:

Plaintext
gabriel> user add dev_john SecurePass2026! Moderator
Account 'dev_john' [Moderator] created.
3. Routing Custom Backends (Node, Python, Go)
You do not need to write C# code to proxy incoming web paths to external services. Use the built-in GenericProxyPlugin:

Via Web Console:
Under Attach Any Service, input:

Name: Python FastAPI ML

Type: PROXY

Host: 127.0.0.1

Port: 8000

Route Prefix: /api/predict

Click Attach Service.

Behavior:
Any request hitting http://localhost:8080/api/predict/* will be transparently proxied to http://127.0.0.1:8000/api/predict/*, preserving all HTTP headers, methods, and JSON payloads.

Plugin SDK
Building external plugins for Gabriel Gateway is simple. Any .NET 8/9 class library that implements IGatewayPlugin can be dropped into ./plugins/ and loaded at runtime.

1. Create a Plugin Project

dotnet new classlib -n Gabriel.MetricsPlugin
cd Gabriel.MetricsPlugin

using System.Net;
using System.Text;
using GabrielGateway.Models;
using GabrielGateway.Plugins;

namespace Gabriel.MetricsPlugin;

public class MetricsPlugin : IGatewayPlugin
{
    public PluginConfig Config { get; set; } = new()
    {
        Id = "metrics01",
        Name = "Gateway Metrics",
        Type = "Metrics",
        TargetPort = 8080,
        RoutePrefix = "/metrics",
        IsEnabled = true
    };

    public Task InitializeAsync()
    {
        Console.WriteLine("[Plugin: Metrics] Initialized.");
        return Task.CompletedTask;
    }

    public Task HotReloadAsync() => Task.CompletedTask;

    public Task<bool> CanHandleAsync(HttpListenerRequest request)
    {
        if (!Config.IsEnabled) return Task.FromResult(false);
        string path = request.Url?.AbsolutePath ?? "";
        return Task.FromResult(path.StartsWith(Config.RoutePrefix, StringComparison.OrdinalIgnoreCase));
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        string telemetry = $"{{\"status\":\"healthy\",\"memory_bytes\":{GC.GetTotalMemory(false)}}}";
        byte[] payload = Encoding.UTF8.GetBytes(telemetry);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload);
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}


### 3. Compile and Deploy
Bash
dotnet build -c Release
Copy the compiled DLL from bin/Release/net8.0/Gabriel.MetricsPlugin.dll directly into the gateway's ./plugins/ directory, then run:

Plaintext
gabriel> plugin scan
Directory Structure
Plaintext
GabrielMasterServiceGateway/
├── Cli/
│   └── AdminCli.cs             # Interactive console shell (REPL)
├── Core/
│   ├── AdminServer.cs          # Port 9090 listener & embedded dashboard
│   └── GatewayServer.cs        # Port 8080 listener & static engine
├── Models/
│   └── CoreModels.cs           # User accounts, plugin models & settings
├── Plugins/
│   ├── GenericProxyPlugin.cs   # Universal HTTP reverse proxy
│   ├── MySqlAddonPlugin.cs     # HandshakeV10 packet sniffer & monitor
│   ├── PhpAddonPlugin.cs       # 3-tier self-healing PHP engine
│   └── PluginManager.cs        # Assembly discovery & reflection factory
├── Security/
│   └── AuthManager.cs          # PBKDF2 cryptography & session tokens
├── Services/
│   └── RuntimeProvisioner.cs   # Auto-fetcher for portable runtimes
├── plugins/                    # Watched folder for external .dll plugins
├── runtimes/                   # Auto-provisioned portable PHP & MariaDB binaries
├── wwwroot/                    # Public static files (HTML, CSS, JS)
└── Program.cs                  # Host bootstrapper & lifecycle hooks

Frequently Asked Questions
A: A 502 Bad Gateway indicates that the PHP worker daemon has not started and local CGI execution failed. Gabriel includes a self-healing fallback:

Ensure Config.AutoSpawnPhpDaemon = true in your configuration.

On Windows, Gabriel will automatically stream and configure a portable PHP instance into ./runtimes/php.

On Linux/macOS, install the PHP CLI via sudo apt install php-cli or brew install php.

A: You can change default ports in Program.cs before building:

C#
var settings = new GatewaySettings
{
    GatewayPort = 80,    // Standard HTTP
    AdminPort = 9443,    // Custom Management Port
    WebRoot = "./wwwroot"
};
Note: Binding to ports below 1024 on Linux/macOS requires elevated permissions (sudo or setcap).

A: Yes. Set settings.UseHttps = true; in Program.cs. When using HttpListener on Windows, bind your certificate thumbprint to the port via netsh:

DOS
netsh http add sslcert ipport=0.0.0.0:8080 certhash=YOUR_THUMBPRINT appid={YOUR-GUID}
A: Each request handled by a plugin is isolated within try-catch safety boundaries in GatewayServer.cs. If an unhandled exception occurs inside a third-party plugin:

The gateway catches the fault, logs the diagnostic error, and returns a formatted 500 Gateway Fault to the client.

The main HTTP listeners and other active plugins continue running without interruption.

Administrators can disable the faulty plugin via the web UI or CLI using plugin toggle <id> false.

A:

Moderators: Can view active services, inspect server metrics, view health endpoints, and hot-reload existing service mappings.

Admins: Full access, including adding/deleting user accounts, downloading new .dll assemblies, registering new proxy routes, and triggering remote server shutdowns.

Contributing
Fork the Project repository.

Create your Feature Branch (git checkout -b feature/AmazingFeature).

Commit your Changes (git commit -m 'Add AmazingFeature').

Push to the Branch (git push origin feature/AmazingFeature).

Open a Pull Request.

License
Distributed under the MIT License. See LICENSE for more information.




