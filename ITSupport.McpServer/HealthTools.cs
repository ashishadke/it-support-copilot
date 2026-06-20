using System.ComponentModel;
using ModelContextProtocol.Server;

// [McpServerToolType] marks this class as containing MCP tools that the server
// will expose to any connected client.
[McpServerToolType]
public static class HealthTools
{
    // [McpServerTool] exposes this method as a callable tool. The [Description]
    // attributes become the tool's docs that a client's LLM reads to decide
    // when to call it and with what arguments.
    [McpServerTool, Description("Checks whether a named IT system or service is currently up or down.")]
    public static string CheckSystemHealth(
        [Description("The system to check, e.g. 'build server', 'vpn', 'email'")] string system)
    {
        // Mock status (a real version would query a monitoring API or database).
        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["build server"] = "DOWN (under maintenance until 6 PM IST)",
            ["vpn"]          = "UP",
            ["email"]        = "UP",
            ["wiki"]         = "DEGRADED (slow response times)"
        };

        return statuses.TryGetValue(system, out var status)
            ? $"{system}: {status}"
            : $"{system}: UP (no incidents reported)";
    }
}
