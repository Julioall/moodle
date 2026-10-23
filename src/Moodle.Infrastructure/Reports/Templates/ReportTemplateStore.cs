using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;

namespace Moodle.Infrastructure.Reports.Templates;

public sealed class ReportTemplateStore(ConnectorDbContext db) : IReportTemplateStore
{
    public async Task EnsureDefaultsAsync(Guid ownerId, IReadOnlyList<ReportTemplateInput> defaults, int limit, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owners = await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE \"Id\" = {ownerId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (owners.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var initialized = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO report_template_catalog_states ("OwnerId", "DefaultsSeededAt")
            VALUES ({ownerId}, {now})
            ON CONFLICT ("OwnerId") DO NOTHING
            """, ct);
        if (initialized == 0)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        var available = Math.Max(0, limit - await db.ReportTemplates.CountAsync(template => template.OwnerId == ownerId, ct));
        var entities = defaults.Take(available).Select(input => new ReportTemplateEntity
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = input.Name, Description = input.Description,
            SourceId = input.Definition.SourceId, DefinitionJson = Serialize(input.Definition),
            CreatedAt = now, UpdatedAt = now,
        }).ToArray();
        if (entities.Length > 0)
        {
            db.ReportTemplates.AddRange(entities);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<ReportTemplatePage> ListAsync(Guid ownerId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.ReportTemplates.AsNoTracking().Where(t => t.OwnerId == ownerId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items.Select(ToModel).ToArray(), page, pageSize, total);
    }

    public async Task<ReportTemplate?> GetAsync(Guid ownerId, Guid id, CancellationToken ct)
    {
        var entity = await db.ReportTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.OwnerId == ownerId && t.Id == id, ct);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<ReportTemplateMutation> CreateAsync(Guid ownerId, ReportTemplateInput input, int limit, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize the quota with account deletion and other creates; no HTTP inside this transaction.
        var owners = await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE \"Id\" = {ownerId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (owners.Count == 0) return new(ReportTemplateMutationStatus.NotFound);
        if (await db.ReportTemplates.CountAsync(t => t.OwnerId == ownerId, ct) >= limit)
            return new(ReportTemplateMutationStatus.LimitReached);
        var now = DateTimeOffset.UtcNow;
        var entity = new ReportTemplateEntity { Id = Guid.NewGuid(), OwnerId = ownerId,
            Name = input.Name, Description = input.Description, SourceId = input.Definition.SourceId,
            DefinitionJson = Serialize(input.Definition), CreatedAt = now, UpdatedAt = now };
        db.ReportTemplates.Add(entity);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        return new(ReportTemplateMutationStatus.Success, ToModel(entity));
    }

    public async Task<ReportTemplateMutation> UpdateAsync(Guid ownerId, Guid id, long expectedRevision, ReportTemplateInput input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var json = Serialize(input.Definition);
        var now = DateTimeOffset.UtcNow;
        var updated = await db.ReportTemplates.Where(t => t.OwnerId == ownerId && t.Id == id && t.Revision == expectedRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, input.Name).SetProperty(t => t.Description, input.Description)
                .SetProperty(t => t.SourceId, input.Definition.SourceId).SetProperty(t => t.DefinitionJson, json)
                .SetProperty(t => t.Revision, t => t.Revision + 1).SetProperty(t => t.UpdatedAt, now), ct);
        if (updated == 0) return await MissingOrConflict(ownerId, id, ct);
        var model = await GetAsync(ownerId, id, ct);
        await transaction.CommitAsync(ct);
        return new(ReportTemplateMutationStatus.Success, model);
    }

    public async Task<ReportTemplateMutation> DeleteAsync(Guid ownerId, Guid id, long expectedRevision, CancellationToken ct)
    {
        var deleted = await db.ReportTemplates.Where(t => t.OwnerId == ownerId && t.Id == id && t.Revision == expectedRevision)
            .ExecuteDeleteAsync(ct);
        return deleted == 1 ? new(ReportTemplateMutationStatus.Success) : await MissingOrConflict(ownerId, id, ct);
    }

    private async Task<ReportTemplateMutation> MissingOrConflict(Guid ownerId, Guid id, CancellationToken ct) =>
        new(await db.ReportTemplates.AnyAsync(t => t.OwnerId == ownerId && t.Id == id, ct)
            ? ReportTemplateMutationStatus.RevisionConflict : ReportTemplateMutationStatus.NotFound);
    private static string Serialize(ReportDefinition definition) => JsonSerializer.Serialize(definition, ReportDefinitionValidator.JsonOptions);
    private static ReportTemplate ToModel(ReportTemplateEntity entity) => new(entity.Id, entity.OwnerId, entity.Name,
        entity.Description, JsonSerializer.Deserialize<ReportDefinition>(entity.DefinitionJson, ReportDefinitionValidator.JsonOptions)!,
        entity.Revision, entity.CreatedAt, entity.UpdatedAt);
}
