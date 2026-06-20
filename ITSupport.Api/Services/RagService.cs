using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.AI;
using Npgsql;
using Pgvector;
using System.Net.WebSockets;

namespace ITSupport.Api.Services;

// Result types returned to the API caller.
public record Citation(string FileName, int ChunkIndex, double Score);
public record ChatAnswer(string Answer, IReadOnlyList<Citation> Sources, string Route = "rag", bool Grounded = true);
// One prior turn of the conversation, sent from the UI so the agent has context
// (needed so a "confirm" message knows which action it's confirming).
public record ChatMessageDto(string Role, string Text);
// The heart of Phase 1: ingest documents into pgvector, and answer questions
// grounded in the retrieved chunks (with citations). Same pipeline as the console
// app, now a reusable service injected into the Web API.
public class RagService
{
    private readonly NpgsqlDataSource _db;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IChatClient _chat;
    private readonly McpService _mcp;

    public RagService(
        NpgsqlDataSource db,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IChatClient chat,
        McpService mcp)
    {
        _db = db;
        _embedder = embedder;
        _chat = chat;
        _mcp = mcp;
    }

    // INGEST: chunk -> embed -> store each chunk. Re-uploading the same file name
    // replaces its old chunks so we don't get duplicates.
    public async Task<int> IngestAsync(string fileName, string text)
    {
        await using (var del = _db.CreateCommand("DELETE FROM doc_chunks WHERE file_name = $1;"))
        {
            del.Parameters.AddWithValue(fileName);
            await del.ExecuteNonQueryAsync();
        }

        var chunks = Chunker.Split(text);

        for (int i = 0; i < chunks.Count; i++)
        {
            var emb = await _embedder.GenerateAsync([chunks[i]]);
            await using var cmd = _db.CreateCommand(
                "INSERT INTO doc_chunks (file_name, chunk_index, content, embedding) " +
                "VALUES ($1, $2, $3, $4);");
            cmd.Parameters.AddWithValue(fileName);
            cmd.Parameters.AddWithValue(i);
            cmd.Parameters.AddWithValue(chunks[i]);
            cmd.Parameters.AddWithValue(new Vector(emb[0].Vector.ToArray()));
            await cmd.ExecuteNonQueryAsync();
        }
        return chunks.Count;
    }

    // ASK: embed question -> retrieve Top-K -> grounded answer + citations.
    public async Task<ChatAnswer> AskAsync(string question, int topK = 5)
    {
        // First attempt: search using the question as-is.
        var first = await RetrieveAndAnswerAsync(question, question, topK);

        if (first.sources.Count == 0)
            return new ChatAnswer("I don't have any documents to answer from yet. Please upload some first.", [], "rag", true);

        bool grounded = await VerifyAsync(question, first.context, first.answer);

        // SELF-CORRECTION: if not grounded OR retrieval was weak, retry once with a rewritten query.
        if (!grounded || first.topScore < 0.45)
        {
            Console.WriteLine($"[RETRY] grounded={grounded}, topScore={first.topScore:F3} -> rewriting query");
            var betterQuery = await RewriteQueryAsync(question);
            var second = await RetrieveAndAnswerAsync(question, betterQuery, topK);
            bool grounded2 = await VerifyAsync(question, second.context, second.answer);
            return new ChatAnswer(second.answer, second.sources, "rag", grounded2);
        }

        return new ChatAnswer(first.answer, first.sources, "rag", grounded);
    }
    // Convert UI-sent history DTOs into ChatMessages the model understands.
    private static IEnumerable<ChatMessage> ToHistory(List<ChatMessageDto>? history) =>
        (history ?? new List<ChatMessageDto>()).Select(m =>
            new ChatMessage(
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant : ChatRole.User,
                m.Text));

