using System.Collections.Generic;
using System.Linq;

namespace DevToolbox.Services.Models
{
    // Structured search built in the UI query builder. User text is always bound as SQL parameters.
    public class LogSearchCriteria
    {
        // Keyword-group builder: each group's terms are OR'd; groups are combined left-to-right by Gate.
        public List<KeywordGroup> Groups { get; set; } = new();

        // Advanced mode: a safe boolean expression (quoted terms + AND / OR / NOT / parentheses).
        public bool UseAdvanced { get; set; }
        public string? AdvancedExpression { get; set; }

        public bool HasContent =>
            (UseAdvanced && !string.IsNullOrWhiteSpace(AdvancedExpression)) ||
            (!UseAdvanced && Groups.Any(g => g.Terms.Any(t => !string.IsNullOrWhiteSpace(t))));
    }

    public class KeywordGroup
    {
        // How this group combines with the running predicate: AND | OR | NOT.
        public string Gate { get; set; } = "AND";

        // Interchangeable terms; a group matches when ANY term is contained in ANY column.
        public List<string> Terms { get; set; } = new();
    }
}
