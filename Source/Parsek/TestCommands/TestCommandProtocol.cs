using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure wire-protocol primitives for the ParsekTestCommands seam (M-A2):
    /// the percent codec, the <see cref="ParsedCommand"/> result struct, and the
    /// <c>key=value</c> token splitter. No UnityEngine / KSP API usage — this
    /// type is exercised entirely by xUnit without a live Unity runtime.
    ///
    /// <para>Value encoding: a value that contains whitespace, <c>=</c>, <c>%</c>,
    /// or a control character is percent-encoded (<c>%20</c> for space, <c>%25</c>
    /// for <c>%</c>, <c>%3D</c> for <c>=</c>). Encoding is byte-oriented over the
    /// UTF-8 form of the string, so a multibyte character that needs escaping
    /// round-trips exactly. Numeric values use <see cref="CultureInfo.InvariantCulture"/>.</para>
    /// </summary>
    internal static class TestCommandProtocol
    {
        /// <summary>
        /// Percent-encodes <paramref name="value"/> for a single wire token.
        /// Encodes <c>%</c>, <c>=</c>, ASCII space, control chars (&lt;= 0x20 and
        /// 0x7F), and any other non-printable byte; all other bytes pass through
        /// literally. Byte-oriented over the UTF-8 form so it round-trips with
        /// <see cref="TryDecode"/>.
        /// </summary>
        internal static string Encode(string value)
        {
            if (value == null) return string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                if (NeedsEncoding(b))
                {
                    sb.Append('%');
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append((char)b);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Percent-decodes <paramref name="encoded"/>. Returns false on a malformed
        /// escape (a <c>%</c> not followed by exactly two hex digits). On success
        /// <paramref name="decoded"/> is the literal value.
        /// </summary>
        internal static bool TryDecode(string encoded, out string decoded)
        {
            decoded = null;
            if (encoded == null)
            {
                decoded = string.Empty;
                return true;
            }

            var bytes = new List<byte>(encoded.Length);
            int i = 0;
            while (i < encoded.Length)
            {
                char c = encoded[i];
                if (c == '%')
                {
                    if (i + 2 >= encoded.Length)
                        return false;
                    string hex = encoded.Substring(i + 1, 2);
                    if (!byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                        return false;
                    bytes.Add(b);
                    i += 3;
                }
                else if (c < 128)
                {
                    bytes.Add((byte)c);
                    i++;
                }
                else
                {
                    // A raw multibyte char (should not appear in well-formed encoded
                    // input, but decode defensively rather than corrupting it).
                    byte[] enc = Encoding.UTF8.GetBytes(c.ToString());
                    bytes.AddRange(enc);
                    i++;
                }
            }

            decoded = Encoding.UTF8.GetString(bytes.ToArray());
            return true;
        }

        /// <summary>
        /// Splits a single <c>key=value</c> token at the FIRST <c>=</c>. The value
        /// portion is returned still percent-encoded (the caller decodes it).
        /// Returns false for a bare token with no <c>=</c> or an empty key.
        /// </summary>
        internal static bool TrySplitToken(string token, out string key, out string rawValue)
        {
            key = null;
            rawValue = null;
            if (string.IsNullOrEmpty(token)) return false;
            int idx = token.IndexOf('=');
            if (idx <= 0) return false; // no '=' or empty key
            key = token.Substring(0, idx);
            rawValue = token.Substring(idx + 1);
            return true;
        }

        private static bool NeedsEncoding(byte b)
        {
            // '%' (0x25) and '=' (0x3D) are the reserved delimiters; anything
            // <= space (0x20, includes tab/newline/CR) or DEL (0x7F) is a control
            // or whitespace byte that must not appear raw in a token.
            return b == (byte)'%' || b == (byte)'=' || b <= 0x20 || b == 0x7F;
        }
    }

    /// <summary>
    /// Pure result of parsing one command line (<see cref="TestCommandParser.ParseLine"/>).
    /// </summary>
    internal struct ParsedCommand
    {
        /// <summary>The raw line content (newline stripped).</summary>
        public string RawLine;

        /// <summary>1-based position of the line in the command file.</summary>
        public int LineNumber;

        /// <summary>Decoded <c>id</c> token value; null if the id token was absent.</summary>
        public string Id;

        /// <summary>Decoded <c>cmd</c> token value; null if the cmd token was absent.</summary>
        public string Verb;

        /// <summary>Decoded verb-specific args (excludes id/cmd).</summary>
        public Dictionary<string, string> Args;

        /// <summary>True when the line parsed into a well-formed command envelope.</summary>
        public bool ParseOk;

        /// <summary>Reason string when <see cref="ParseOk"/> is false
        /// (<c>malformed</c> / <c>missing-id</c> / <c>missing-cmd</c>).</summary>
        public string ParseError;

        /// <summary>True for a blank line or a <c>#</c> comment: skip silently, no
        /// response is written. Distinct from a malformed line, which is REJECTED.</summary>
        public bool Ignored;
    }
}
