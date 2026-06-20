// =============================================================================
//  IT SUPPORT COPILOT — Phase 1: RAG as a .NET Web API
//
//  Endpoints:
//    POST /api/documents/upload   (multipart file)  -> chunk + embed + store
//    POST /api/chat               { "question": "" } -> retrieve + grounded answer
//    GET  /api/documents                              -> list ingested files
//
//  Requires: Ollama (nomic-embed-text + qwen3:4b) and Postgres ITSupportDb (pgvector).
// =============================================================================

using ITSupport.Api.Services;
using Microsoft.Extensions.AI;
using Npgsql;
using OllamaSharp;
using Pgvector.Npgsql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// --- AI clients (singletons) --------------------------------------------------
// Read everything from configuration (appsettings.json / env vars / user-secrets)
// so no machine-specific values or credentials are hard-coded in source.
var ollamaUrl      = builder.Configuration["Ollama:Url"]            ?? "http://localhost:11434";
var embeddingModel = builder.Configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
var chatModel      = builder.Configuration["Ollama:ChatModel"]      ?? "qwen3:4b";

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(ollamaUrl, embeddingModel));

builder.Services.AddSingleton<IChatClient>(
    ((IChatClient)new OllamaApiClient(ollamaUrl, chatModel))
        .AsBuilder()
        .UseFunctionInvocation()   // runs the tool-call loop so the LLM can use MCP tools
        .Build());

// --- Postgres data source with pgvector mapping (singleton) -------------------
var connectionString =
    builder.Configuration.GetConnectionString("ITSupportDb")
    ?? "Host=localhost;Port=5432;Database=ITSupportDb;Username=postgres;Password=postgres";
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
builder.Services.AddSingleton(dataSourceBuilder.Build());

// --- Our RAG service ----------------------------------------------------------
builder.Services.AddScoped<RagService>();
// Allow the Angular dev server (localhost:4200) to call this API from the browser.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});
// Connect to the MCP server and discover its tools once, at startup.
// ServerPath can be overridden in config; otherwise McpService resolves it
// relative to this app's folder so the repo works on any machine.
var mcpServerPath = builder.Configuration["Mcp:ServerPath"];
var mcpService = new McpService();
await mcpService.InitializeAsync(mcpServerPath, connectionString);
builder.Services.AddSingleton(mcpService);

var app = builder.Build();
app.UseCors();   // activates the CORS policy above
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // serves the interactive UI at /scalar/v1
}

// -----------------------------------------------------------------------------
// POST /api/documents/upload  — upload a PDF or TXT, ingest it into pgvector.
// -----------------------------------------------------------------------------
app.MapPost("/api/documents/upload", async (IFormFile file, RagService rag) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    string text;
    await using (var stream = file.OpenReadStream())
        text = DocumentTextExtractor.Extract(stream, file.FileName);

    int chunks = await rag.IngestAsync(file.FileName, text);
    return Results.Ok(new { file = file.FileName, characters = text.Length, chunksStored = chunks });
})
.DisableAntiforgery();   // simplifies multipart upload for our local API

// -----------------------------------------------------------------------------
// POST /api/chat  — ask a question, get a grounded answer + citations.
// -----------------------------------------------------------------------------
app.MapPost("/api/chat", async (ChatRequest req, RagService rag) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest("Question is required.");

    ChatAnswer answer = await rag.AskAgentAsync(req.Question, req.History);
    return Results.Ok(answer);
});

app.MapPost("/api/chat/stream", async (ChatRequest req, RagService rag, HttpResponse response) =>
{
    // Tell the client this is a live event stream, not a single response.
    response.Headers.Append("Content-Type", "text/event-stream");

    // Pull each token from our streaming service and push it immediately.
    await foreach (var token in rag.AskStreamingAsync(req.Question, req.History))
    {
        if (string.IsNullOrEmpty(token)) continue;   // skip the model's empty "thinking" chunks
        var json = System.Text.Json.JsonSerializer.Serialize(token);  // safely escapes newlines, quotes
        await response.WriteAsync($"data: {json}\n\n");
        await response.Body.FlushAsync();
    }
});

// TEMP: test the router in isolation. e.g. POST {"question":"who wrote Romeo and Juliet?"}
app.MapPost("/api/route", async (ChatRequest req, RagService rag) =>
{
    var route = await rag.RouteAsync(req.Question);
    return Results.Ok(new { question = req.Question, route });
});

// -----------------------------------------------------------------------------
// GET /api/documents  — list which files have been ingested (and chunk counts).
// -----------------------------------------------------------------------------
app.MapGet("/api/documents", async (NpgsqlDataSource db) =>
{
    await using var cmd = db.CreateCommand(
        "SELECT file_name, COUNT(*) FROM doc_chunks GROUP BY file_name ORDER BY file_name;");
    var docs = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        docs.Add(new { file = reader.GetString(0), chunks = reader.GetInt64(1) });
    return Results.Ok(docs);
});

app.MapGet("/", () => Results.Redirect("/scalar/v1"));

app.Run();

// Request body for /api/chat and /api/chat/stream.
// History is the recent conversation (sent by the UI) so the agent has context
// — e.g. a "confirm" message knows which ticket it's confirming.
record ChatRequest(string Question, List<ChatMessageDto>? History = null);
