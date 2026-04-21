using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Infrastructure.Data;
using DocumentIntelligence.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DocumentIntelligence.Infrastructure.Tests.Repositories;

public class DocumentRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly DocumentRepository _sut;

    public DocumentRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new SqliteAppDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new DocumentRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsDocument()
    {
        var doc = MakeDocument();
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(doc.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(doc.Id);
        result.FileName.Should().Be(doc.FileName);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistingId_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_IncludesExtractionResult_WhenPresent()
    {
        var doc = MakeDocument();
        var extraction = new ExtractionResult
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            ExtractedJson = "{}",
            ModelVersion = "test",
            ProcessedAt = DateTimeOffset.UtcNow
        };
        _db.Documents.Add(doc);
        _db.ExtractionResults.Add(extraction);
        await _db.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(doc.Id);

        result!.ExtractionResult.Should().NotBeNull();
        result.ExtractionResult!.ExtractedJson.Should().Be("{}");
    }

    // ── GetPagedAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagedAsync_ReturnsOnlyDocumentsForUser()
    {
        var userId = "user-1";
        _db.Documents.AddRange(
            MakeDocument(userId: userId),
            MakeDocument(userId: userId),
            MakeDocument(userId: "other-user"));
        await _db.SaveChangesAsync();

        var (items, totalCount) = await _sut.GetPagedAsync(userId, page: 1, pageSize: 10);

        items.Should().HaveCount(2);
        totalCount.Should().Be(2);
        items.Should().OnlyContain(d => d.UploadedByUserId == userId);
    }

    [Fact]
    public async Task GetPagedAsync_RespectsPageSizeAndPage()
    {
        var userId = "user-paged";
        for (int i = 0; i < 5; i++)
            _db.Documents.Add(MakeDocument(userId: userId));
        await _db.SaveChangesAsync();

        var (items, totalCount) = await _sut.GetPagedAsync(userId, page: 2, pageSize: 2);

        items.Should().HaveCount(2);
        totalCount.Should().Be(5);
    }

    [Fact]
    public async Task GetPagedAsync_EmptyResult_WhenNoDocuments()
    {
        var (items, totalCount) = await _sut.GetPagedAsync("nobody", 1, 10);
        items.Should().BeEmpty();
        totalCount.Should().Be(0);
    }

    // ── AddAsync / SaveChangesAsync ───────────────────────────────────────

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsDocument()
    {
        var doc = MakeDocument();

        await _sut.AddAsync(doc);
        await _sut.SaveChangesAsync();

        var stored = await _db.Documents.FindAsync(doc.Id);
        stored.Should().NotBeNull();
        stored!.FileName.Should().Be(doc.FileName);
    }

    [Fact]
    public async Task AddAsync_DoesNotPersist_UntilSaveChanges()
    {
        var doc = MakeDocument();
        await _sut.AddAsync(doc);

        // Without SaveChanges the in-memory store may have it tracked but not "visible"
        // via a fresh query — use AsNoTracking to simulate an uncommitted state.
        var count = await _db.Documents.AsNoTracking().CountAsync();
        count.Should().Be(0);
    }

    // ── UpdateStatusAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatusAsync_ChangesStatusAndErrorMessage()
    {
        var doc = MakeDocument();
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UpdateStatusAsync(doc.Id, DocumentStatus.Failed, "some error");

        var updated = await _db.Documents.FindAsync(doc.Id);
        updated!.Status.Should().Be(DocumentStatus.Failed);
        updated.ErrorMessage.Should().Be("some error");
    }

    [Fact]
    public async Task UpdateStatusAsync_ClearsErrorMessage_WhenNullPassed()
    {
        var doc = MakeDocument(errorMessage: "old error");
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UpdateStatusAsync(doc.Id, DocumentStatus.Completed, null);

        var updated = await _db.Documents.FindAsync(doc.Id);
        updated!.ErrorMessage.Should().BeNull();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesDocument()
    {
        var doc = MakeDocument();
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.DeleteAsync(doc.Id);

        var found = await _db.Documents.FindAsync(doc.Id);
        found.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistingId_DoesNotThrow()
    {
        Func<Task> act = () => _sut.DeleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Document MakeDocument(
        string userId = "user-1",
        string? errorMessage = null) => new()
    {
        Id = Guid.NewGuid(),
        FileName = "test.pdf",
        BlobPath = $"{Guid.NewGuid():N}/test.pdf",
        ContentType = "application/pdf",
        FileSize = 1024,
        Status = DocumentStatus.Pending,
        UploadedByUserId = userId,
        UploadedAt = DateTimeOffset.UtcNow,
        ErrorMessage = errorMessage
    };
}
