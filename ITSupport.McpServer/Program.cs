using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// MCP over stdio uses stdout for protocol messages, so logs must NOT go there
// (anything written to stdout would corrupt the JSON-RPC channel).
builder.Logging.ClearProviders();

// Register an MCP server that:
//   - talks over stdio (standard input/output)
//   - auto-discovers every [McpServerTool] method in this assembly
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
