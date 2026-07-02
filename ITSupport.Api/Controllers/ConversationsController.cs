using ITSupport.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ITSupport.Api.Controllers;

// Feeds the UI's "recent chats" sidebar and restores a chat's transcript.
[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly ConversationStore _conversations;

    public ConversationsController(ConversationStore conversations) => _conversations = conversations;

    // GET /api/conversations — the 10 most recent chats (title = first user message).
    [HttpGet]
    public async Task<IActionResult> Recent()
        => Ok(await _conversations.GetRecentConversationsAsync(10));

    // GET /api/conversations/{id} — full transcript, for restoring a clicked chat.
    [HttpGet("{id}")]
    public async Task<IActionResult> Messages(string id)
        => Ok(await _conversations.GetAllMessagesAsync(id));
}
