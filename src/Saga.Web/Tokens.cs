namespace Saga.Web;

/// <summary>
/// Formats the token counts stored on usage rows for display, the way Money formats their cost.
/// </summary>
public static class Tokens
{
    /// <summary>
    /// Renders the cached slice of a call's input tokens. Cached tokens are part of the input count,
    /// never additional to it, so they are always shown as a share of it — the share is the number
    /// that says whether prompt caching is working at all. "—" when no input was billed, which is
    /// every Content Understanding row and any call the provider sent no usage for.
    /// </summary>
    public static string Cached(long cachedTokens, long inputTokens)
        => inputTokens <= 0 ? "—" : $"{cachedTokens:N0} ({(double)cachedTokens / inputTokens:P0})";
}
