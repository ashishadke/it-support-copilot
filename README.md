# IT Support Copilot

A helpdesk assistant for an internal IT team, built in **.NET 10**. It combines four
patterns that together make a real *agent* rather than a single LLM call:

- **RAG** — answers questions from internal IT documents (handbook, policies) grounded in
  retrieved text, with citations and an honest "I don't know" when the docs don't cover it.
- **Tools via MCP** — a separate **Model Context Protocol** server exposes live actions
  (check system health, look up / create support tickets) that the agent can call.
- **Agent orchestration** — a hand-built control loop: *route → retrieve → verify → self-correct*.
- **Human-in-the-loop** — data-changing actions (creating a ticket) are previewed and require
  explicit user confirmation before they run.

> Built as a learning project to understand agentic AI in .NET end to end — no
> orchestration framework (LangChain / Semantic Kernel). Every step is plain C# so the
> control flow is visible.

## Architecture

```
            ┌──────────────┐   POST /api/chat/stream (SSE)   ┌────────────────────┐
            │  Angular UI  │ ───────────────────────────────►│   ITSupport.Api    │
            │ (chat + SSE) │ ◄─────────────── tokens ─────────│  (ASP.NET Core)    │
            └──────────────┘                                  └─────────┬──────────┘
                                                                        │
                          ┌─────────────────────────────────────────────┤
                          │                          │                   │
                    Router (LLM)              RAG retrieval         Tool calling
                  rag / tool / direct      pgvector cosine       (MCP tools as
                          │                  Top-K search         AIFunctions)
                          │                          │                   │
                          ▼                          ▼                   ▼ JSON-RPC / stdio
                  Verify + self-correct      Postgres + pgvector  ┌────────────────────┐
                   (LLM-as-judge, retry)       (doc_chunks)       │ ITSupport.McpServer│
                                                                  │  (separate .exe)   │
                                                                  │  HealthTools       │
                                                                  │  TicketTools ──────┼──► Postgres
                                                                  └────────────────────┘    (tickets)
```

**Models run locally via [Ollama](https://ollama.com):**
`nomic-embed-text` (768-dim embeddings) for retrieval, `qwen3:4b` for chat + tool calling.

### How a request flows
1. The UI streams the question (plus recent history) to `/api/chat/stream`.
2. The **router** classifies it: `rag` (answer from docs), `tool` (call an action), or `direct`.
3. **rag** → embed query → cosine search in pgvector → grounded answer → **verify** (LLM-as-judge);
   if weak/ungrounded, **rewrite the query and retry once**.
4. **tool** → the LLM is given the MCP tools and picks one; `UseFunctionInvocation()` runs the
   call over stdio/JSON-RPC to the MCP server, feeds the result back, and the LLM replies.
5. Creating a ticket is gated: first call **previews** (`confirmed=false`); only after the user
   confirms does it **insert** (`confirmed=true`).

## Tech stack
| Layer | Tech |
|------|------|
| API | ASP.NET Core minimal APIs (.NET 10), SSE streaming |
| AI | Microsoft.Extensions.AI, OllamaSharp (`IChatClient`, `IEmbeddingGenerator`) |
| Vectors | PostgreSQL + [pgvector](https://github.com/pgvector/pgvector) (cosine `<=>`) |
| Tools | Model Context Protocol (`ModelContextProtocol` 1.4.0) over stdio |
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
- Upload a document at the top of the UI, then ask a question about it (RAG).
- Ask *"is the build server down?"* (tool: `check_system_health`).
- Ask *"what's the status of ticket 1?"* (tool: `get_ticket_status`).
- Ask *"create a ticket for my flickering monitor"* → it previews → reply *"confirm"* to create it
  (human-in-the-loop).

## Project layout
```
ITSupport.Api/          ASP.NET Core API — RAG, router, verify, MCP client, endpoints
  Services/
    RagService.cs       the agent core (route / retrieve / verify / self-correct)
    McpService.cs       launches the MCP server, discovers its tools
ITSupport.McpServer/    standalone MCP server (separate process)
  HealthTools.cs        check_system_health
  TicketTools.cs        get_ticket_status, create_ticket (with confirm gate)
it-support-ui/          Angular chat UI with streaming
```

## Notes
- Credentials in `appsettings.json` are **local-dev defaults only**. Use real secrets management
  for anything beyond local development.
- Everything runs locally — no data leaves the machine (local Ollama models, local Postgres).
