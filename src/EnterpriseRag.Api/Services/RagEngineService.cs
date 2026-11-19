using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EnterpriseRag.Api.Data;
using EnterpriseRag.Api.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseRag.Api.Services;

public sealed record IngestDocumentRequest(string Title, string SourceUri, string Content);
public sealed record IngestDocumentResponse(Guid DocumentId, string Title, string ContentHash, int ChunkCount, bool Deduplicated);

public sealed record RagQueryRequest(string Question, int TopK = 4, double MinRelevanceScore = 0.68);
public sealed record CitationSourceDto(
    Guid ChunkId,
    string DocumentTitle,
    string SourceUri,
    int ChunkIndex,
    string Excerpt,
    double VectorSimilarity,
    double KeywordScore,
    double RrfScore);

public sealed record RagQueryResponse(
    string Question,
    string Answer,
    bool Grounded,
    double TopSimilarityScore,
    string ProviderUsed,
    long LatencyMs,
    IReadOnlyList<CitationSourceDto> Citations);

public sealed class RagEngineService(
    RagDbContext db,
    IEmbeddingProvider embedder,
    IChatCompletionProvider chat)
{
    public async Task<IngestDocumentResponse> IngestAsync(string tenantId, IngestDocumentRequest req, CancellationToken ct = default)
    {
        var normalizedContent = req.Content.Trim();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent));
        var contentHash = Convert.ToHexStringLower(hashBytes);

        var existing = await db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.ContentHash == contentHash, ct);

        if (existing is not null)
        {
            return new IngestDocumentResponse(existing.Id, existing.Title, existing.ContentHash, existing.ChunkCount, Deduplicated: true);
        }

        var slices = SlidingWindowChunker.Chunk(normalizedContent);
        var doc = new KnowledgeDocument
        {
            TenantId = tenantId,
            Title = req.Title.Trim(),
            SourceUri = string.IsNullOrWhiteSpace(req.SourceUri) ? $"internal://{tenantId}/{contentHash[..12]}" : req.SourceUri.Trim(),
            ContentHash = contentHash,
            ChunkCount = slices.Count
        };

        var embedTasks = slices.Select(s => embedder.EmbedAsync(s.Content, "RETRIEVAL_DOCUMENT", ct)).ToArray();
        var vectors = await Task.WhenAll(embedTasks);

        var chunkRecords = new List<(DocumentChunk Chunk, string VectorJson)>(slices.Count);
        for (int i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var chunk = new DocumentChunk
            {
                DocumentId = doc.Id,
                TenantId = tenantId,
                ChunkIndex = slice.Index,
                Content = slice.Content,
                TokenEstimate = slice.TokenEstimate,
                EmbeddingJson = string.Empty
            };
            chunkRecords.Add((chunk, SerializeVector(vectors[i])));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);

        foreach (var (chunk, vecJson) in chunkRecords)
        {
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO DocumentChunks (Id, DocumentId, TenantId, ChunkIndex, Content, TokenEstimate, Embedding)
                  VALUES (@Id, @DocumentId, @TenantId, @ChunkIndex, @Content, @TokenEstimate, CAST(@Embedding AS VECTOR(768)));",
                new SqlParameter("@Id", chunk.Id),
                new SqlParameter("@DocumentId", chunk.DocumentId),
                new SqlParameter("@TenantId", chunk.TenantId),
                new SqlParameter("@ChunkIndex", chunk.ChunkIndex),
                new SqlParameter("@Content", chunk.Content),
                new SqlParameter("@TokenEstimate", chunk.TokenEstimate),
                new SqlParameter("@Embedding", vecJson));
        }

        await tx.CommitAsync(ct);
        return new IngestDocumentResponse(doc.Id, doc.Title, doc.ContentHash, doc.ChunkCount, Deduplicated: false);
    }

    public async Task<RagQueryResponse> QueryAsync(string tenantId, RagQueryRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var topK = Math.Clamp(req.TopK, 1, 10);
        var question = req.Question.Trim();

        var queryVector = await embedder.EmbedAsync(question, "RETRIEVAL_QUERY", ct);
        var queryVectorJson = SerializeVector(queryVector);

        var candidates = await FetchVectorCandidatesAsync(tenantId, queryVectorJson, limit: Math.Max(topK * 4, 20), ct);

        if (candidates.Count == 0)
        {
            sw.Stop();
            const string gateProvider = "PreLlmGate (EmptyIndex)";
            await LogAuditAsync(tenantId, question, grounded: false, topScore: 0, citedCount: 0, sw.ElapsedMilliseconds, gateProvider, ct);
            return new RagQueryResponse(
                question,
                "INSUFFICIENT_GROUNDING: No documents are indexed for this tenant.",
                Grounded: false,
                TopSimilarityScore: 0,
                ProviderUsed: gateProvider,
                LatencyMs: sw.ElapsedMilliseconds,
                Citations: []);
        }

        var queryTerms = TokenizeKeywords(question);
        var withKeyword = candidates
            .Select(c => new
            {
                Candidate = c,
                KeywordScore = ComputeKeywordScore(c.Content, c.DocumentTitle, queryTerms)
            })
            .ToList();

        var vectorRanked = withKeyword
            .OrderByDescending(x => x.Candidate.VectorSimilarity)
            .Select((x, rank) => (x.Candidate.ChunkId, Rank: rank + 1))
            .ToDictionary(x => x.ChunkId, x => x.Rank);

        var keywordRanked = withKeyword
            .OrderByDescending(x => x.KeywordScore)
            .ThenByDescending(x => x.Candidate.VectorSimilarity)
            .Select((x, rank) => (x.Candidate.ChunkId, Rank: rank + 1))
            .ToDictionary(x => x.ChunkId, x => x.Rank);

        const double rrfK = 60.0;
        var fused = withKeyword
            .Select(x =>
            {
                var vRank = vectorRanked[x.Candidate.ChunkId];
                var kRank = keywordRanked[x.Candidate.ChunkId];
                var rrf = (1.0 / (rrfK + vRank)) + (x.KeywordScore > 0 ? (1.0 / (rrfK + kRank)) : 0.0);
                return new RetrievedChunkContext(
                    x.Candidate.ChunkId,
                    x.Candidate.DocumentTitle,
                    x.Candidate.SourceUri,
                    x.Candidate.ChunkIndex,
                    x.Candidate.Content,
                    Math.Round(x.Candidate.VectorSimilarity, 4),
                    Math.Round(x.KeywordScore, 4),
                    Math.Round(rrf, 6));
            })
            .OrderByDescending(x => x.RrfScore)
            .Take(topK)
            .ToList();

        var topBest = fused[0];
        var strongKeywordMatch = (topBest.KeywordScore >= 0.45 && topBest.VectorSimilarity >= 0.30) || (topBest.KeywordScore >= 0.25 && topBest.VectorSimilarity >= 0.58);
        var passesThreshold = topBest.VectorSimilarity >= req.MinRelevanceScore || strongKeywordMatch;

        if (!passesThreshold)
        {
            sw.Stop();
            const string gateProvider = "PreLlmGate (SubThreshold)";
            await LogAuditAsync(tenantId, question, grounded: false, topBest.VectorSimilarity, 0, sw.ElapsedMilliseconds, gateProvider, ct);
            return new RagQueryResponse(
                question,
                "INSUFFICIENT_GROUNDING: Retrieved passages fell below the confidence threshold; refusing to speculate.",
                Grounded: false,
                TopSimilarityScore: topBest.VectorSimilarity,
                ProviderUsed: gateProvider,
                LatencyMs: sw.ElapsedMilliseconds,
                Citations: []);
        }

        var rawAnswer = await chat.GenerateGroundedAnswerAsync(question, fused, ct);
        var modelRefused = rawAnswer.StartsWith("INSUFFICIENT_GROUNDING", StringComparison.OrdinalIgnoreCase);

        sw.Stop();
        var citations = modelRefused
            ? []
            : fused.Where(c => c.VectorSimilarity >= 0.25 || c.KeywordScore > 0).Select(c => new CitationSourceDto(
                c.ChunkId,
                c.DocumentTitle,
                c.SourceUri,
                c.ChunkIndex,
                c.Content.Length > 240 ? c.Content[..240] + "..." : c.Content,
                c.VectorSimilarity,
                c.KeywordScore,
                c.RrfScore)).ToList();

        await LogAuditAsync(tenantId, question, grounded: !modelRefused, topBest.VectorSimilarity, citations.Count, sw.ElapsedMilliseconds, chat.ProviderName, ct);

        return new RagQueryResponse(
            question,
            rawAnswer,
            Grounded: !modelRefused,
            TopSimilarityScore: topBest.VectorSimilarity,
            ProviderUsed: chat.ProviderName,
            LatencyMs: sw.ElapsedMilliseconds,
            Citations: citations);
    }

    private async Task<List<VectorRow>> FetchVectorCandidatesAsync(string tenantId, string queryVectorJson, int limit, CancellationToken ct)
    {
        var results = new List<VectorRow>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP (@Limit)
                c.Id,
                d.Title,
                d.SourceUri,
                c.ChunkIndex,
                c.Content,
                1.0 - VECTOR_DISTANCE('cosine', c.Embedding, CAST(@QueryVec AS VECTOR(768))) AS CosineSimilarity
            FROM DocumentChunks c
            INNER JOIN KnowledgeDocuments d ON c.DocumentId = d.Id AND d.TenantId = @TenantId
            WHERE c.TenantId = @TenantId
            ORDER BY VECTOR_DISTANCE('cosine', c.Embedding, CAST(@QueryVec AS VECTOR(768))) ASC;";

        cmd.Parameters.Add(new SqlParameter("@Limit", limit));
        cmd.Parameters.Add(new SqlParameter("@TenantId", tenantId));
        cmd.Parameters.Add(new SqlParameter("@QueryVec", queryVectorJson));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new VectorRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5)));
        }

        return results;
    }

    private async Task LogAuditAsync(string tenantId, string question, bool grounded, double topScore, int citedCount, long latencyMs, string providerUsed, CancellationToken ct)
    {
        db.QueryAuditLogs.Add(new QueryAuditLog
        {
            TenantId = tenantId,
            Question = question.Length > 2000 ? question[..2000] : question,
            Grounded = grounded,
            TopSimilarityScore = Math.Round(topScore, 4),
            CitedChunkCount = citedCount,
            LatencyMs = latencyMs,
            ProviderUsed = providerUsed
        });
        await db.SaveChangesAsync(ct);
    }

    private static string SerializeVector(float[] vector)
    {
        var sb = new StringBuilder(vector.Length * 10);
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString("G7", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static HashSet<string> TokenizeKeywords(string text)
    {
        var stopWords = new HashSet<string>(["the", "is", "at", "which", "on", "for", "and", "or", "to", "in", "of", "a", "an", "what", "how", "does", "do", "are", "who", "when", "where", "why"]);
        return text.ToLowerInvariant()
            .Split([' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !stopWords.Contains(t))
            .ToHashSet();
    }

    private static double ComputeKeywordScore(string content, string title, HashSet<string> queryTerms)
    {
        if (queryTerms.Count == 0) return 0.0;
        var haystack = $"{title} {content}".ToLowerInvariant();
        int hits = 0;
        foreach (var term in queryTerms)
        {
            if (haystack.Contains(term, StringComparison.Ordinal))
                hits++;
        }
        return (double)hits / queryTerms.Count;
    }

    private sealed record VectorRow(Guid ChunkId, string DocumentTitle, string SourceUri, int ChunkIndex, string Content, double VectorSimilarity);
}




