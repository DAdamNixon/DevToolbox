using System.Text.RegularExpressions;

namespace DevToolbox.Services.Models
{
    /// <summary>
    /// The columns the ingest invents for fields a line carries beyond what its template declares.
    /// <para>
    /// Named here rather than formatted at each use because there are two sites that have to agree
    /// exactly — the one that adds the columns to the table and the one that fills them in per row —
    /// and a template validator that has to recognise the result. A mismatch between the first two
    /// puts data in a column the table does not have.
    /// </para>
    /// <para>
    /// No space in the name, deliberately. These are the columns most likely to be typed by hand in
    /// the advanced SQL box, and <c>Message 1</c> has to be written <c>[Message 1]</c> every single
    /// time or the query is a syntax error. <c>Message1</c> needs no quoting.
    /// </para>
    /// </summary>
    public static class LogOverflowColumns
    {
        /// <summary><paramref name="ordinal"/> is 1-based: the first overflow field is Message1.</summary>
        public static string Name(int ordinal) => $"Message{ordinal}";

        /// <summary>
        /// Whether a name would collide with a generated column, so a template cannot declare one.
        /// <para>
        /// Matches the spaced and underscored forms too. They no longer collide with anything, but a
        /// template column called <c>Message 1</c> sitting beside a generated <c>Message1</c> is a
        /// trap worth refusing outright rather than explaining later.
        /// </para>
        /// </summary>
        public static bool IsGeneratedName(string? column) =>
            column is not null && GeneratedName.IsMatch(column.Trim());

        private static readonly Regex GeneratedName =
            new(@"^Message[ _]?\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
