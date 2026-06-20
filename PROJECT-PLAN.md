# IT Support Copilot — Project Plan & Learning Guide
A real-world application covering **RAG**, **Agent Orchestration**, and **MCP Servers**, built with a fully free stack.

---

## 0. Current Environment (ALREADY SET UP) ✅
Adjusted to reflect what's already installed on this machine, so we skip those steps:

- **Ollama** — installed and serving on `localhost:11434`. Models already pulled:
  `nomic-embed-text` (embeddings, 768-dim), `qwen2.5:3b`, `qwen3:4b` (tool-calling), `gemma4`.
- **PostgreSQL 18 + pgvector 0.8.2** — pgvector compiled from source and installed.
  Learning DB `RagLearningDb` exists with a `rag_chunks(id, content, embedding vector(768))` table.
  Creds: `postgres` / `postgres` on `localhost:5432`. psql at `C:\Program Files\PostgreSQL\18\bin\psql.exe`.
- **.NET 10 SDK** — installed. Visual Studio 18 Community available.
- **Decision:** use **pgvector** as the vector DB (NOT Qdrant) — it's already installed and we know it well.
- **Embeddings:** Ollama `nomic-embed-text` (local, unlimited).
- **Reasoning/agent model:** start with local `qwen3:4b`; optionally swap to Gemini Flash later for quality.

> Prior learning: console-app RAG + agent already built in `C:\ashish folder\claude repos\AgenticAiLearning`
> (Phases 0–5: embeddings, RAG, pgvector persistence, tools/function-calling, agent, conversation memory).
> This project turns that knowledge into a real multi-tier application.

---

## 1. Core Concepts

### MCP Server (Model Context Protocol)
- An open standard (by Anthropic) that lets an AI model connect to external tools and data in a standardized way.
- An MCP server exposes **tools** (e.g., "create ticket", "query database"), **resources** (files, docs), and **prompts** to any MCP-compatible client.
- Think of it as a REST API designed for LLMs: write one MCP server, and any AI client can discover and call its tools.

### RAG Server (Retrieval-Augmented Generation)
Solves the problem that LLMs don't know your private/recent data.
Pipeline:
1. **Ingestion** — documents are chunked, converted to embeddings (vectors), stored in a vector DB.
2. **Retrieval** — user query is embedded; most semantically similar chunks are fetched.
3. **Generation** — retrieved chunks are injected into the LLM prompt so answers are grounded in your data (with citations).

### Agent Orchestration
- Coordinating multiple LLM calls, tools, and decision steps into a workflow.
- Modeled as a **graph**: nodes = steps (LLM call, tool call, retrieval), edges = transitions, conditional edges = decisions.
- Handles state, loops/retries, and human-in-the-loop checkpoints.
- **Key insight:** the orchestrator doesn't think. Every "decision" is an LLM call whose output your code branches on. The LLM is the brain; the orchestrator is the skeleton.
- **Orchestration tech — TBD (see note):** plan originally said **LangGraph (Python)**. Since the target is a **.NET job**, a .NET-native option is **Microsoft Agent Framework / Semantic Kernel**, which keeps the whole stack in C#. Decide before Phase 3.

### How they fit together
An orchestrated agent decides a question needs internal docs → calls the RAG pipeline to retrieve context → uses an MCP server to take an action (e.g., create a ticket) — all in one workflow.

---

## 2. The Project: IT Support Copilot
An assistant for a company helpdesk that:
- Answers employee questions from internal docs (VPN setup, leave policy) → **RAG**
- Checks live system data ("is the build server down?") → **MCP tools**
- Takes actions ("reset my password", "create a ticket") → **MCP tools + human approval**
- Routes each query to the right path and verifies answers are grounded → **agent orchestration**

### Why this project
- A genuine product category companies build today — every problem you hit is a real engineering problem.
- Naturally exercises all three concepts.
- Fits the existing stack: Angular + .NET + Redis + orchestrator + LLM API.

---

## 3. Architecture
```
┌─────────────┐     SSE/SignalR      ┌──────────────────┐
│   Angular    │ ◄──────────────────► │   .NET Web API    │
│  (chat UI,   │                      │  (auth, sessions, │
│  citations,  │                      │   doc upload,     │
│  tickets)    │                      │   streaming)      │
└─────────────┘                      └────────┬─────────┘
                                              │ HTTP
                                     ┌────────▼─────────┐
                                     │ Orchestrator      │
                                     │  router → RAG /   │
                                     │  tools → verify   │
                                     └──┬─────────┬──────┘
                                        │         │
                              ┌─────────▼──┐   ┌──▼────────────┐
                              │ pgvector    │   │ C# MCP Server │
                              │ (Postgres,  │   │ create_ticket │
                              │  installed) │   │ ticket_status │
                              └────────────┘   │ system_health │
                                               └──────┬────────┘
                                                ┌─────▼─────┐
                                                │ SQL DB    │
                                                │ (mock IT  │
                                                │  data)    │
                                                └───────────┘
         Redis: chat history, embedding cache, response cache, rate limiting
```

