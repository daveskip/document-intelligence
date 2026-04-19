using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentIntelligence.Infrastructure.Repositories;

public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<(IReadOnlyList<Document> Items, int TotalCount)> GetPagedAsync(string userId, int page, int pageSize, CancellationToken ct = default);
    Task AddAsync(Document document, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid id, DocumentStatus status, string? errorMessage = null, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public class DocumentRepository(AppDbContext db) : IDocumentRepository
{
    public Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Documents.Include(d => d.ExtractionResult).FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<(IReadOnlyList<Document> Items, int TotalCount)> GetPagedAsync(
        string userId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Documents
            .Where(d => d.UploadedByUserId == userId)
            .OrderByDescending(d => d.UploadedAt);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task AddAsync(Document document, CancellationToken ct = default)
    {
        await db.Documents.AddAsync(document, ct);
    }

    public async Task UpdateStatusAsync(Guid id, DocumentStatus status, string? errorMessage = null, CancellationToken ct = default)
    {
        await db.Documents
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.ErrorMessage, errorMessage), ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => db.SaveChangesAsync(ct);
}
