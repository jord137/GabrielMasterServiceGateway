using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace GabrielGateway.Services;

public static class RuntimeProvisioner
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly string BaseRuntimeDir = Path.GetFullPath("./runtimes");

    // Official portable Windows distributions
    private const string PhpWinUrl = "https://windows.php.net/downloads/releases/archives/php-8.2.20-Win32-vs16-x64.zip";
    private const string MariaDbWinUrl = "https://archive.mariadb.org/mariadb-10.11.8/winx64-packages/mariadb-10.11.8-winx64.zip";

    /// <summary>
    /// Ensures a working PHP binary exists. Checks system PATH first, then local ./runtimes/php, then auto-downloads.
    /// </summary>
    public static async Task<string> EnsurePhpAsync()
    {
        string binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "php.exe" : "php";

        // 1. Check if available globally in PATH
        if (IsBinaryOnPath(binaryName))
        {
            return binaryName;
        }

        // 2. Check if already auto-downloaded locally
        string localPhpDir = Path.Combine(BaseRuntimeDir, "php");
        string localPhpExe = Path.Combine(localPhpDir, binaryName);
        if (File.Exists(localPhpExe))
        {
            return localPhpExe;
        }

        // 3. Auto-download portable PHP
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[Provisioner] 'php' not detected on system PATH.");
            Console.WriteLine("[Provisioner] Auto-downloading portable PHP runtime for Windows...");
            Console.ResetColor();

            Directory.CreateDirectory(localPhpDir);
            string zipPath = Path.Combine(BaseRuntimeDir, "php_temp.zip");

            await DownloadFileWithProgressAsync(PhpWinUrl, zipPath);

            Console.WriteLine("[Provisioner] Extracting PHP runtime to ./runtimes/php...");
            ZipFile.ExtractToDirectory(zipPath, localPhpDir, overwriteFiles: true);
            File.Delete(zipPath);

            // Configure basic php.ini
            SetupPhpIni(localPhpDir);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Provisioner] Portable PHP installed successfully: {localPhpExe}");
            Console.ResetColor();

            return localPhpExe;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Provisioner] Linux/macOS detected. Please run: 'sudo apt install php-cli' or 'brew install php'");
            Console.ResetColor();
            return binaryName;
        }
    }

    /// <summary>
    /// Ensures a working MySQL/MariaDB server binary exists locally or on PATH.
    /// </summary>
    public static async Task<string> EnsureMySqlAsync(int targetPort)
    {
        string binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mysqld.exe" : "mysqld";

        if (IsBinaryOnPath(binaryName))
        {
            return binaryName;
        }

        string localMySqlDir = Path.Combine(BaseRuntimeDir, "mysql");
        string localMySqlExe = Path.Combine(localMySqlDir, "bin", binaryName);
        if (File.Exists(localMySqlExe))
        {
            return localMySqlExe;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[Provisioner] MySQL/MariaDB not detected on host.");
            Console.WriteLine("[Provisioner] Auto-downloading portable MariaDB runtime...");
            Console.ResetColor();

            Directory.CreateDirectory(localMySqlDir);
            string zipPath = Path.Combine(BaseRuntimeDir, "mariadb_temp.zip");

            await DownloadFileWithProgressAsync(MariaDbWinUrl, zipPath);

            Console.WriteLine("[Provisioner] Extracting database server files...");
            string extractTemp = Path.Combine(BaseRuntimeDir, "mariadb_extracted");
            ZipFile.ExtractToDirectory(zipPath, extractTemp, overwriteFiles: true);
            File.Delete(zipPath);

            // Move contents of nested extraction folder into ./runtimes/mysql
            string nestedFolder = Directory.GetDirectories(extractTemp).FirstOrDefault() ?? extractTemp;
            foreach (var dir in Directory.GetDirectories(nestedFolder))
            {
                string dest = Path.Combine(localMySqlDir, Path.GetFileName(dir));
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(dir, dest);
            }
            foreach (var file in Directory.GetFiles(nestedFolder))
            {
                string dest = Path.Combine(localMySqlDir, Path.GetFileName(file));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
            Directory.Delete(extractTemp, true);

            // Run mysql_install_db.exe to build system database tables
            InitializeMySqlDataDir(localMySqlDir, targetPort);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Provisioner] MySQL runtime installed successfully: {localMySqlExe}");
            Console.ResetColor();

            return localMySqlExe;
        }

        return binaryName;
    }

    private static void SetupPhpIni(string phpDir)
    {
        string iniPath = Path.Combine(phpDir, "php.ini");
        string templatePath = Path.Combine(phpDir, "php.ini-development");

        string content = File.Exists(templatePath)
            ? File.ReadAllText(templatePath)
            : "[PHP]\n";

        // Enable common required extensions
        string customConfig = @"
extension_dir = ""ext""
extension=curl
extension=mbstring
extension=openssl
extension=mysqli
extension=pdo_mysql
memory_limit = 256M
max_execution_time = 300
display_errors = On
";
        File.WriteAllText(iniPath, content + customConfig);
    }

    private static void InitializeMySqlDataDir(string mysqlDir, int port)
    {
        string installDbExe = Path.Combine(mysqlDir, "bin", "mysql_install_db.exe");
        string dataDir = Path.Combine(mysqlDir, "data");

        if (!Directory.Exists(dataDir) && File.Exists(installDbExe))
        {
            Console.WriteLine("[Provisioner] Initializing MySQL system database catalog...");
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = installDbExe,
                Arguments = $"--datadir=\"{dataDir}\" --port={port}",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(30000);
        }
    }

    private static bool IsBinaryOnPath(string binaryName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = binaryName,
                Arguments = "-v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            proc?.WaitForExit(1000);
            return proc != null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DownloadFileWithProgressAsync(string url, string destinationPath)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        byte[] buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;
        int lastPercent = 0;

        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalRead += bytesRead;

            if (totalBytes.HasValue)
            {
                int currentPercent = (int)((totalRead * 100) / totalBytes.Value);
                if (currentPercent >= lastPercent + 10)
                {
                    Console.Write($"\r[Download Progress] {currentPercent}% ({totalRead / (1024 * 1024)} MB)");
                    lastPercent = currentPercent;
                }
            }
        }
        Console.WriteLine("\n[Download] Complete.");
    }
}