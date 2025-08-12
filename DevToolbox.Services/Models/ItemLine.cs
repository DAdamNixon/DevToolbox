using System;

namespace DevToolbox.Services.Models
{
    /// <summary>
    /// Represents a single parsed log line from an archived log file.
    /// </summary>
    public class LogLine
    {
        public string Server { get; set; } = "";
        public DateTime TimeStamp { get; set; }
        public string UserGUID { get; set; } = "";
        public string UserIP { get; set; } = "";
        public string AccountID { get; set; } = "";
        public int UserID { get; set; }
        public string Type { get; set; } = "";
        public int JobSeq { get; set; }
        public int StoreNumber { get; set; }
        public string[] Messages { get; set; } = Array.Empty<string>();
    }
}