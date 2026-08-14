using System.Globalization;
using System.Text.Json;

namespace DevToolbox.Services.Services;

/// <summary>
/// Reads a single scalar out of a JSON document by dotted path — <c>blue.status</c>,
/// <c>nodes[0].name</c>, <c>activeEnvironment</c>.
/// <para>
/// Deliberately not JSONPath. Service Pulse needs to name a field on a health
/// payload it knows nothing about; it does not need filters, wildcards or
/// recursive descent, and pulling in a dependency to get them would be paying for
/// a query language to do a lookup. Anything this cannot express is a signal that
/// the value wants a real parser, not that this should grow one.
/// </para>
/// </summary>
public static class JsonPathReader
{
    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="json"/>.
    /// Returns false — without throwing — for malformed JSON, a missing segment,
    /// an out-of-range index, or a value that is an object or array rather than a
    /// scalar. Callers treat all of those the same way: the detail is not shown.
    /// </summary>
    public static bool TryRead(string json, string path, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;

            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryStep(ref current, segment)) return false;
            }

            return TryScalar(current, out value);
        }
        catch (JsonException)
        {
            // Not JSON at all — an HTML error page, or a plain-text body. Expected
            // enough that it is not worth surfacing; the ping result stands on its
            // status code either way.
            return false;
        }
    }

    /// <summary>Walks one <c>name</c> or <c>name[0][1]</c> segment.</summary>
    private static bool TryStep(ref JsonElement current, string segment)
    {
        var bracket = segment.IndexOf('[');
        var name = bracket < 0 ? segment : segment[..bracket];

        if (name.Length > 0)
        {
            if (current.ValueKind != JsonValueKind.Object) return false;
            if (!current.TryGetProperty(name, out var next)) return false;
            current = next;
        }

        if (bracket < 0) return true;

        // Indices are applied left to right, so nodes[0][1] works.
        var rest = segment[bracket..];
        while (rest.Length > 0)
        {
            if (rest[0] != '[') return false;
            var close = rest.IndexOf(']');
            if (close < 0) return false;

            if (!int.TryParse(rest[1..close], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return false;
            if (current.ValueKind != JsonValueKind.Array) return false;
            if (index < 0 || index >= current.GetArrayLength()) return false;

            current = current[index];
            rest = rest[(close + 1)..];
        }

        return true;
    }

    private static bool TryScalar(JsonElement element, out string value)
    {
        value = string.Empty;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                value = element.GetRawText();
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetBoolean() ? "true" : "false";
                return true;
            default:
                // Objects, arrays and null have no sensible one-line rendering.
                return false;
        }
    }
}
