using ITSupport.Api.Services;

namespace ITSupport.Api.Models;

// Request body for the chat endpoint. History is the recent conversation
// (sent by the UI) so the agent has context — e.g. a "confirm" message knows
// which action it's confirming.
public record ChatRequest(string Question, List<ChatMessageDto>? History = null);
