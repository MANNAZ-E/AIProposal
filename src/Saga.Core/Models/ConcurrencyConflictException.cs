namespace Saga.Core.Models;

/// <summary>
/// Thrown when saving an artifact that someone else changed meanwhile. Carries the other
/// person's current content so the UI can show both versions — nothing is silently lost.
/// </summary>
public class ConcurrencyConflictException(
    string message,
    string? currentMarkdown,
    string? currentJson,
    byte[] currentRowVersion) : Exception(message)
{
    public string? CurrentMarkdown { get; } = currentMarkdown;
    public string? CurrentJson { get; } = currentJson;
    public byte[] CurrentRowVersion { get; } = currentRowVersion;
}
