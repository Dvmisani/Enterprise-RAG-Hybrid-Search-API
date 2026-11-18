using EnterpriseRag.Api.Data;
using EnterpriseRag.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseRag.Api.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(RagDbContext db, RagEngineService rag) : ControllerBase
{
    [HttpPost("ingest")]
    public async Task<ActionResult<IngestDocumentResponse>> Ingest(
        [FromHeader(Name = "X-Tenant-Id")] string? tenantId,
        [FromBody] IngestDocumentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "Missing required X-Tenant-Id header." });
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Title and Content are required." });

        var result = await rag.IngestAsync(tenantId.Trim(), request, ct);
        return result.Deduplicated ? Ok(result) : CreatedAtAction(nameof(List), new { }, result);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromHeader(Name = "X-Tenant-Id")] string? tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "Missing required X-Tenant-Id header." });

        var docs = await db.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId.Trim())
            .OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new { d.Id, d.Title, d.SourceUri, d.ContentHash, d.ChunkCount, d.CreatedAtUtc })
            .ToListAsync(ct);

        return Ok(docs);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete([FromHeader(Name = "X-Tenant-Id")] string? tenantId, Guid id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "Missing required X-Tenant-Id header." });

        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId.Trim(), ct);
        if (doc is null) return NotFound();

        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/query")]
public sealed class QueryController(RagDbContext db, RagEngineService rag) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<RagQueryResponse>> Query(
        [FromHeader(Name = "X-Tenant-Id")] string? tenantId,
        [FromBody] RagQueryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "Missing required X-Tenant-Id header." });
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required." });

        var result = await rag.QueryAsync(tenantId.Trim(), request, ct);
        return Ok(result);
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromHeader(Name = "X-Tenant-Id")] string? tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "Missing required X-Tenant-Id header." });

        var logs = await db.QueryAuditLogs
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId.Trim())
            .OrderByDescending(l => l.ExecutedAtUtc)
            .Take(25)
            .ToListAsync(ct);

        return Ok(logs);
    }
}
