namespace Saga.Core.Abstractions;

/// <summary>Stores original uploaded files. Local disk in dev, Azure Blob in production.</summary>
public interface IFileStorage
{
    /// <summary>Saves a file and returns the storage path to persist on the document.</summary>
    Task<string> SaveAsync(Guid proposalId, string fileName, Stream content, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);
}