    public async IAsyncEnumerable<string> AskStreamingAsync(
        string question, List<ChatMessageDto>? history = null, int topK = 5)
    {
        // 1. Decide the path first (with history so follow-ups like "confirm" route correctly).
        var route = await RouteAsync(question, history);

        ChatOptions options = new() { Temperature = 0.1f };
        ChatMessage systemMsg;

        if (route == "direct")
        {
            systemMsg = new(ChatRole.System, "You are a helpful assistant. Answer concisely.");
        }
        else if (route == "tool")
        {
            // TOOL: tools + human-approval rule for write actions.
            systemMsg = new(ChatRole.System,
                "You are an IT support assistant with tools. For READ-ONLY checks (system health, " +
                "ticket status) call the tool and answer. For ACTIONS that change data (creating a " +
                "ticket) you MUST first call create_ticket with confirmed=false to preview, show the " +
                "user the proposed details, and ask them to confirm. Only call create_ticket with " +
                "confirmed=true after the user has explicitly confirmed in their latest message.");
            options = new ChatOptions { Tools = [.. _mcp.Tools], Temperature = 0 };
        }
        else
        {
            // RAG: retrieve, then build a grounded prompt.
            var qEmb = await _embedder.GenerateAsync([question]);
            var queryVec = new Vector(qEmb[0].Vector.ToArray());

            var retrieved = new List<string>();
            await using (var cmd = _db.CreateCommand(
                "SELECT content FROM doc_chunks ORDER BY embedding <=> $1 LIMIT $2;"))
            {
                cmd.Parameters.AddWithValue(queryVec);
                cmd.Parameters.AddWithValue(topK);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    retrieved.Add(reader.GetString(0));
            }
            string context = string.Join("\n\n", retrieved);
            systemMsg = new(ChatRole.System,
                "You are an IT support assistant. Answer using ONLY the context below. " +
                "If the answer is not in the context, say you don't know.\n\nContext:\n" + context);
        }

        // Build: system + prior conversation (so 'confirm' has context) + the new question.
        var messages = new List<ChatMessage> { systemMsg };
        messages.AddRange(ToHistory(history));
        messages.Add(new(ChatRole.User, question));

        // 2. Stream whichever path we chose.
        await foreach (var update in _chat.GetStreamingResponseAsync(messages, options))
        {
            yield return update.Text;
        }
    }
    // ROUTER: ask the LLM to classify the question, return "rag" or "direct".
    public async Task<string> RouteAsync(string question, List<ChatMessageDto>? history = null)
    {
        // 1. Find out what's actually in the knowledge base (so the router can decide well).
        var files = new List<string>();
        await using (var cmd = _db.CreateCommand("SELECT DISTINCT file_name FROM doc_chunks;"))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                files.Add(reader.GetString(0));
        }
        string kb = files.Count > 0 ? string.Join(", ", files) : "(no documents yet)";

