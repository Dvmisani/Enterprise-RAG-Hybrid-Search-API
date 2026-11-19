using EnterpriseRag.Api.Data;
using EnterpriseRag.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseRag.Api.Controllers;

[ApiController]
[Route("api/tenant")]
public sealed class TenantController(ITenantContext tenant) : ControllerBase
{
    [HttpGet("whoami")]
    public IActionResult WhoAmI() => Ok(new
    {
        tenantId = tenant.TenantId,
        autoWipeMinutes = tenant.TenantId == DemoTenantCleanupService.DemoTenantId ? 30 : (int?)null
    });
}

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(RagDbContext db, RagEngineService rag, ITenantContext tenant) : ControllerBase
{
    public const int MaxDocumentChars = 15_000;
    public const int MaxTitleChars = 200;

    [HttpPost("ingest")]
    public async Task<ActionResult<IngestDocumentResponse>> Ingest(
        [FromBody] IngestDocumentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Title and Content are required." });
        if (request.Title.Length > MaxTitleChars)
            return BadRequest(new { error = $"Title exceeds maximum length of {MaxTitleChars} characters." });
        if (request.Content.Length > MaxDocumentChars)
            return BadRequest(new { error = $"Document content exceeds demo quota limit of {MaxDocumentChars} characters." });

        var result = await rag.IngestAsync(tenant.TenantId, request, ct);
        return result.Deduplicated ? Ok(result) : CreatedAtAction(nameof(List), new { }, result);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var docs = await db.Documents
            .AsNoTracking()
            .OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new { d.Id, d.Title, d.SourceUri, d.ContentHash, d.ChunkCount, d.CreatedAtUtc })
            .ToListAsync(ct);

        return Ok(docs);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/query")]
public sealed class QueryController(RagDbContext db, RagEngineService rag, ITenantContext tenant) : ControllerBase
{
    public const int MaxQuestionChars = 500;

    [HttpPost]
    public async Task<ActionResult<RagQueryResponse>> Query(
        [FromBody] RagQueryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required." });
        if (request.Question.Length > MaxQuestionChars)
            return BadRequest(new { error = $"Question exceeds maximum length of {MaxQuestionChars} characters." });

        var result = await rag.QueryAsync(tenant.TenantId, request, ct);
        return Ok(result);
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(CancellationToken ct)
    {
        var logs = await db.QueryAuditLogs
            .AsNoTracking()
            .OrderByDescending(l => l.ExecutedAtUtc)
            .Take(25)
            .ToListAsync(ct);

        return Ok(logs);
    }
}
