using Npgsql;

namespace ITSupport.Api.Services;

// One row in the UI's "recent chats" sidebar.
public record ConversationSummaryDto(string Id, string Title, DateTime UpdatedAt);

// Repository for conversation persistence (Postgres). Owns all SQL for the
// conversations + messages tables so the rest of the app never touches it directly.
public class ConversationStore
{
    private readonly NpgsqlDataSource _db;

    public ConversationStore(NpgsqlDataSource db) => _db = db;

    // Make sure the conversation row exists (idempotent) and bump its updated_at.
    public async Task EnsureConversationAsync(string conversationId)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO conversations (id) VALUES ($1) " +
            "ON CONFLICT (id) DO UPDATE SET updated_at = now();");
        cmd.Parameters.AddWithValue(conversationId);
        await cmd.ExecuteNonQueryAsync();
    }

    // Append one message ('user' or 'assistant') to a conversation.
    public async Task AddMessageAsync(string conversationId, string role, string content)
    {
        await EnsureConversationAsync(conversationId);
        await using var cmd = _db.CreateCommand(
            "INSERT INTO messages (conversation_id, role, content) VALUES ($1, $2, $3);");
        cmd.Parameters.AddWithValue(conversationId);
        cmd.Parameters.AddWithValue(role);
        cmd.Parameters.AddWithValue(content);
        await cmd.ExecuteNonQueryAsync();
    }

    // Load the most recent `limit` messages, returned in chronological order
    // (oldest first) so they read naturally as conversation history.
    public async Task<List<ChatMessageDto>> GetRecentAsync(string conversationId, int limit = 10)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT role, content FROM (" +
            "  SELECT id, role, content FROM messages WHERE conversation_id = $1 " +
            "  ORDER BY id DESC LIMIT $2" +
            ") t ORDER BY id ASC;");
        cmd.Parameters.AddWithValue(conversationId);
        cmd.Parameters.AddWithValue(limit);

        var list = new List<ChatMessageDto>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new ChatMessageDto(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    // The running summary of older turns (filled in milestone 2b). Null if none yet.
    public async Task<string?> GetSummaryAsync(string conversationId)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT summary FROM conversations WHERE id = $1;");
        cmd.Parameters.AddWithValue(conversationId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    // Messages that are (a) not yet covered by the summary (id > summarized_upto) and
    // (b) OLDER than the live window of the most recent `keepLast` messages — i.e. the
    // turns that have fallen out of the window and would otherwise be forgotten.
    public async Task<(List<ChatMessageDto> Messages, long MaxId)> GetUnsummarizedOlderAsync(
        string conversationId, int keepLast = 10)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT id, role, content FROM messages " +
            "WHERE conversation_id = $1 " +
            "  AND id > (SELECT summarized_upto FROM conversations WHERE id = $1) " +
            "  AND id <= (SELECT COALESCE(MAX(id),0) - $2 FROM messages WHERE conversation_id = $1) " +
            "ORDER BY id ASC;");
        cmd.Parameters.AddWithValue(conversationId);
        cmd.Parameters.AddWithValue((long)keepLast);

        var list = new List<ChatMessageDto>();
        long maxId = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            maxId = reader.GetInt64(0);
            list.Add(new ChatMessageDto(reader.GetString(1), reader.GetString(2)));
        }
        return (list, maxId);
    }

    // The most recent conversations for the UI sidebar (capped — older chats stay in
    // the DB, they just age off the list). Title = the first user message of the chat.
    public async Task<List<ConversationSummaryDto>> GetRecentConversationsAsync(int limit = 10)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT c.id, " +
            "       COALESCE((SELECT m.content FROM messages m " +
            "                 WHERE m.conversation_id = c.id AND m.role = 'user' " +
            "                 ORDER BY m.id ASC LIMIT 1), '(empty chat)') AS title, " +
            "       c.updated_at " +
            "FROM conversations c ORDER BY c.updated_at DESC LIMIT $1;");
        cmd.Parameters.AddWithValue(limit);

        var list = new List<ConversationSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var title = reader.GetString(1);
            if (title.Length > 60) title = title[..60] + "…";
            list.Add(new ConversationSummaryDto(reader.GetString(0), title, reader.GetDateTime(2)));
        }
        return list;
    }

    // Full transcript of one conversation (for restoring a chat the user clicks on).
    public async Task<List<ChatMessageDto>> GetAllMessagesAsync(string conversationId, int limit = 200)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT role, content FROM messages WHERE conversation_id = $1 ORDER BY id ASC LIMIT $2;");
        cmd.Parameters.AddWithValue(conversationId);
        cmd.Parameters.AddWithValue(limit);

        var list = new List<ChatMessageDto>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new ChatMessageDto(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    // Store the new merged summary and advance the watermark so those messages are
    // never summarized twice.
    public async Task SaveSummaryAsync(string conversationId, string summary, long summarizedUpto)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE conversations SET summary = $2, summarized_upto = $3, updated_at = now() " +
            "WHERE id = $1;");
        cmd.Parameters.AddWithValue(conversationId);
        cmd.Parameters.AddWithValue(summary);
        cmd.Parameters.AddWithValue(summarizedUpto);
        await cmd.ExecuteNonQueryAsync();
    }
}
