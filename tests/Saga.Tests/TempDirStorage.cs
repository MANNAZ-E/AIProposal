using Saga.Core.Abstractions;

namespace Saga.Tests;

/// <summary>File storage backed by a throwaway directory, shared by the tests that upload.</summary>
internal sealed class TempDirStorage(string root) : IFileStorage
{
    public async Task<string> SaveAsync(Guid proposalId, string fileName, Stream content, CancellationToken ct = default)
    {
        var relative = Path.Combine(proposalId.ToString("N"), $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}");
        var fullPath = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);
        return relative.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream>(File.OpenRead(Path.Combine(root, path)));

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(root, path);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }
}
