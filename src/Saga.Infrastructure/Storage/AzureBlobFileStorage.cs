using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Storage;

/// <summary>
/// Production storage in Azure Blob. Authenticates with Managed Identity
/// (the App Service identity needs the "Storage Blob Data Contributor" role).
/// Configured by Storage:BlobServiceUri (+ optional Storage:Container, default "uploads").
/// </summary>
public class AzureBlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;

    public AzureBlobFileStorage(IConfiguration configuration)
    {
        var serviceUri = configuration["Storage:BlobServiceUri"]
            ?? throw new InvalidOperationException("Storage:BlobServiceUri is not configured.");
        var containerName = configuration["Storage:Container"] is { Length: > 0 } name ? name : "uploads";
        _container = new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential())
            .GetBlobContainerClient(containerName);
    }

    public async Task<string> SaveAsync(Guid proposalId, string fileName, Stream content,
        CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var safeName = Path.GetFileName(fileName);
        var path = $"{proposalId:N}/{Guid.NewGuid():N}_{safeName}";
        await _container.GetBlobClient(path).UploadAsync(content, overwrite: false, ct);
        return path;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => await _container.GetBlobClient(path).OpenReadAsync(cancellationToken: ct);

    public async Task DeleteAsync(string path, CancellationToken ct = default)
        => await _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);
}
