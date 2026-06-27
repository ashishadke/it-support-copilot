namespace ITSupport.Api.Models;

// Request body for the chat endpoint. ConversationId ties messages together so the
// server can load the prior turns from the database (memory survives a page refresh).
public record ChatRequest(string Question, string? ConversationId = null);
