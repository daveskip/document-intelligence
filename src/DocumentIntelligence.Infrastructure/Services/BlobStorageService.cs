using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DocumentIntelligence.Infrastructure.Services;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default);
    Task<byte[]> DownloadBytesAsync(string blobPath, CancellationToken ct = default);
    Task DeleteAsync(string blobPath, CancellationToken ct = default);
}

public class BlobStorageService(BlobServiceClient blobServiceClient) : IBlobStorageService
{
    private const string ContainerName = "documents";

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        var container = blobServiceClient.GetBlobContainerClient(ContainerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        return container;
    }

    public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blobName = $"{Guid.NewGuid():N}/{fileName}";
        var blob = container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        return blobName;
    }

    public async Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(blobPath);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task<byte[]> DownloadBytesAsync(string blobPath, CancellationToken ct = default)
    {
        using var stream = await DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(blobPath);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }
}
