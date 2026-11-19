# Enterprise RAG & Hybrid Search API (.NET 10 + SQL Server 2025 `VECTOR(768)`)

[![Build, Test (SQL Server 2025) & Deploy to Azure](https://github.com/dvmisani/Enterprise-RAG-Hybrid-Search-API/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/dvmisani/Enterprise-RAG-Hybrid-Search-API/actions/workflows/ci-cd.yml)

Production-grade, multi-tenant Retrieval-Augmented Generation (RAG) and hybrid search engine built in **C# / .NET 10**, **SQL Server 2025 / Azure SQL native `VECTOR(768)`**, and **Google Gemini**—engineered with strict anti-hallucination guardrails, inline chunk citations, and deterministic `Testcontainers` integration tests.

- **Live Cloud Playground:** [https://enterprise-rag-api-6125.azurewebsites.net](https://enterprise-rag-api-6125.azurewebsites.net)
- **Interactive OpenAPI / Swagger:** [https://enterprise-rag-api-6125.azurewebsites.net/swagger](https://enterprise-rag-api-6125.azurewebsites.net/swagger)
- **Health Check:** [https://enterprise-rag-api-6125.azurewebsites.net/health](https://enterprise-rag-api-6125.azurewebsites.net/health)

---

## Key Architectural Highlights

1. **Native SQL Server 2025 / Azure SQL `VECTOR(768)` Storage & Search**
   - Stores 768-dimensional embeddings directly in relational tables (`Embedding VECTOR(768) NOT NULL`) alongside tenant metadata—eliminating external vector database synchronization drift.
   - Computes exact cosine distance (`VECTOR_DISTANCE('cosine', ...)`) combined with lexical term scoring via **Reciprocal Rank Fusion (RRF)**.

2. **Anti-Hallucination Guardrail & Inline Provenance**
   - Enforces strict grounding prompts requiring `[Document Title | Chunk #N]` inline citations for every factual claim.
   - Refuses out-of-domain or unsupported queries (`INSUFFICIENT_GROUNDING`) and logs every decision, similarity score, provider, and latency metric to `QueryAuditLogs`.

3. **Multi-Tenant Isolation & Idempotent Ingestion**
   - Isolates document storage, vector retrieval, and audit trails per tenant via the `X-Tenant-Id` header.
   - Prevents duplicate chunk embedding costs using SHA-256 content-hash deduplication per tenant.

4. **Resilient AI Provider Pipeline**
   - Dynamically discovers text-capable Gemini models (`gemini-embedding-001` for 768-dim vectors; multi-model Flash failover with per-model timeouts for synthesis) and degrades gracefully when hosted endpoints experience transient load spikes.

5. **Automated CI/CD with SQL Server 2025 `Testcontainers`**
   - GitHub Actions spins up a real `mcr.microsoft.com/mssql/server:2025-latest` Docker container on every push to verify multi-tenant isolation, sliding-window chunking, SHA-256 deduplication, and grounded citation synthesis before deploying to Azure App Service.

---

## Local Quickstart

bash
# 1. Start SQL Server 2025 in Docker
docker run -d --name sql2025-rag -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Your_Strong_Password123!" -p 1433:1433 mcr.microsoft.com/mssql/server:2025-latest
docker exec sql2025-rag /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Your_Strong_Password123!" -C -Q "CREATE DATABASE EnterpriseRagDb;"

# 2. Configure Gemini API Key via .NET User Secrets
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project src/EnterpriseRag.Api

# 3. Run API & Web Playground
dotnet run --project src/EnterpriseRag.Api


Run the automated SQL Server 2025 integration test suite:

bash
dotnet test --nologo --logger "console;verbosity=normal"


