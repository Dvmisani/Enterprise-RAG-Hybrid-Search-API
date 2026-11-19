using EnterpriseRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseRag.Api.Services;

public interface ITenantContext
{
    string TenantId { get; }
    bool IsAuthenticated { get; }
    void SetTenant(string tenantId);
}

public sealed class HttpTenantContext : ITenantContext
{
    public string TenantId { get; private set; } = string.Empty;
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(TenantId);

    public void SetTenant(string tenantId)
    {
        TenantId = tenantId;
    }
}

public sealed class TenantApiKeyOptions
{
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal)
    {
        ["demo-public-key-2026"] = "acme-fintech-za",
        ["alpha-secret-key"] = "tenant-alpha",
        ["beta-secret-key"] = "tenant-beta"
    };
}

public sealed class TenantAuthMiddleware(RequestDelegate next, TenantApiKeyOptions keyOptions)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var rawKey) ||
            string.IsNullOrWhiteSpace(rawKey) ||
            !keyOptions.Keys.TryGetValue(rawKey.ToString().Trim(), out var resolvedTenant))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Unauthorized: Provide a valid X-Api-Key header mapped to an authorized tenant."
            });
            return;
        }

        tenantContext.SetTenant(resolvedTenant);
        await next(context);
    }
}

public sealed class DemoTenantCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<DemoTenantCleanupService> logger) : BackgroundService
{
    public const string DemoTenantId = "acme-fintech-za";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                await PurgeExpiredDemoDataAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Demo tenant cleanup cycle encountered a transient error.");
            }
        }
    }

    public async Task PurgeExpiredDemoDataAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tenantCtx = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantCtx.SetTenant(DemoTenantId);

        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);

        var staleDocs = await db.Documents
            .Where(d => d.CreatedAtUtc < cutoff)
            .ToListAsync(ct);

        if (staleDocs.Count > 0)
        {
            db.Documents.RemoveRange(staleDocs);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} expired demo documents for {Tenant}.", staleDocs.Count, DemoTenantId);
        }

        var staleLogs = await db.QueryAuditLogs
            .Where(l => l.ExecutedAtUtc < cutoff)
            .ToListAsync(ct);

        if (staleLogs.Count > 0)
        {
            db.QueryAuditLogs.RemoveRange(staleLogs);
            await db.SaveChangesAsync(ct);
        }
    }
}
