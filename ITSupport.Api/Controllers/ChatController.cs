using ITSupport.Api.Models;
using ITSupport.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ITSupport.Api.Controllers;

// Chat endpoints. The UI talks to /api/chat/stream and renders tokens as they arrive.
[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly RagService _rag;
    private readonly ConversationStore _conversations;

    public ChatController(RagService rag, ConversationStore conversations)
    {
        _rag = rag;
        _conversations = conversations;
    }

    // POST /api/chat/stream
    // Server-Sent Events. If a conversationId is supplied, the server loads the prior
    // turns (+ running summary) from the database so the agent remembers the conversation
    // — and persists this turn — so memory survives a page refresh.
    [HttpPost("stream")]
    public async Task Stream([FromBody] ChatRequest req)
    {
        // Tell the client this is a live event stream, not a single response.
        Response.Headers.Append("Content-Type", "text/event-stream");

        var conversationId = req.ConversationId;
        bool persist = !string.IsNullOrWhiteSpace(conversationId);

        // Load memory BEFORE saving the new question (so history = prior turns only).
        List<ChatMessageDto>? history = null;
        string? summary = null;
        if (persist)
        {
            history = await _conversations.GetRecentAsync(conversationId!, 10);
            summary = await _conversations.GetSummaryAsync(conversationId!);
            await _conversations.AddMessageAsync(conversationId!, "user", req.Question);
        }

        var full = new System.Text.StringBuilder();
        await foreach (var token in _rag.AskStreamingAsync(req.Question, history, summary))
        {
            if (string.IsNullOrEmpty(token)) continue;   // skip the model's empty "thinking" chunks
            full.Append(token);
            var json = System.Text.Json.JsonSerializer.Serialize(token);  // safely escapes newlines, quotes
            await Response.WriteAsync($"data: {json}\n\n");
            await Response.Body.FlushAsync();
        }

        // Persist the assistant's full reply for next time.
        if (persist)
        {
            await _conversations.AddMessageAsync(conversationId!, "assistant", full.ToString());

            // MEMORY MAINTENANCE (runs AFTER the response is sent, so the user never
            // waits for it): once >=10 messages have fallen out of the live window,
            // condense them into the running summary and advance the watermark.
            var (older, maxId) = await _conversations.GetUnsummarizedOlderAsync(conversationId!, keepLast: 10);
            if (older.Count >= 6)
            {
                var previous = await _conversations.GetSummaryAsync(conversationId!);
                var merged = await _rag.SummarizeAsync(previous, older);
                await _conversations.SaveSummaryAsync(conversationId!, merged, maxId);
            }
        }
    }
}
