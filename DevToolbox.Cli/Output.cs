using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToolbox.Cli;

/// <summary>Printing: JSON for scripts, aligned columns for people.</summary>
internal static class Output
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, Json));

    /// <summary>
    /// Rows as columns padded to their widest cell, each capped so one long message cannot push
    /// every other column off the screen. Pass <paramref name="widthCap"/> 0 for no cap.
    /// </summary>
    public static void WriteTable(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int widthCap = 60)
    {
        var all = rows.Select(r => r.Select(Clean).ToArray()).ToList();
        var widths = headers.Select((h, i) => all.Select(r => i < r.Length ? r[i].Length : 0).Prepend(h.Length).Max()).ToArray();
        if (widthCap > 0) widths = widths.Select(w => Math.Min(w, widthCap)).ToArray();

        Console.Out.WriteLine(Line(headers.ToArray(), widths));
        Console.Out.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in all) Console.Out.WriteLine(Line(row, widths));
    }

    private static string Line(string[] cells, int[] widths) =>
        string.Join("  ", widths.Select((w, i) => Fit(i < cells.Length ? cells[i] : "", w, last: i == widths.Length - 1)));

    private static string Fit(string text, int width, bool last)
    {
        if (text.Length > width) return text[..Math.Max(0, width - 1)] + "…";
        return last ? text : text.PadRight(width);
    }

    /// <summary>A cell on one line: a tab or newline inside a log message would break the columns.</summary>
    private static string Clean(string? text) =>
        (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
