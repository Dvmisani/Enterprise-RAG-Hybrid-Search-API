using EnterpriseRag.Api.Domain;
using EnterpriseRag.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseRag.Api.Data;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    private readonly ITenantContext _tenant = tenantContext;

    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<QueryAuditLog> QueryAuditLogs => Set<QueryAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeDocument>(b =>
        {
            b.ToTable("KnowledgeDocuments");
            b.HasKey(x => x.Id);
            b.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Title).HasMaxLength(256).IsRequired();
            b.Property(x => x.SourceUri).HasMaxLength(512).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ContentHash }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<DocumentChunk>(b =>
        {
            b.ToTable("DocumentChunks");
            b.HasKey(x => x.Id);
            b.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Content).IsRequired();
            b.Ignore(x => x.EmbeddingJson);
            b.HasIndex(x => new { x.TenantId, x.DocumentId });
            b.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            b.HasOne(x => x.Document)
             .WithMany(d => d.Chunks)
             .HasForeignKey(x => x.DocumentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QueryAuditLog>(b =>
        {
            b.ToTable("QueryAuditLogs");
            b.HasKey(x => x.Id);
            b.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Question).HasMaxLength(2000).IsRequired();
            b.Property(x => x.ProviderUsed).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ExecutedAtUtc });
            b.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });
    }
}
