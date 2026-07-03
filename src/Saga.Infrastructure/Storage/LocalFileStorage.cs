using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Storage;

/// <summary>Development storage on local disk under Storage:LocalPath (default ./App_Data/uploads).</summary>
public class LocalFileStorage(IConfiguration configuration) : IFileStorage
{
    private string Root => string.IsNullOrEmpty(configuration["Storage:LocalPath"])
        ? Path.Combine(AppContext.BaseDirectory, "App_Data", "uploads")
        : configuration["Storage:LocalPath"]!;

    public async Task<string> SaveAsync(Guid proposalId, string fileName, Stream content, CancellationToken ct = default)
    {
        // Unique prefix prevents collisions and path traversal via the original file name.
        var safeName = Path.GetFileName(fileName);
        var relative = Path.Combine(proposalId.ToString("N"), $"{Guid.NewGuid():N}_{safeName}");
        var fullPath = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);
        return relative.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream>(File.OpenRead(Path.Combine(Root, path)));

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(Root, path);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }
}
