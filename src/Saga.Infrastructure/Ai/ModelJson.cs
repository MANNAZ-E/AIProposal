using System.Text.Json;

namespace Saga.Infrastructure.Ai;

/// <summary>Tolerant parsing of JSON arrays and objects out of model output (fences, stray prose).</summary>
public static class ModelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static List<T> ParseArray<T>(string modelOutput)
    {
        var text = modelOutput.Trim();
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(text[start..(end + 1)], Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static T? ParseObject<T>(string modelOutput) where T : class
    {
        var text = modelOutput.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
