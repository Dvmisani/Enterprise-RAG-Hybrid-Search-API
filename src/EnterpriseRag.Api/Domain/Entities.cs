namespace EnterpriseRag.Api.Domain;

public sealed class KnowledgeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string Title { get; set; }
    public required string SourceUri { get; set; }
    public required string ContentHash { get; set; }
    public int ChunkCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<DocumentChunk> Chunks { get; set; } = [];
}

public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }
    public required string TenantId { get; set; }
    public int ChunkIndex { get; set; }
    public required string Content { get; set; }
    public int TokenEstimate { get; set; }
    public required string EmbeddingJson { get; set; }
}

public sealed class QueryAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantId { get; set; }
    public required string Question { get; set; }
    public bool Grounded { get; set; }
    public double TopSimilarityScore { get; set; }
    public int CitedChunkCount { get; set; }
    public long LatencyMs { get; set; }
    public required string ProviderUsed { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
