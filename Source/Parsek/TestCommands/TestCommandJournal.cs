using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The lifecycle phase recorded for a command id in the write-ahead journal.
    /// Ordered so <see cref="TestCommandJournal.ReplayIntoPhaseMap"/> can keep the
    /// highest phase seen per id: None &lt; Claimed &lt; Executed &lt; Done.
    /// </summary>
    internal enum JournalPhase
    {
        None = 0,
        Claimed = 1,
        Executed = 2,
        Done = 3,
    }

    /// <summary>The recovery action for a command id given its replayed phase.</summary>
    internal enum RecoveryAction
    {
        /// <summary>No journal entry: run the side effect normally.</summary>
        Execute,

        /// <summary>At CLAIMED (crashed mid-execution): do NOT re-execute; write an
        /// INTERRUPTED terminal response and mark DONE.</summary>
        Interrupted,

        /// <summary>At EXECUTED (side effect ran, response maybe not written): skip
        /// execution, (re)write the response, mark DONE.</summary>
        RewriteResponse,

        /// <summary>At DONE: fully handled; skip entirely.</summary>
        Skip,
    }

    /// <summary>Pure result of parsing one journal line.</summary>
    internal struct JournalLine
    {
        public bool Ok;
        public string Id;
        public JournalPhase Phase;
        public long Seq;
        public string Verb;
        public string Session;
        public string Verdict;
        public double T;

        /// <summary>Decoded free-text message (EXECUTED lines, for true response rewrite);
        /// null when the line carried no <c>msg=</c> token or it was empty.</summary>
        public string Msg;

        /// <summary>Decoded verb-specific payload pairs (EXECUTED lines, for true response
        /// rewrite); null when the line carried no <c>payload=</c> token.</summary>
        public List<KeyValuePair<string, string>> Payload;
    }

    /// <summary>
    /// Pure write-ahead-log (WAL) grammar and replay for the ParsekTestCommands
    /// seam (M-A2). The journal is the durable at-most-once mechanism: execution
    /// happens ONLY on the transition from "no entry" to CLAIMED -> EXECUTED. Replay
    /// folds the append-only journal into a per-id highest-phase map, tolerating a
    /// torn (non-newline-terminated) trailing line, and <see cref="DecideRecovery"/>
    /// maps a phase to the crash-recovery action.
    /// </summary>
    internal static class TestCommandJournal
    {
        // ----- Formatters (bare lines; the append path adds the newline) -----

        internal static string FormatClaimed(string id, long seq, string verb, string session, double t)
        {
            return "id=" + id
                + " phase=CLAIMED"
                + " seq=" + seq.ToString(CultureInfo.InvariantCulture)
                + " verb=" + verb
                + " session=" + session
                + " t=" + t.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The EXECUTED write-ahead line. It now carries the terminal
        /// <paramref name="verdict"/> plus the percent-encoded <paramref name="payload"/> and
        /// <paramref name="msg"/> so a crash-recovery replay at EXECUTED can re-emit the
        /// ORIGINAL response line (true RewriteResponse) instead of a synthetic INTERRUPTED
        /// ack. The <c>payload</c> token is the whole space-joined <c>key=encodedValue</c>
        /// list percent-encoded once more so it survives as a single wire token.
        /// </summary>
        internal static string FormatExecuted(
            string id, long seq, double t, string verdict,
            List<KeyValuePair<string, string>> payload, string msg)
        {
            return "id=" + id
                + " phase=EXECUTED"
                + " seq=" + seq.ToString(CultureInfo.InvariantCulture)
                + " t=" + t.ToString("R", CultureInfo.InvariantCulture)
                + " verdict=" + TestCommandProtocol.Encode(verdict ?? string.Empty)
                + " payload=" + TestCommandProtocol.Encode(SerializePayload(payload))
                + " msg=" + TestCommandProtocol.Encode(msg ?? string.Empty);
        }

        // Serializes a payload list into one string of space-joined key=encodedValue pairs.
        // Keys are literal identifiers (safe); values are percent-encoded. The whole result
        // is percent-encoded again by the caller so it rides as a single journal token.
        private static string SerializePayload(List<KeyValuePair<string, string>> payload)
        {
            if (payload == null || payload.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < payload.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(payload[i].Key).Append('=')
                    .Append(TestCommandProtocol.Encode(payload[i].Value ?? string.Empty));
            }
            return sb.ToString();
        }

        // Inverse of SerializePayload: splits the (already once-decoded) payload string into
        // key=value pairs, decoding each value. A malformed pair is skipped defensively.
        private static List<KeyValuePair<string, string>> DeserializePayload(string s)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(s)) return list;
            string[] tokens = s.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string tok in tokens)
            {
                if (TestCommandProtocol.TrySplitToken(tok, out string key, out string rawVal)
                    && TestCommandProtocol.TryDecode(rawVal, out string val))
                    list.Add(new KeyValuePair<string, string>(key, val));
            }
            return list;
        }

        internal static string FormatDone(string id, long seq, string verdict, double t)
        {
            return "id=" + id
                + " phase=DONE"
                + " seq=" + seq.ToString(CultureInfo.InvariantCulture)
                + " verdict=" + verdict
                + " t=" + t.ToString("R", CultureInfo.InvariantCulture);
        }

        // ----- Parse -----

        /// <summary>
        /// Parses one whole journal line into a <see cref="JournalLine"/>. Requires a
        /// parseable <c>id</c> and a known <c>phase</c>; other fields are optional.
        /// Returns false (Ok=false) for a line missing id/phase or with an unknown
        /// phase token.
        /// </summary>
        internal static bool TryParseLine(string line, out JournalLine parsed)
        {
            parsed = new JournalLine { Phase = JournalPhase.None };
            if (string.IsNullOrEmpty(line)) return false;

            string[] tokens = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            bool sawId = false;
            bool sawPhase = false;

            foreach (string token in tokens)
            {
                if (!TestCommandProtocol.TrySplitToken(token, out string key, out string value))
                    continue; // tolerate stray tokens

                switch (key)
                {
                    case "id":
                        parsed.Id = value;
                        sawId = true;
                        break;
                    case "phase":
                        if (!TryParsePhase(value, out JournalPhase phase))
                            return false;
                        parsed.Phase = phase;
                        sawPhase = true;
                        break;
                    case "seq":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq))
                            parsed.Seq = seq;
                        break;
                    case "verb":
                        parsed.Verb = value;
                        break;
                    case "session":
                        parsed.Session = value;
                        break;
                    case "verdict":
                        // Encoded on EXECUTED lines, raw on DONE lines; decode is identity for
                        // the raw (encoding-safe verdict) case.
                        if (TestCommandProtocol.TryDecode(value, out string verdictDec))
                            parsed.Verdict = verdictDec;
                        break;
                    case "payload":
                        if (TestCommandProtocol.TryDecode(value, out string payloadDec))
                            parsed.Payload = DeserializePayload(payloadDec);
                        break;
                    case "msg":
                        if (TestCommandProtocol.TryDecode(value, out string msgDec))
                            parsed.Msg = msgDec;
                        break;
                    case "t":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                            parsed.T = t;
                        break;
                }
            }

            if (!sawId || !sawPhase) return false;
            parsed.Ok = true;
            return true;
        }

        // ----- Replay -----

        /// <summary>
        /// Replays a whole journal file's contents into a per-id highest-phase map. A
        /// torn trailing line (the file does not end in <c>\n</c>) is ignored: it was
        /// never durably committed. Only whole, newline-terminated lines drive the map.
        /// </summary>
        internal static Dictionary<string, JournalPhase> ReplayIntoPhaseMap(string fileContent)
        {
            return ReplayIntoPhaseMap(SplitCompleteLines(fileContent));
        }

        /// <summary>
        /// Replays already-extracted whole journal lines into a per-id highest-phase
        /// map (max phase per id; duplicate lines from crash-recovery rewrites keep
        /// the highest).
        /// </summary>
        internal static Dictionary<string, JournalPhase> ReplayIntoPhaseMap(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, JournalPhase>();
            if (lines == null) return map;

            foreach (string line in lines)
            {
                if (!TryParseLine(line, out JournalLine jl)) continue;
                if (map.TryGetValue(jl.Id, out JournalPhase existing))
                {
                    if (jl.Phase > existing)
                        map[jl.Id] = jl.Phase;
                }
                else
                {
                    map[jl.Id] = jl.Phase;
                }
            }
            return map;
        }

        /// <summary>
        /// Extracts whole, newline-terminated lines from raw file content. A final
        /// segment without a trailing <c>\n</c> is a torn line and is dropped. <c>\r</c>
        /// before a <c>\n</c> is stripped so CRLF content parses the same as LF.
        /// </summary>
        internal static List<string> SplitCompleteLines(string content)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(content)) return lines;

            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    int end = i;
                    if (end > start && content[end - 1] == '\r')
                        end--;
                    lines.Add(content.Substring(start, end - start));
                    start = i + 1;
                }
            }
            // Any content after the last '\n' is a torn trailing line: ignore it.
            return lines;
        }

        // ----- Recovery decision -----

        /// <summary>
        /// Maps a replayed phase to the crash-recovery action. The <paramref name="id"/>
        /// is carried for logging/correlation only; the decision is phase-driven.
        /// </summary>
        internal static RecoveryAction DecideRecovery(string id, JournalPhase phase)
        {
            switch (phase)
            {
                case JournalPhase.Claimed:
                    return RecoveryAction.Interrupted;
                case JournalPhase.Executed:
                    return RecoveryAction.RewriteResponse;
                case JournalPhase.Done:
                    return RecoveryAction.Skip;
                default:
                    return RecoveryAction.Execute;
            }
        }

        /// <summary>Convenience: recovery action for an id looked up in a replayed map.</summary>
        internal static RecoveryAction DecideRecovery(Dictionary<string, JournalPhase> phaseMap, string id)
        {
            JournalPhase phase = JournalPhase.None;
            if (phaseMap != null && id != null)
                phaseMap.TryGetValue(id, out phase);
            return DecideRecovery(id, phase);
        }

        /// <summary>
        /// Raises the replayed phase for <paramref name="id"/> in <paramref name="map"/> to the
        /// phase named by <paramref name="phaseToken"/> (<c>CLAIMED</c> / <c>EXECUTED</c> /
        /// <c>DONE</c>), keeping the highest phase seen. The addon mirrors every durable journal
        /// write here so a same-process re-dispatch of an already-claimed id can never read
        /// <see cref="JournalPhase.None"/> from the map (defense-in-depth at-most-once): an
        /// unknown token or a lower phase is a no-op.
        /// </summary>
        internal static void MirrorPhaseIntoMap(Dictionary<string, JournalPhase> map, string id, string phaseToken)
        {
            if (map == null || string.IsNullOrEmpty(id)) return;
            if (!TryParsePhase(phaseToken, out JournalPhase phase)) return;
            if (!map.TryGetValue(id, out JournalPhase existing) || phase > existing)
                map[id] = phase;
        }

        /// <summary>
        /// Recovers the ORIGINAL terminal response fields (verdict / payload / msg) stored on
        /// the last whole EXECUTED line for <paramref name="id"/>, so a RewriteResponse
        /// recovery re-emits the same response the pre-crash run would have (byte-equivalent in
        /// verdict / payload / msg; only <c>seq</c> and <c>ut</c> legitimately differ). Returns
        /// false when no EXECUTED line carries the enriched fields (a torn / pre-enrichment
        /// line), so the caller can fall back. <paramref name="msg"/> is normalized empty -&gt;
        /// null to match a null-msg original response exactly.
        /// </summary>
        internal static bool TryGetExecutedResponse(
            IEnumerable<string> lines, string id,
            out string verdict, out List<KeyValuePair<string, string>> payload, out string msg)
        {
            verdict = null;
            payload = null;
            msg = null;
            if (lines == null || id == null) return false;

            bool found = false;
            foreach (string line in lines)
            {
                if (!TryParseLine(line, out JournalLine jl)) continue;
                if (jl.Phase != JournalPhase.Executed || jl.Id != id) continue;
                if (jl.Verdict == null) continue; // pre-enrichment EXECUTED line: no stored verdict
                verdict = jl.Verdict;
                payload = jl.Payload;
                msg = string.IsNullOrEmpty(jl.Msg) ? null : jl.Msg;
                found = true; // last EXECUTED line for the id wins
            }
            return found;
        }

        private static bool TryParsePhase(string value, out JournalPhase phase)
        {
            switch (value)
            {
                case "CLAIMED": phase = JournalPhase.Claimed; return true;
                case "EXECUTED": phase = JournalPhase.Executed; return true;
                case "DONE": phase = JournalPhase.Done; return true;
                default: phase = JournalPhase.None; return false;
            }
        }
    }
}
