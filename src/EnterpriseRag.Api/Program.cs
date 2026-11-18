using EnterpriseRag.Api.Data;
using EnterpriseRag.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddRouting(o => o.LowercaseUrls = true);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
              ?? "Server=localhost,1433;Database=EnterpriseRagDb;User Id=sa;Password=Your_Strong_Password123!;TrustServerCertificate=True;";

builder.Services.AddDbContext<RagDbContext>(o => o.UseSqlServer(connStr));

var geminiOptions = new GeminiOptions
{
    ApiKey = builder.Configuration["Gemini:ApiKey"],
    EmbeddingModel = builder.Configuration["Gemini:EmbeddingModel"] ?? "gemini-embedding-001",
    ChatModel = builder.Configuration["Gemini:ChatModel"] ?? "gemini-2.5-flash",
    Dimensions = 768
};
builder.Services.AddSingleton(geminiOptions);
builder.Services.AddHttpClient<IEmbeddingProvider, GeminiEmbeddingProvider>();
builder.Services.AddHttpClient<IChatCompletionProvider, GeminiChatCompletionProvider>();
builder.Services.AddScoped<RagEngineService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
    if (db.Database.IsSqlServer())
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'KnowledgeDocuments', N'U') IS NULL
            BEGIN
                CREATE TABLE KnowledgeDocuments (
                    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    TenantId NVARCHAR(64) NOT NULL,
                    Title NVARCHAR(256) NOT NULL,
                    SourceUri NVARCHAR(512) NOT NULL,
                    ContentHash NVARCHAR(64) NOT NULL,
                    ChunkCount INT NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL
                );
                CREATE UNIQUE INDEX IX_KnowledgeDocuments_TenantId_ContentHash ON KnowledgeDocuments (TenantId, ContentHash);
            END;

            IF OBJECT_ID(N'DocumentChunks', N'U') IS NULL
            BEGIN
                CREATE TABLE DocumentChunks (
                    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    DocumentId UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_DocumentChunks_Documents FOREIGN KEY REFERENCES KnowledgeDocuments(Id) ON DELETE CASCADE,
                    TenantId NVARCHAR(64) NOT NULL,
                    ChunkIndex INT NOT NULL,
                    Content NVARCHAR(MAX) NOT NULL,
                    TokenEstimate INT NOT NULL,
                    Embedding VECTOR(768) NOT NULL
                );
                CREATE INDEX IX_DocumentChunks_TenantId_DocumentId ON DocumentChunks (TenantId, DocumentId);
            END;

            IF OBJECT_ID(N'QueryAuditLogs', N'U') IS NULL
            BEGIN
                CREATE TABLE QueryAuditLogs (
                    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    TenantId NVARCHAR(64) NOT NULL,
                    Question NVARCHAR(2000) NOT NULL,
                    Grounded BIT NOT NULL,
                    TopSimilarityScore FLOAT NOT NULL,
                    CitedChunkCount INT NOT NULL,
                    LatencyMs BIGINT NOT NULL,
                    ProviderUsed NVARCHAR(64) NOT NULL,
                    ExecutedAtUtc DATETIMEOFFSET NOT NULL
                );
                CREATE INDEX IX_QueryAuditLogs_TenantId_ExecutedAtUtc ON QueryAuditLogs (TenantId, ExecutedAtUtc);
            END;");
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
