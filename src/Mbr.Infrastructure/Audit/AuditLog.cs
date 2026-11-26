using System.Text.Json;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;

namespace Mbr.Infrastructure.Audit;

public sealed class AuditLog(MbrDbContext db)
{
    public async Task WriteAsync(string actor, string action, Guid? tenantId = null, string? target = null,
        object? details = null, string? ip = null, CancellationToken ct = default)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            Actor = actor, Action = action, TenantId = tenantId, Target = target, IpAddress = ip,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await db.SaveChangesAsync(ct);
    }
}
