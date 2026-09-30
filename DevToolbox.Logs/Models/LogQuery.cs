using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevToolbox.Services.Models
{
    public class LogQuery
    {
        public Dictionary<string, object>? Filters { get; set; }
        public string? SearchTerm { get; set; }
        public LogSearchCriteria? Criteria { get; set; }
        // Full user-authored SQL SELECT (advanced mode); wrapped as a subquery for count/paging.
        public string? RawQuery { get; set; }
        public int? Page { get; set; }
        public int? PageSize { get; set; }

        // Add this property for sorting
        public List<SortColumn>? Sort { get; set; }

        /// <summary>
        /// When no sort resolves, order by <c>rowid ASC</c> instead of the usual
        /// <c>rowid DESC</c> — the order a materialized <c>results</c> table was collapsed in.
        /// Ignored once <see cref="Sort"/> resolves to anything, and ignored in SQL mode, where
        /// the inner query's own order (or lack of one) always decides.
        /// </summary>
        public bool InsertionOrder { get; set; }

        /// <summary>
        /// False skips the COUNT a search otherwise runs alongside its page. A caller that only
        /// wants rows — a page, a CSV — would pay a second full pass for a number it throws away,
        /// and with a keyword filter that pass evaluates LIKE on every column of every row.
        /// </summary>
        public bool IncludeCount { get; set; } = true;

        /// <summary>
        /// Return keyword-mode columns in the table's own order rather than with provenance moved to
        /// the end. For the <c>results</c> table, whose order a collapse already chose — display order
        /// for a keyword collapse, the SELECT's for a SQL one.
        /// </summary>
        public bool PhysicalColumnOrder { get; set; }
    }
}
