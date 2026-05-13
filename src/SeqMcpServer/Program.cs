using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeqMcpServer.Services;
using Serilog;
using System.Reflection;

const string DefaultProfile = "imageangel";

// Parse command-line arguments. We extract our own options (--version, --help,
// --profile) and forward anything else through to the host builder so that
// existing configuration-style switches (--Some:Key=value) still work.
string? profileName = null;
var forwardedArgs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    var lower = arg.ToLowerInvariant();

    if (lower == "--version" || lower == "-v")
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var version = assemblyVersion != null
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : "0.0.0";
        Console.WriteLine($"seq-mcp-server {version}");
        return 0;
    }
    else if (lower == "--help" || lower == "-h" || lower == "-?")
    {
        Console.WriteLine("Seq MCP Server - Query Seq logs via Model Context Protocol");
        Console.WriteLine();
        Console.WriteLine("Usage: seq-mcp-server [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --version, -v       Show version information");
        Console.WriteLine("  --help, -h          Show this help message");
        Console.WriteLine($"  --profile <name>    Select a Seq profile defined in configuration (default: {DefaultProfile})");
        Console.WriteLine();
        Console.WriteLine("Configuration files (loaded in this order, last wins):");
        Console.WriteLine("  <toolDir>/appsettings.json                 (packaged with the tool, optional)");
        Console.WriteLine("  %USERPROFILE%\\.seqmcpserver\\appsettings.json (per-user overrides, optional)");
        Console.WriteLine();
        Console.WriteLine("Environment Variables:");
        Console.WriteLine("  SEQ_API_KEY         API key for accessing Seq (required)");
        Console.WriteLine("  SEQ_SERVER_URL      Override the profile's ServerUrl (optional)");
        Console.WriteLine();
        Console.WriteLine("Profiles are defined under the 'SeqProfiles' configuration section, e.g.:");
        Console.WriteLine("  {\"SeqProfiles\": {\"imageangel\": {\"ServerUrl\": \"https://seq.imageangel.co.uk\"}}}");
        return 0;
    }
    else if (lower == "--profile")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("Error: --profile requires a value (e.g. --profile imageangel).");
            return 1;
        }
        profileName = args[++i];
    }
    else if (lower.StartsWith("--profile=", StringComparison.Ordinal))
    {
        profileName = arg["--profile=".Length..];
    }
    else
    {
        // Anything else gets forwarded so host configuration overrides still work.
        forwardedArgs.Add(arg);
    }
}

profileName ??= DefaultProfile;

if (string.IsNullOrWhiteSpace(profileName))
{
    throw new InvalidOperationException("Profile name cannot be empty.");
}

// Load environment variables from .env file if it exists
// Try multiple strategies to find the .env file
string? envPath = null;

// Strategy 1: Check current directory
if (File.Exists(".env"))
{
    envPath = Path.GetFullPath(".env");
}
// Strategy 2: Walk up from current directory to find .env
else
{
    var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (currentDir != null && envPath == null)
    {
        var possibleEnvPath = Path.Combine(currentDir.FullName, ".env");
        if (File.Exists(possibleEnvPath))
        {
            envPath = possibleEnvPath;
            break;
        }

        // Stop if we find a .sln file (we've reached the solution root)
        if (currentDir.GetFiles("*.sln").Length != 0)
        {
            break;
        }

        currentDir = currentDir.Parent;
    }
}

// Strategy 3: Check common locations relative to the executable
if (envPath == null)
{
    var baseDir = AppContext.BaseDirectory;
    var possiblePaths = new[]
    {
        Path.Combine(baseDir, ".env"),
        Path.Combine(baseDir, "..", "..", "..", ".env"),
        Path.Combine(baseDir, "..", "..", "..", "..", ".env"),
        Path.Combine(baseDir, "..", "..", "..", "..", "..", ".env")
    };

    foreach (var path in possiblePaths)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            envPath = fullPath;
            break;
        }
    }
}

// Load the .env file if found
if (envPath != null && File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && line.Contains('='))
        {
            var parts = line.Split('=', 2);
            Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
        }
    }
}

// Create host builder for the MCP server
var builder = Host.CreateApplicationBuilder(forwardedArgs.ToArray());

