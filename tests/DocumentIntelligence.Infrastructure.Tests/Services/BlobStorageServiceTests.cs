using DocumentIntelligence.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure;

namespace DocumentIntelligence.Infrastructure.Tests.Services;

public class BlobStorageServiceTests
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _containerClient;
    private readonly BlobClient _blobClient;
    private readonly BlobStorageService _sut;

    public BlobStorageServiceTests()
    {
        _blobServiceClient = Substitute.For<BlobServiceClient>();
        _containerClient = Substitute.For<BlobContainerClient>();
        _blobClient = Substitute.For<BlobClient>();

        _blobServiceClient.GetBlobContainerClient("documents").Returns(_containerClient);
        _containerClient.CreateIfNotExistsAsync(
            publicAccessType: PublicAccessType.None,
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(BlobsModelFactory.BlobContainerInfo(default, default), Substitute.For<Response>()));

        _sut = new BlobStorageService(_blobServiceClient);
    }

    [Fact]
    public async Task UploadAsync_ReturnsBlobNameContainingFileName()
    {
        _containerClient.GetBlobClient(Arg.Any<string>()).Returns(_blobClient);
        _blobClient.UploadAsync(
            Arg.Any<Stream>(),
            Arg.Any<BlobHttpHeaders>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(BlobsModelFactory.BlobContentInfo(default, default, default, default, default, default, 0), Substitute.For<Response>()));

        await using var stream = new MemoryStream([1, 2, 3]);
        var blobPath = await _sut.UploadAsync(stream, "document.pdf", "application/pdf");

        blobPath.Should().EndWith("/document.pdf");
        blobPath.Should().MatchRegex(@"^[a-f0-9]{32}/document\.pdf$");
    }

    [Fact]
    public async Task UploadAsync_GeneratesUniquePathsForSameFileName()
    {
        _containerClient.GetBlobClient(Arg.Any<string>()).Returns(_blobClient);
        _blobClient.UploadAsync(
            Arg.Any<Stream>(),
            Arg.Any<BlobHttpHeaders>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(BlobsModelFactory.BlobContentInfo(default, default, default, default, default, default, 0), Substitute.For<Response>()));

        await using var stream1 = new MemoryStream([1]);
        await using var stream2 = new MemoryStream([2]);
        var path1 = await _sut.UploadAsync(stream1, "same.pdf", "application/pdf");
        var path2 = await _sut.UploadAsync(stream2, "same.pdf", "application/pdf");

        path1.Should().NotBe(path2);
    }

    [Fact]
    public async Task DeleteAsync_CallsDeleteIfExistsOnBlobClient()
    {
        _containerClient.GetBlobClient("some/path.pdf").Returns(_blobClient);
        _blobClient.DeleteIfExistsAsync(cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(true, Substitute.For<Response>()));

        await _sut.DeleteAsync("some/path.pdf");

        await _blobClient.Received(1).DeleteIfExistsAsync(cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadBytesAsync_ReturnsStreamContents()
    {
        var expectedBytes = new byte[] { 10, 20, 30 };
        var memStream = new MemoryStream(expectedBytes);

        // BlobDownloadStreamingResult cannot be directly substituted for Content — use factory
        var downloadResult = BlobsModelFactory.BlobDownloadStreamingResult(content: memStream);
        var response = Response.FromValue(downloadResult, Substitute.For<Response>());

        _containerClient.GetBlobClient("some/file.pdf").Returns(_blobClient);
        _blobClient.DownloadStreamingAsync(cancellationToken: Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await _sut.DownloadBytesAsync("some/file.pdf");

        result.Should().BeEquivalentTo(expectedBytes);
    }
}
