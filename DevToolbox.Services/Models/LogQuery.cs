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
        public int? Page { get; set; }
        public int? PageSize { get; set; }
    }
}
