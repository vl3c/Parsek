namespace Parsek.Tests.LogValidation
{
    internal sealed class KspLogEntry
    {
        public KspLogEntry(
            int lineNumber,
            string rawLine,
            bool isStructured,
            string level,
            string subsystem,
            string message)
        {
            LineNumber = lineNumber;
            RawLine = rawLine ?? string.Empty;
            IsStructured = isStructured;
            Level = level;
            Subsystem = subsystem;
            Message = message;
        }

        public int LineNumber { get; }
        public string RawLine { get; }
        public bool IsStructured { get; }
        public string Level { get; }
        public string Subsystem { get; }
        public string Message { get; }
    }
}
