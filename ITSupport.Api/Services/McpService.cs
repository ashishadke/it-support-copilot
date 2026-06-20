using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace ITSupport.Api.Services;

// Connects to our ITSupport.McpServer (launched as a subprocess over stdio),
// discovers its tools, and exposes them so the agent's LLM can call them.
//
// Each McpClientTool IS an AIFunction (Microsoft.Extensions.AI), so the list
// drops straight into ChatOptions.Tools — the same mechanism IChatClient uses.
public class McpService
{
    private McpClient? _client;

    public IList<McpClientTool> Tools { get; private set; } = new List<McpClientTool>();

    public async Task InitializeAsync(string? serverPath = null, string? connectionString = null)
    {
        // Trace-level logging just for the MCP SDK: it logs every JSON-RPC
        // message it sends/receives over stdio, so we can watch the raw traffic.
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddFilter("ModelContextProtocol", LogLevel.Trace);  // raw JSON-RPC
            b.SetMinimumLevel(LogLevel.Warning);                  // keep everything else quiet
        });

        // Resolve the MCP server executable. Prefer an explicit config path; otherwise
        // find it relative to THIS app's output folder so the repo runs on any machine:
        //   <repo>/ITSupport.Api/bin/<Config>/net10.0/   ->  ../../../../ITSupport.McpServer/...
        var command = string.IsNullOrWhiteSpace(serverPath)
            ? ResolveDefaultServerPath()
            : serverPath;

        // Pass the DB connection string down to the server process as an env var so
        // both processes use the same database without duplicating it in source.
        var env = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(connectionString))
            env["ITSUPPORT_DB"] = connectionString;

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ITSupportMcp",
            Command = command,
            Arguments = [],
            EnvironmentVariables = env
        }, loggerFactory);

        _client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory);
        Tools = await _client.ListToolsAsync();

        Console.WriteLine($"[MCP] Connected. Discovered {Tools.Count} tool(s): " +
                          string.Join(", ", Tools.Select(t => t.Name)));
    }

    // Find ITSupport.McpServer's build output relative to this app, mirroring the
    // same build configuration (Debug/Release) and target framework folder.
    private static string ResolveDefaultServerPath()
    {
        // AppContext.BaseDirectory = <repo>/ITSupport.Api/bin/<Config>/<tfm>/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm    = baseDir.Name;                      // e.g. net10.0
        var config = baseDir.Parent!.Name;             // e.g. Debug
        var repoRoot = baseDir.Parent!.Parent!.Parent!.Parent!.FullName;  // up 4: tfm,Config,bin,Api

        var exeName = OperatingSystem.IsWindows()
            ? "ITSupport.McpServer.exe"
            : "ITSupport.McpServer";

        return Path.Combine(repoRoot, "ITSupport.McpServer", "bin", config, tfm, exeName);
    }
}
