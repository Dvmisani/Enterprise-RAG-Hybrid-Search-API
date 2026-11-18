# Enterprise RAG & Hybrid Search API (.NET 10 + SQL Server 2025 + Gemini)

A production-grade, multi-tenant **Retrieval-Augmented Generation (RAG) & Hybrid Search API** engineered in **C# / .NET 10** and **SQL Server 2025**, designed to eliminate LLM hallucinations on internal enterprise policies, financial standards, and technical documentation.

## Core Architecture & Engineering Highlights

- **Native SQL Server 2025 Vector Search (`VECTOR(768)`):** Stores L2-normalized 768-dimensional embeddings directly in SQL Server 2025 `VECTOR(768)` columns and computes cosine similarity via native `VECTOR_DISTANCE('cosine', ...)`.
- **Hybrid Retrieval via Reciprocal Rank Fusion (RRF):** Combines semantic dense-vector ranking with exact lexical keyword term-frequency ranking (`k = 60`) so exact regulatory codes (e.g., `POPIA Section 14`, `Rule FIN-904`) and semantic paraphrases both rank at the top.
- **Anti-Hallucination Citation Guardrails:** Enforces a strict relevance threshold gate prior to synthesis and instructs `gemini-2.5-flash` to cite `[Document Title | Chunk #N]` inline or explicitly refuse with `INSUFFICIENT_GROUNDING` when indexed context lacks evidence.
- **Multi-Tenant Isolation & SHA-256 Ingestion Idempotency:** Every document, chunk, vector query, and audit log is isolated by `X-Tenant-Id`, with SHA-256 content-hash deduplication to avoid redundant embedding API spend.
- **Deterministic Offline Integration Suite (`Testcontainers`):** Executes end-to-end HTTP + SQL Server 2025 integration tests in Docker with a deterministic hash-embedding fallback provider so CI/CD pipelines run fast without external API keys.

## Running Locally

```bash
# 1. Configure your free Google Gemini API key via User Secrets
dotnet user-secrets init --project src/EnterpriseRag.Api
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project src/EnterpriseRag.Api

# 2. Run the SQL Server 2025 Testcontainers integration tests
dotnet test --nologo --logger "console;verbosity=normal"
