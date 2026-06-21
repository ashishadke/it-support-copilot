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

    public ChatController(RagService rag) => _rag = rag;

    // POST /api/chat/stream
    // Server-Sent Events: the agent routes the question, answers it (RAG answers are
    // verified + self-corrected first), and we push each token to the client live.
    [HttpPost("stream")]
    public async Task Stream([FromBody] ChatRequest req)
    {
        // Tell the client this is a live event stream, not a single response.
        Response.Headers.Append("Content-Type", "text/event-stream");

        await foreach (var token in _rag.AskStreamingAsync(req.Question, req.History))
        {
            if (string.IsNullOrEmpty(token)) continue;   // skip the model's empty "thinking" chunks
            var json = System.Text.Json.JsonSerializer.Serialize(token);  // safely escapes newlines, quotes
            await Response.WriteAsync($"data: {json}\n\n");
            await Response.Body.FlushAsync();
        }
    }
}
