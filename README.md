# IT Support Copilot

A helpdesk assistant for an internal IT team, built in **.NET 10**. It combines four
patterns that together make a real *agent* rather than a single LLM call:

- **Agentic RAG** — document retrieval is exposed to the LLM as a tool (`search_documents`),
  grounded in pgvector with citations and an honest "I don't know" when the docs don't cover it.
- **Tools via MCP** — a separate **Model Context Protocol** server exposes live actions
  (check system health, look up / create support tickets) that the agent can call.
- **Agentic orchestration** — no router: the LLM is handed one toolbox (document search + MCP
  tools) and decides which to call, chaining several in a single turn when a question needs it.
- **Human-in-the-loop** — data-changing actions (creating a ticket) are previewed and require
  explicit user confirmation before they run.

> Built as a learning project to understand agentic AI in .NET end to end — no
> orchestration framework (LangChain / Semantic Kernel). Every step is plain C# so the
> control flow is visible.

## Architecture

```
            ┌──────────────┐   POST /api/chat/stream (SSE)   ┌────────────────────┐
            │  Angular UI  │ ───────────────────────────────►│   ITSupport.Api    │
            │ (chat + SSE) │ ◄─────────────── tokens ─────────│   (Controllers)    │
            └──────────────┘                                  └─────────┬──────────┘
                                                                        │
                               AskStreamingAsync — ONE agentic path: the LLM is handed a
                               toolbox and picks/chains tools itself (UseFunctionInvocation)
                          ┌─────────────────────────────────────────────┴──────────┐
                          ▼                                                          ▼
                 search_documents (local tool)                   MCP tools (over stdio / JSON-RPC)
                 embed (cached in Redis) →                       check_system_health
                 pgvector top-8 + citations                      get_ticket_status
                          │                                      create_ticket (preview → confirm)
                          ▼                                                          ▼
                 Postgres + pgvector                             ┌────────────────────┐
                   (doc_chunks)                                  │ ITSupport.McpServer │
                                                                 │  (separate .exe)    │
                                                                 │  → Postgres (tickets)│
                                                                 └────────────────────┘
```

**Models run locally via [Ollama](https://ollama.com):**
`nomic-embed-text` (768-dim embeddings) for retrieval, `qwen3:4b` for chat + tool calling.

### How a request flows
1. The UI streams the question (plus recent history) to `/api/chat/stream`.
2. The agent is handed **one toolbox** — `search_documents` plus the MCP tools — and a system
   prompt. There is **no router**: the LLM decides which tools to call.
3. `UseFunctionInvocation()` runs the tool loop, so the model can call **several tools in one turn**
   (e.g. `search_documents` for a policy question *and* `get_ticket_status` for a ticket lookup),
   then compose one combined answer.
4. `search_documents` embeds the query (cached in Redis), does a top-8 cosine search in pgvector,
   and returns the chunks with citations; the model answers only from what it gets back.
5. Creating a ticket is gated: the model first **previews** (`confirmed=false`) and asks the user
   to confirm; only after an explicit "yes" does it call `create_ticket` with `confirmed=true`.

## Tech stack
| Layer | Tech |
|------|------|
| API | ASP.NET Core (.NET 10) Web API — controllers, SSE streaming |
| AI | Microsoft.Extensions.AI, OllamaSharp (`IChatClient`, `IEmbeddingGenerator`, `AIFunctionFactory`) |
| Vectors | PostgreSQL + [pgvector](https://github.com/pgvector/pgvector) (cosine `<=>`) |
| Tools | Model Context Protocol (`ModelContextProtocol` 1.4.0) over stdio |
| Caching | Redis (`StackExchange.Redis`) — embedding cache |
| UI | Angular (standalone components, signals, fetch + ReadableStream for streaming) |

## Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Node.js](https://nodejs.org/) (for the Angular UI)
- [Ollama](https://ollama.com) with the models pulled:
  ```bash
  ollama pull nomic-embed-text
  ollama pull qwen3:4b
  ```
- PostgreSQL with the **pgvector** extension installed.
- [Redis](https://redis.io) running locally on `localhost:6379` (used for the embedding cache).

## Setup

### 1. Database
Create the database and tables:
```sql
CREATE DATABASE "ITSupportDb";
\c ITSupportDb
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE doc_chunks (
    id          SERIAL PRIMARY KEY,
    file_name   TEXT NOT NULL,
    chunk_index INT  NOT NULL,
    content     TEXT NOT NULL,
    embedding   VECTOR(768)
);

CREATE TABLE tickets (
    id          SERIAL PRIMARY KEY,
    title       TEXT NOT NULL,
    description TEXT,
    status      TEXT NOT NULL DEFAULT 'Open',
    created_at  TIMESTAMP DEFAULT NOW()
);
```

### 2. Configuration
The connection string and Ollama URL live in `ITSupport.Api/appsettings.json`. The defaults
target a **local** Postgres (`localhost:5432`, user `postgres`). Override them for your machine
via `appsettings.Development.json`, environment variables, or user-secrets — don't commit real
credentials. The MCP server picks up the same connection string from the API at startup
(passed as an environment variable to the subprocess).

### 3. Run
```bash
# 1. Build the MCP server so its .exe exists (the API launches it as a subprocess)
dotnet build ITSupport.McpServer

# 2. Run the API (also starts the MCP server subprocess + Ollama clients)
dotnet run --project ITSupport.Api
#   API:        http://localhost:5073   (Scalar API docs at /scalar/v1)

# 3. Run the UI
cd it-support-ui
npm install
npm start
#   UI:         http://localhost:4200
```

### 4. Try it
- Upload a document at the top of the UI, then ask a question about it (calls `search_documents`).
- Ask *"is the build server down?"* (tool: `check_system_health`).
- Ask *"what's the status of ticket 1?"* (tool: `get_ticket_status`).
- Ask a **compound** question: *"who is Ashish and what is the status of ticket 1?"* — the agent
  calls **both** `search_documents` and `get_ticket_status` in one turn and combines the answer.
- Ask *"create a ticket for my flickering monitor"* → it previews → reply *"confirm"* to create it
  (human-in-the-loop).

### Evaluation
`GET /api/eval` runs `ITSupport.Api/eval-set.json` (a list of test questions + expected facts)
against the agent and returns a scored pass/fail report — automated regression testing for the
RAG answers. Edit `eval-set.json` to add your own test cases.

## Project layout
```
ITSupport.Api/          ASP.NET Core Web API
  Controllers/          ChatController (SSE), DocumentsController, EvalController
  Services/
    RagService.cs       the agent core: search_documents tool + agentic streaming path
    McpService.cs       launches the MCP server, discovers its tools
    EvaluationService.cs runs eval-set.json against the agent (regression testing)
  eval-set.json         the evaluation "exam" (questions + expected facts)
ITSupport.McpServer/    standalone MCP server (separate process)
  HealthTools.cs        check_system_health
  TicketTools.cs        get_ticket_status, create_ticket (with confirm gate)
it-support-ui/          Angular chat UI with streaming
```

## Notes
- Credentials in `appsettings.json` are **local-dev defaults only**. Use real secrets management
  for anything beyond local development.
- Everything runs locally — no data leaves the machine (local Ollama models, local Postgres).
