using System;
using System.Collections.Generic;
using System.Linq;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>Why <see cref="DefaultTemplateResolver.Resolve"/> did or did not switch the template.</summary>
    public enum DefaultTemplateHint
    {
        /// <summary>Nothing to say — no selection, every location has no default, or one applies cleanly.</summary>
        None,

        /// <summary>The selected locations do not all name the same default. A location with none counts as disagreeing.</summary>
        Differ,

        /// <summary>Every selected location agrees on a default, but it does not name a known template.</summary>
        Missing
    }

    /// <summary>What the selected locations agree the template should be, if anything.</summary>
    public sealed record DefaultTemplateResolution(string? Template, DefaultTemplateHint Hint);

    /// <summary>
    /// Decides whether a location selection implies a template switch. Pure and static so the rule is
    /// testable on its own, apart from the state that calls it.
    /// <para>
    /// The reading of "every selected location agrees" is strict: a location with no default counts as
    /// disagreeing, the same as one naming a different template. This makes the switch predictable —
    /// adding a location can never silently pick a template it never named.
    /// </para>
    /// </summary>
    public static class DefaultTemplateResolver
    {
        public static DefaultTemplateResolution Resolve(
            IReadOnlyList<LogLocation> selected,
            IReadOnlyCollection<string> knownTemplates)
        {
            if (selected.Count == 0)
                return new DefaultTemplateResolution(null, DefaultTemplateHint.None);

            var defaults = selected.Select(l => (l.DefaultTemplate ?? "").Trim()).ToList();
            if (defaults.All(d => d.Length == 0))
                return new DefaultTemplateResolution(null, DefaultTemplateHint.None);

            var first = defaults[0];
            var allAgree = defaults.All(d => string.Equals(d, first, StringComparison.OrdinalIgnoreCase));
            if (!allAgree)
                return new DefaultTemplateResolution(null, DefaultTemplateHint.Differ);

            var canonical = knownTemplates.FirstOrDefault(t => string.Equals(t, first, StringComparison.OrdinalIgnoreCase));
            return canonical is not null
                ? new DefaultTemplateResolution(canonical, DefaultTemplateHint.None)
                : new DefaultTemplateResolution(null, DefaultTemplateHint.Missing);
        }
    }
}
