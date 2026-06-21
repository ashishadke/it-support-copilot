// =============================================================================
//  IT SUPPORT COPILOT — composition root (DI + middleware).
//  HTTP endpoints live in Controllers/:
//    ChatController       POST /api/chat/stream        (SSE: routed, verified RAG)
//    DocumentsController  POST /api/documents/upload   (chunk + embed + store)
//                         GET  /api/documents          (list ingested files)
//
//  Requires: Ollama (nomic-embed-text + qwen3:4b), Postgres ITSupportDb (pgvector),
//            and Redis (caching). All configured in appsettings.json.
// =============================================================================

using ITSupport.Api.Services;
using Microsoft.Extensions.AI;
using Npgsql;
using OllamaSharp;
using Pgvector.Npgsql;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();   // endpoints live in Controllers/ (ChatController, DocumentsController)
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

// --- Redis (singleton connection, reused for all caching) ---------------------
var redisUrl = builder.Configuration["Redis:Url"] ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisUrl));

// --- Our RAG service ----------------------------------------------------------
builder.Services.AddScoped<RagService>();
builder.Services.AddScoped<EvaluationService>();   // runs the eval set against the agent
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

// All API endpoints live in Controllers/ (ChatController, DocumentsController).
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/scalar/v1"));

app.Run();