// Replace the default configuration sources with a deterministic layout:
//   1. <toolDir>/appsettings.json  (packaged with the tool, optional)
//   2. %USERPROFILE%/.seqmcpserver/appsettings.json  (per-user overrides, optional)
//   3. Environment variables
//   4. Command-line arguments
// The default Host builder bases its config on Directory.GetCurrentDirectory(),
// which is wrong for a dotnet global tool: the cwd is wherever the user
// invoked the tool from, not where the tool actually lives.
var userConfigDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".seqmcpserver");
var userConfigPath = Path.Combine(userConfigDir, "appsettings.json");

builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(userConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(forwardedArgs.ToArray());

// Clear all default logging providers to prevent console output
// MCP servers must not write to stdout/stderr as it interferes with JSON-RPC communication
builder.Logging.ClearProviders();

// Add filter for MCP-specific events as recommended by Microsoft
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);

// Resolve the selected Seq profile from configuration.
var profileSection = builder.Configuration.GetSection($"SeqProfiles:{profileName}");
if (!profileSection.Exists())
{
    throw new InvalidOperationException(
        $"Seq profile '{profileName}' was not found in configuration. " +
        $"Define it under 'SeqProfiles:{profileName}' in '{userConfigPath}' " +
        $"or in the packaged appsettings.json next to the tool ('{Path.Combine(AppContext.BaseDirectory, "appsettings.json")}').");
}

var profileServerUrl = profileSection["ServerUrl"];
if (string.IsNullOrWhiteSpace(profileServerUrl))
{
    throw new InvalidOperationException(
        $"Seq profile '{profileName}' is missing the required 'ServerUrl' setting. " +
        $"Set 'SeqProfiles:{profileName}:ServerUrl' in your configuration.");
}

// Allow the SEQ_SERVER_URL environment variable to override the profile's URL.
// Useful for tests and one-off invocations where you don't want to edit a
// config file. The profile must still exist and be valid (we already checked).
var seqServerUrl = Environment.GetEnvironmentVariable("SEQ_SERVER_URL");
if (string.IsNullOrWhiteSpace(seqServerUrl))
{
    seqServerUrl = profileServerUrl;
}

// Surface the resolved values via well-known keys so downstream services
// (SeqConnectionFactory et al.) and Serilog can read them without knowing
// about profiles.
builder.Configuration["Seq:ServerUrl"] = seqServerUrl;
builder.Configuration["Seq:Profile"] = profileName;

var seqApiKey = Environment.GetEnvironmentVariable("SEQ_API_KEY");

// Validate API key is provided
if (string.IsNullOrEmpty(seqApiKey))
{
    throw new InvalidOperationException("SEQ_API_KEY environment variable must be set");
}

// Configure Serilog with enhanced context for MCP operations
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("ModelContextProtocol", Serilog.Events.LogEventLevel.Debug)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "SeqMcpServer")
    .Enrich.WithProperty("SeqProfile", profileName)
    .WriteTo.Seq(seqServerUrl, apiKey: seqApiKey)
    .CreateLogger();

builder.Logging.AddSerilog();
// Register services
builder.Services.AddSingleton<ICredentialStore, EnvironmentCredentialStore>();
builder.Services.AddSingleton<SeqConnectionFactory>();

// Configure MCP server with stdio transport
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Seq version validation on startup (without logging since we can't write to console)
var minVer = Version.Parse(builder.Configuration["SeqVersion:Min"] ?? "2024.1");
var maxVer = Version.Parse(builder.Configuration["SeqVersion:Max"] ?? "2025.3");

var logger = host.Services.GetService<ILogger<Program>>();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(async () =>
{
    logger?.LogInformation("Seq MCP Server started using profile {Profile} -> {ServerUrl}",
        profileName, seqServerUrl);

    try
    {
        var conn = host.Services.GetRequiredService<SeqConnectionFactory>().Create();
        var root = await conn.Client.GetRootAsync();
        var ver = Version.Parse(root.Version);

        logger?.LogInformation("Connected to Seq version {Version}", ver);

        if (ver < minVer || ver > maxVer)
        {
            logger?.LogWarning("Seq version {Version} is outside supported range {MinVersion}-{MaxVersion}",
                ver, minVer, maxVer);
        }
    }
    catch (Exception ex)
    {
        logger?.LogError(ex, "Failed to connect to Seq server");
    }
});

try
{
    // Run the MCP server
    await host.RunAsync();
    return 0;
}
finally
{
    // Ensure all logs are flushed before exit
    await Log.CloseAndFlushAsync();
}

public partial class Program { }