        // 2. Ask the LLM to classify, aware of the documents AND the conversation so far
        //    (so a follow-up like "confirm" routes to the same action it follows).
        var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
            $"You are a routing classifier. The knowledge base contains these documents: {kb}.\n" +
            "Use the conversation so far for context. Reply with ONLY JSON, nothing else:\n" +
            "{\"route\": \"rag\"}    -> answerable from those documents (a person named in them, " +
            "their skills/experience, company policies, IT procedures).\n" +
            "{\"route\": \"tool\"}   -> needs LIVE system status or an action, e.g. " +
            "'is the build server down?', 'check the VPN status', 'create a ticket', 'ticket status'. " +
            "ALSO choose \"tool\" when the user is confirming or continuing a pending tool action " +
            "(e.g. replying 'yes' / 'confirm' right after the assistant proposed creating a ticket).\n" +
            "{\"route\": \"direct\"} -> general knowledge or small talk unrelated to the above.\n" +
            "When in doubt, choose \"rag\".")
    };
        messages.AddRange(ToHistory(history));
        messages.Add(new(ChatRole.User, question));

        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0 });

        string text = response.Text;
        string route = "rag";
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text.Substring(start, end - start + 1));
                route = doc.RootElement.GetProperty("route").GetString() ?? "rag";
            }
            catch { }
        }
        route = route.Trim().ToLowerInvariant();
        Console.WriteLine($"[ROUTER] \"{question}\" -> {route}");
        return route;
    }    // DIRECT path: answer from the model's own knowledge, no retrieval, no citations.
    public async Task<ChatAnswer> AnswerDirectAsync(string question)
    {
        var messages = new List<ChatMessage>
    {
        new(ChatRole.System, "You are a helpful assistant. Answer concisely."),
        new(ChatRole.User, question)
    };
        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.3f });
        return new ChatAnswer(response.Text, [], "direct");
    }

    // AGENT: route first, then take the chosen path.
    public async Task<ChatAnswer> AskAgentAsync(string question, List<ChatMessageDto>? history = null)
    {
        var route = await RouteAsync(question, history);
        return route switch
        {
            "direct" => await AnswerDirectAsync(question),
            "tool"   => await AnswerWithToolsAsync(question, history),
            _        => await AskAsync(question)   // "rag"
        };
    }

    // TOOL path: give the LLM the MCP server's tools and let it call them.
    // For write actions (create ticket) it must preview + get user confirmation first.
    public async Task<ChatAnswer> AnswerWithToolsAsync(string question, List<ChatMessageDto>? history = null)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are an IT support assistant with tools. For READ-ONLY checks (system health, " +
                "ticket status) call the tool and answer. For ACTIONS that change data (creating a " +
                "ticket) you MUST first call create_ticket with confirmed=false to preview, show the " +
                "user the proposed details, and ask them to confirm. Only call create_ticket with " +
                "confirmed=true after the user has explicitly confirmed in their latest message.")
        };
        messages.AddRange(ToHistory(history));
        messages.Add(new(ChatRole.User, question));

        var options = new ChatOptions { Tools = [.. _mcp.Tools], Temperature = 0 };
        var response = await _chat.GetResponseAsync(messages, options);
        return new ChatAnswer(response.Text, [], "tool");
    }

    // VERIFY (LLM-as-judge): is the answer actually supported by the retrieved context?
    public async Task<bool> VerifyAsync(string question, string context, string answer)
    {
        var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
            "You are a strict fact-checker. Given a CONTEXT and an ANSWER, decide whether the " +
            "answer is fully supported by the context (no invented facts). " +
            "An honest 'I don't know' counts as grounded. " +
            "Reply with ONLY JSON: {\"grounded\": true} or {\"grounded\": false}."),
        new(ChatRole.User, $"QUESTION: {question}\n\nCONTEXT:\n{context}\n\nANSWER:\n{answer}")
    };

        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0 });

        // Defensive parse (same pattern as the router).
        string text = response.Text;
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text.Substring(start, end - start + 1));
                bool grounded = doc.RootElement.GetProperty("grounded").GetBoolean();
                Console.WriteLine($"[VERIFY] grounded = {grounded}");
                return grounded;
            }
            catch { /* fall through */ }
        }
        return true;   // if the judge fails to answer, don't block the response
    }

    // Rewrite a question into a better search query for retrieval.
    public async Task<string> RewriteQueryAsync(string question)
    {
        var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
            "Rewrite the user's question into a short, keyword-rich search query that will " +
            "retrieve relevant documents. Reply with ONLY the rewritten query — no quotes, no explanation."),
        new(ChatRole.User, question)
    };
        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0 });
        var rewritten = response.Text.Trim();
        Console.WriteLine($"[REWRITE] \"{question}\" -> \"{rewritten}\"");
        return rewritten;
    }


    // One pass: retrieve using searchQuery, answer the original question, return the pieces.
    private async Task<(string answer, List<Citation> sources, string context, double topScore)>
        RetrieveAndAnswerAsync(string question, string searchQuery, int topK)
    {
        var qEmb = await _embedder.GenerateAsync([searchQuery]);
        var queryVec = new Vector(qEmb[0].Vector.ToArray());

        var retrieved = new List<(string file, int idx, string content, double score)>();
        await using (var cmd = _db.CreateCommand(
            "SELECT file_name, chunk_index, content, 1 - (embedding <=> $1) AS score " +
            "FROM doc_chunks ORDER BY embedding <=> $1 LIMIT $2;"))
        {
            cmd.Parameters.AddWithValue(queryVec);
            cmd.Parameters.AddWithValue(topK);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                retrieved.Add((reader.GetString(0), reader.GetInt32(1),
                               reader.GetString(2), reader.GetDouble(3)));
        }

        string context = string.Join("\n\n", retrieved.Select(r =>
            $"[Source: {r.file} #{r.idx}]\n{r.content}"));

        var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
            "You are an IT support assistant. Answer the user's question using ONLY the " +
            "context below. Cite the source file name(s). If the answer is not in the context, " +
            "say you don't know.\n\nContext:\n" + context),
        new(ChatRole.User, question)
    };
        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.1f });

        var sources = retrieved.Select(r => new Citation(r.file, r.idx, Math.Round(r.score, 3))).ToList();
        double topScore = retrieved.Count > 0 ? retrieved[0].score : 0;
        return (response.Text, sources, context, topScore);
    }
}
