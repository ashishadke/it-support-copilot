using System.ComponentModel;
using ModelContextProtocol.Server;
using Npgsql;

// Helpdesk ticket tools backed by the ITSupportDb 'tickets' table.
[McpServerToolType]
public static class TicketTools
{
    // The API passes the real connection string via the ITSUPPORT_DB env var when it
    // launches this server. Falls back to the local default for standalone runs.
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ITSUPPORT_DB")
        ?? "Host=localhost;Port=5432;Database=ITSupportDb;Username=postgres;Password=postgres";

    // READ-ONLY tool: safe to run immediately.
    [McpServerTool, Description("Gets the title and status of a support ticket by its numeric id.")]
    public static async Task<string> GetTicketStatus(
        [Description("The numeric ticket id, e.g. 1")] int ticketId)
    {
        // stderr is SAFE here (stdout is the MCP protocol channel). This lets us
        // watch the tool actually fire inside the MCP server process.
        Console.Error.WriteLine($"[TOOL] GetTicketStatus called with ticketId={ticketId}");

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT title, status FROM tickets WHERE id = $1;", conn);
        cmd.Parameters.AddWithValue(ticketId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return $"Ticket #{ticketId}: \"{reader.GetString(0)}\" — status: {reader.GetString(1)}";
        return $"No ticket found with id {ticketId}.";
    }

    // READ-ONLY tool: list/count tickets. Answers "how many tickets are pending",
    // "list open tickets", "what tickets are there", etc.
    [McpServerTool, Description(
        "Lists support tickets with their status. Optionally filter by an exact status " +
        "(valid values: 'Open', 'In Progress', 'Resolved'). For vague questions like " +
        "'how many are pending/unresolved', call with NO status and count the results yourself " +
        "(pending usually means Open or In Progress).")]
    public static async Task<string> ListTickets(
        [Description("Optional exact status filter, e.g. 'Open'. Leave empty to list ALL tickets.")] string? status = null)
    {
        Console.Error.WriteLine($"[TOOL] ListTickets called with status='{status}'");

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = "SELECT id, title, status FROM tickets";
        if (!string.IsNullOrWhiteSpace(status)) sql += " WHERE lower(status) = lower($1)";
        sql += " ORDER BY id;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(status)) cmd.Parameters.AddWithValue(status);

        await using var reader = await cmd.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add($"#{reader.GetInt32(0)}: \"{reader.GetString(1)}\" — {reader.GetString(2)}");

        if (lines.Count == 0)
            return string.IsNullOrWhiteSpace(status)
                ? "There are no tickets."
                : $"There are no tickets with status '{status}'.";

        return $"{lines.Count} ticket(s):\n" + string.Join("\n", lines);
    }

    // WRITE tool with built-in human-approval gating:
    //   confirmed = false  -> PREVIEW only, no row is created.
    //   confirmed = true   -> actually inserts the ticket.
    [McpServerTool, Description(
        "Creates a support ticket. IMPORTANT: this changes data. First call with confirmed=false to " +
        "PREVIEW the ticket and ask the user to confirm. Only call with confirmed=true after the user " +
        "has explicitly confirmed.")]
    public static async Task<string> CreateTicket(
        [Description("Short ticket title")] string title,
        [Description("Detailed description of the issue")] string description,
        [Description("Must be true to actually create the ticket; use false to preview first")] bool confirmed = false)
    {
        Console.Error.WriteLine($"[TOOL] CreateTicket called: title='{title}', confirmed={confirmed}");

        if (!confirmed)
            return $"PREVIEW (NOT yet created): title='{title}', description='{description}'. " +
                   "Tell the user these details and ask them to confirm before creating.";

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO tickets (title, description, status) VALUES ($1, $2, 'Open') RETURNING id;", conn);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(description);
        var id = (int)(await cmd.ExecuteScalarAsync())!;
        return $"Created ticket #{id}: \"{title}\" (status: Open).";
    }
}
