namespace Parsek.Tests.LogValidation
{
    internal sealed class LogViolation
    {
        public LogViolation(string code, int lineNumber, string message, string rawLine)
        {
            Code = code;
            LineNumber = lineNumber;
            Message = message;
            RawLine = rawLine ?? string.Empty;
        }

        public string Code { get; }
        public int LineNumber { get; }
        public string Message { get; }
        public string RawLine { get; }

        public string ToDisplayString()
        {
            return $"[{Code}] line {LineNumber}: {Message}{System.Environment.NewLine}  {RawLine}";
        }
    }
}
