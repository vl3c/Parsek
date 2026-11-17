using System;

namespace Parsek
{
    /// <summary>The outcome of <see cref="SafeFileId.Check"/>, in the order the checks run.</summary>
    internal enum SafeFileIdVerdict
    {
        Ok,
        NullOrEmpty,
        PathSequence,
        InvalidChar,
    }

    /// <summary>
    /// The pure check that an id may name a file inside a folder Parsek owns (a recording
    /// sidecar, a rewind-point save). The id becomes a file name, so it must not climb out of
    /// that folder or carry a character some file system refuses.
    ///
    /// The refused character set is fixed, never read from the running platform: NUL, the
    /// control chars 1 to 31, and <c>" &lt; &gt; | : * ? \ /</c> (41 chars, the set .NET reports on
    /// Windows). An id is therefore accepted or refused the same way on every operating system
    /// and in every culture: an id written on one machine and read on another (a save copied
    /// between machines, a file from another player) cannot reach a path on the reader that the
    /// writer could not have made. Every id Parsek generates (a GUID in its N or D form, with or
    /// without a <c>rec_</c> / <c>rp_</c> / <c>mj_</c> prefix) passes; a space, a single dot and
    /// non-ASCII letters pass, as they always did on Windows.
    ///
    /// No logging and no <c>System.IO</c> call: the caller logs (see
    /// <c>RecordingPaths.ValidateRecordingId</c>).
    /// </summary>
    internal static class SafeFileId
    {
        // The printable members of the fixed set; every char below U+0020 is refused too.
        private const string InvalidPrintableChars = "\"<>|:*?\\/";

        internal static SafeFileIdVerdict Check(string id)
        {
            if (string.IsNullOrEmpty(id))
                return SafeFileIdVerdict.NullOrEmpty;

            if (id.IndexOf('/') >= 0
                || id.IndexOf('\\') >= 0
                || id.IndexOf("..", StringComparison.Ordinal) >= 0)
                return SafeFileIdVerdict.PathSequence;

            for (int i = 0; i < id.Length; i++)
            {
                if (IsInvalidChar(id[i]))
                    return SafeFileIdVerdict.InvalidChar;
            }
            return SafeFileIdVerdict.Ok;
        }

        internal static bool IsInvalidChar(char c)
        {
            return c < ' ' || InvalidPrintableChars.IndexOf(c) >= 0;
        }
    }
}