### Component responsibilities
| Component | Role |
|---|---|
| **Angular** | Chat UI with streaming, source-citations panel, document upload, ticket history view |
| **.NET Web API** | Core backend: receives chat requests, manages sessions, calls the agent, streams responses (SSE or SignalR) |
| **Orchestrator** | The agent graph: router node → retrieval / tool / direct paths → grounding-check node; loops to rewrite queries on weak retrieval. (.NET-native via Semantic Kernel, or Python LangGraph — TBD) |
| **C# MCP Server** | Tools: `create_ticket`, `get_ticket_status`, `check_system_health` — backed by SQL Server/Postgres with seeded mock data (uses the official MCP C# SDK) |
| **Vector DB** | **pgvector + Postgres (already installed)** — stores document chunk embeddings |
| **Redis** | Chat history, embedding cache for repeated queries, response cache, rate limiting |

### The agent graph
1. **Router node** — LLM classifies intent: `rag` (docs question) / `tool` (live data or action) / `direct` (simple answer). Implemented as a prompt asking for JSON like `{ "route": "rag" | "tool" | "direct" }`; a conditional edge branches on it.
2. **Retrieval node** — embeds query, fetches top chunks from pgvector.
3. **Tool node** — LLM emits a tool call (native `tool_use` / function calling); code executes it via the MCP server and feeds back the result.
4. **Generation node** — answers grounded in retrieved context, with citations.
5. **Verification node** — checks the answer is actually supported by the sources; if retrieval was weak, loop back with a rewritten query.
6. **Human-in-the-loop** — approval checkpoint before risky actions (e.g., password reset).

---

## 4. Free ($0) Model Stack
The architecture is provider-agnostic — swap models with a one-line change (we already use `IChatClient`).

### Option 1 — Fully local with Ollama (ALREADY SET UP, best for learning)
- **Reasoning/agent model:** `qwen3:4b` (supports tool calling). `gemma4` also available.
- **Embeddings:** `nomic-embed-text` (768-dim).
- No API key, no limits, no data leaves your machine.

### Option 2 — Free API tiers (better quality, rate-limited)
- **Google Gemini Flash** (AI Studio): ~1,500 requests/day free, no credit card, supports function calling.
- **Groq / Cerebras / GitHub Models / OpenRouter:** free tiers serving open models (Llama, Qwen) — Groq is extremely fast, good for the router node.
- ⚠️ Most no-card free tiers may use prompts for model training — fine for dummy IT docs; avoid real personal data.

### Recommended combo
- **Ollama** for embeddings (unlimited, local) ✅
- **qwen3:4b** locally for reasoning to start; optionally **Gemini Flash** later for smarter reasoning.
- Production pattern to learn: **cheap/fast model for routing, stronger model for reasoning** (cost-aware model selection).

### Rest of the free stack
- Vector DB: **pgvector + Postgres (installed)**
- Redis: local Docker (to add)
- Orchestrator, .NET, Angular, MCP C# SDK: all free/open source

> If using the Claude API later (e.g., at a job): Sonnet-class model for generation/tool use/verification, Haiku-class for routing. Anthropic doesn't provide embeddings — Voyage AI is the recommended provider (has a free tier).

---

## 5. Build Phases (Learning Path)

### Phase 1 — Plain RAG (as a .NET Web API)
- .NET API: upload PDF → chunk → embed (Ollama) → store in **pgvector**
- Query endpoint: embed question → retrieve top chunks → generate answer with citations
- **Learn:** chunking strategies, embeddings, vector similarity search, honest "I don't know" handling, REST API design
- **Note:** we already have all this logic from the console apps — this phase ports it into a proper Web API with endpoints.

### Phase 2 — Angular chat UI
- Streaming responses (SSE/SignalR), citations panel, document upload
- **Learn:** Angular fundamentals, streaming UX patterns

### Phase 3 — Agent orchestration layer
- Router node + conditional edges replacing the direct RAG call
- Grounding/verification node, query-rewrite retry loop
- **Learn:** graph state, conditional edges, self-correction patterns

### Phase 4 — C# MCP Server
- 2–3 tools (`create_ticket`, `get_ticket_status`, `check_system_health`) backed by a seeded mock database
- Wire into the agent's tool node; add human-in-the-loop approval for actions
- **Learn:** the MCP protocol, tool schemas, JSON-RPC message flow, how LLMs discover and call tools

### Phase 5 — Polish & evaluation
- Redis: embedding cache, response cache, chat history, rate limiting
- Small evaluation set: test questions with expected answers; measure accuracy / groundedness
- **Learn:** cache invalidation strategies, RAG evaluation

---

## 6. Key Mechanics to Understand

**How an LLM "decides" (router example):**
1. Send a prompt: *"Given this user query, respond ONLY with JSON: { \"route\": \"rag\" | \"tool\" | \"direct\" }"*
2. Parse the JSON.
3. The orchestrator's conditional edge branches on the value.

**How tool calling works:**
1. Tools (with schemas) are declared to the model.
2. The model returns a structured tool-call block: *"call `create_ticket` with these arguments."*
3. Your code executes the tool (via the MCP server) and sends the result back.
4. The model continues reasoning with the result.

The model genuinely plans and decides — your code is just the hands.

---

## 7. Next Steps
- [x] Install Ollama; pull a reasoning model + `nomic-embed-text`  ✅ done
- [x] pgvector installed (using pgvector instead of Qdrant)  ✅ done
- [ ] Run Redis in Docker
- [ ] Create the .NET Web API project; first endpoint: embed & store a document chunk
- [ ] Build the query → retrieve → answer flow (Phase 1 complete)
- [ ] Then: Angular UI → orchestration → MCP server → polish
