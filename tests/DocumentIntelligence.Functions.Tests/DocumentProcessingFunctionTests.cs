using System.Text.Json;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Functions;
using DocumentIntelligence.Functions.Services;
using DocumentIntelligence.Infrastructure.Data;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DocumentIntelligence.Functions.Tests;

public class DocumentProcessingFunctionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IDocumentExtractionService _extractionService;
    private readonly ILogger<DocumentProcessingFunction> _logger;
    private readonly DocumentProcessingFunction _sut;

    public DocumentProcessingFunctionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new SqliteAppDbContext(options);
        _db.Database.EnsureCreated();

        _documentRepository = Substitute.For<IDocumentRepository>();
        _blobStorageService = Substitute.For<IBlobStorageService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _configuration = Substitute.For<IConfiguration>();
        _extractionService = Substitute.For<IDocumentExtractionService>();
        _logger = Substitute.For<ILogger<DocumentProcessingFunction>>();

        // Provide a no-op HTTP client for API notify calls
        var httpClient = new HttpClient(new NoOpHttpMessageHandler())
        {
            BaseAddress = new Uri("http://localhost")
        };
        _httpClientFactory.CreateClient("apiservice").Returns(httpClient);

        _sut = new DocumentProcessingFunction(
            _db,
            _documentRepository,
            _blobStorageService,
            _httpClientFactory,
            _configuration,
            _extractionService,
            _logger);
    }

    // ── Invalid JSON message handling ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_InvalidJson_LogsErrorAndReturnsWithoutThrowing()
    {
        var context = Substitute.For<FunctionContext>();

        // Should return silently, not throw (so Service Bus abandons rather than errors out)
        var act = async () => await _sut.RunAsync("not-valid-json{{{{", context, CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _documentRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<Guid>(), Arg.Any<DocumentStatus>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NullDeserializedMessage_ThrowsInvalidOperationException()
    {
        var context = Substitute.For<FunctionContext>();
        // "null" is valid JSON but deserialises to null — function throws for null message body
        var act = async () => await _sut.RunAsync("null", context, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Successful processing ─────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ValidMessage_UpdatesStatusToProcessingThenCompleted()
    {
        var docId = Guid.NewGuid();
        var message = new DocumentProcessingMessage(
            docId, "blob/doc.pdf", "doc.pdf", "application/pdf", "user-1");

        // Seed a document so the DB insert of ExtractionResult has a valid FK
        _db.Documents.Add(new Document
        {
            Id = docId,
            FileName = "doc.pdf",
            BlobPath = "blob/doc.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
            Status = DocumentStatus.Pending,
            UploadedByUserId = "user-1"
        });
        await _db.SaveChangesAsync();

        _blobStorageService.DownloadBytesAsync("blob/doc.pdf", Arg.Any<CancellationToken>())
            .Returns([1, 2, 3]);
        _extractionService.ExtractAsync(Arg.Any<byte[]>(), "application/pdf", "doc.pdf", Arg.Any<CancellationToken>())
            .Returns("{\"field\":\"value\"}");
        _extractionService.ModelName.Returns("test-model");

        var context = Substitute.For<FunctionContext>();
        var json = JsonSerializer.Serialize(message);

        await _sut.RunAsync(json, context, CancellationToken.None);

        await _documentRepository.Received(1).UpdateStatusAsync(
            docId, DocumentStatus.Processing, null, Arg.Any<CancellationToken>());
        await _documentRepository.Received(1).UpdateStatusAsync(
            docId, DocumentStatus.Completed, null, Arg.Any<CancellationToken>());

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result.Should().NotBeNull();
        result!.ExtractedJson.Should().Be("{\"field\":\"value\"}");
    }

    // ── Processing failure handling ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_ExtractionThrows_UpdatesStatusToFailedAndRethrows()
    {
        var docId = Guid.NewGuid();
        var message = new DocumentProcessingMessage(
            docId, "blob/doc.pdf", "doc.pdf", "application/pdf", "user-1");

        _db.Documents.Add(new Document
        {
            Id = docId,
            FileName = "doc.pdf",
            BlobPath = "blob/doc.pdf",
            ContentType = "application/pdf",
            FileSize = 512,
            Status = DocumentStatus.Pending,
            UploadedByUserId = "user-1"
        });
        await _db.SaveChangesAsync();

        _blobStorageService.DownloadBytesAsync("blob/doc.pdf", Arg.Any<CancellationToken>())
            .Returns([1]);
        _extractionService.ExtractAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Ollama is not reachable."));

        var context = Substitute.For<FunctionContext>();
        var json = JsonSerializer.Serialize(message);

        // Should rethrow so Service Bus can dead-letter after max delivery count
        var act = async () => await _sut.RunAsync(json, context, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await _documentRepository.Received(1).UpdateStatusAsync(
            docId, DocumentStatus.Failed, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── NotifyApiAsync — failure is non-fatal ─────────────────────────────

    [Fact]
    public async Task RunAsync_NotifyApiFails_DoesNotBubbleUpException()
    {
        var docId = Guid.NewGuid();
        var message = new DocumentProcessingMessage(
            docId, "blob/doc.pdf", "doc.pdf", "application/pdf", "user-1");

        _db.Documents.Add(new Document
        {
            Id = docId,
            FileName = "doc.pdf",
            BlobPath = "blob/doc.pdf",
            ContentType = "application/pdf",
            FileSize = 512,
            Status = DocumentStatus.Pending,
            UploadedByUserId = "user-1"
        });
        await _db.SaveChangesAsync();

        _blobStorageService.DownloadBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        _extractionService.ExtractAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("{}");
        _extractionService.ModelName.Returns("model");

        // Return a 401 from the notify call — function should log a warning, not throw
        var httpClient = new HttpClient(new AlwaysUnauthorizedHttpMessageHandler())
        {
            BaseAddress = new Uri("http://localhost")
        };
        _httpClientFactory.CreateClient("apiservice").Returns(httpClient);

        var context = Substitute.For<FunctionContext>();
        var json = JsonSerializer.Serialize(message);

        var act = async () => await _sut.RunAsync(json, context, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── ComputeConfidenceScore (via RunAsync) ────────────────────────────────

    [Fact]
    public async Task RunAsync_AllNonNullFields_SetsConfidenceToOne()
    {
        var docId = await SeedDocumentAsync();
        SetupExtractionResult(docId, "{\"fieldA\":\"value\",\"fieldB\":\"value2\"}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(1.0);
    }

    [Fact]
    public async Task RunAsync_HalfNullFields_SetsConfidenceToHalf()
    {
        var docId = await SeedDocumentAsync();
        SetupExtractionResult(docId, "{\"fieldA\":\"value\",\"fieldB\":null}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(0.5);
    }

    [Fact]
    public async Task RunAsync_AllNullFields_SetsConfidenceToZero()
    {
        var docId = await SeedDocumentAsync();
        SetupExtractionResult(docId, "{\"fieldA\":null,\"fieldB\":null}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(0.0);
    }

    [Fact]
    public async Task RunAsync_TruncatedResponse_AppliesTruncationPenalty()
    {
        var docId = await SeedDocumentAsync();
        // 2 non-null / 2 data fields = 1.0 raw, then * 0.7 = 0.7
        SetupExtractionResult(docId, "{\"fieldA\":\"v\",\"fieldB\":\"v\",\"_truncated\":true}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(0.7);
    }

    [Fact]
    public async Task RunAsync_OnlyMetadataAndReasoningKeys_SetsConfidenceToZero()
    {
        var docId = await SeedDocumentAsync();
        // _metadata and _reasoning are excluded; no remaining data fields → 0.0
        SetupExtractionResult(docId, "{\"_metadata\":{\"documentType\":\"W-2\"},\"_reasoning\":\"Based on layout\"}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(0.0);
    }

    [Fact]
    public async Task RunAsync_InvalidJsonResponse_SetsConfidenceToZero()
    {
        var docId = await SeedDocumentAsync();
        // Extraction service returns unparseable text — ComputeConfidenceScore returns 0.0
        SetupExtractionResult(docId, "not valid json at all");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(0.0);
    }

    [Fact]
    public async Task RunAsync_MetadataExcludedFromConfidenceCalculation_OnlyDataFieldsCounted()
    {
        var docId = await SeedDocumentAsync();
        // _metadata is excluded; 1 non-null + 1 null data field → 0.5
        SetupExtractionResult(docId, "{\"_metadata\":{},\"fieldA\":\"value\",\"fieldB\":null}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ConfidenceScore.Should().Be(0.5);
    }

    // ── ProcessingDurationMs (via RunAsync) ──────────────────────────────────

    [Fact]
    public async Task RunAsync_SuccessfulProcessing_SetsDurationMsToNonNegativeValue()
    {
        var docId = await SeedDocumentAsync();
        SetupExtractionResult(docId, "{\"field\":\"value\"}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        result!.ProcessingDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task RunAsync_SuccessfulProcessing_StoresDurationOnExtractionResult()
    {
        var docId = await SeedDocumentAsync();
        SetupExtractionResult(docId, "{\"field\":\"value\"}");

        await RunFunctionAsync(docId);

        var result = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.DocumentId == docId);
        // Duration is stored on ExtractionResult, not on Document
        result.Should().NotBeNull();
        result!.ProcessingDurationMs.Should().BeGreaterThanOrEqualTo(0);
        result.ConfidenceScore.Should().BeGreaterThan(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedDocumentAsync(string userId = "user-1")
    {
        var docId = Guid.NewGuid();
        _db.Documents.Add(new Document
        {
            Id = docId,
            FileName = "doc.pdf",
            BlobPath = "blob/doc.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
            Status = DocumentStatus.Pending,
            UploadedByUserId = userId
        });
        await _db.SaveChangesAsync();
        return docId;
    }

    private void SetupExtractionResult(Guid docId, string extractedJson)
    {
        _blobStorageService
            .DownloadBytesAsync("blob/doc.pdf", Arg.Any<CancellationToken>())
            .Returns([1, 2, 3]);
        _extractionService
            .ExtractAsync(Arg.Any<byte[]>(), "application/pdf", "doc.pdf", Arg.Any<CancellationToken>())
            .Returns(extractedJson);
        _extractionService.ModelName.Returns("test-model");
    }

    private async Task RunFunctionAsync(Guid docId)
    {
        var message = new DocumentProcessingMessage(
            docId, "blob/doc.pdf", "doc.pdf", "application/pdf", "user-1");
        var context = Substitute.For<FunctionContext>();
        await _sut.RunAsync(JsonSerializer.Serialize(message), context, CancellationToken.None);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Test HTTP handlers ────────────────────────────────────────────────

    private sealed class NoOpHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class AlwaysUnauthorizedHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
    }
}
