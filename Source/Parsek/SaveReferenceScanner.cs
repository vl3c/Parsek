using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// What a save file (or memory) keeps alive under <c>Parsek/</c>
    /// (design-load-restores-the-save.md 4.5): recording ids with their content hashes,
    /// rewind point ids, and rewind save names.
    /// </summary>
    internal sealed class SaveReferences
    {
        internal readonly HashSet<string> RecordingIds = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> RecordingVersionKeys = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> RewindPointIds = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> RewindSaveNames = new HashSet<string>(StringComparer.Ordinal);

        internal static string VersionKey(string recordingId, string hash)
        {
            return recordingId + "|" + hash;
        }

        internal void AddRecording(string recordingId, string precHash, string vesselHash, string ghostHash)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            RecordingIds.Add(recordingId);
            AddHash(recordingId, precHash);
            AddHash(recordingId, vesselHash);
            AddHash(recordingId, ghostHash);
        }

        private void AddHash(string recordingId, string hash)
        {
            if (!string.IsNullOrEmpty(hash))
                RecordingVersionKeys.Add(VersionKey(recordingId, hash));
        }

        internal void AddRewindPoint(string rewindPointId, string quicksaveFilename)
        {
            if (!string.IsNullOrEmpty(rewindPointId))
                RewindPointIds.Add(rewindPointId);
            // Defensive: the file is <rpId>.sfs today, but a point also names its file.
            if (!string.IsNullOrEmpty(quicksaveFilename))
            {
                string stem = Path.GetFileNameWithoutExtension(quicksaveFilename.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(stem))
                    RewindPointIds.Add(stem);
            }
        }

        internal void AddRewindSave(string saveName)
        {
            if (!string.IsNullOrEmpty(saveName))
                RewindSaveNames.Add(saveName);
        }

        internal bool NamesVersion(string recordingId, string hash)
        {
            return RecordingVersionKeys.Contains(VersionKey(recordingId, hash));
        }

        internal void UnionWith(SaveReferences other)
        {
            if (other == null)
                return;
            RecordingIds.UnionWith(other.RecordingIds);
            RecordingVersionKeys.UnionWith(other.RecordingVersionKeys);
            RewindPointIds.UnionWith(other.RewindPointIds);
            RewindSaveNames.UnionWith(other.RewindSaveNames);
        }
    }

    /// <summary>
    /// Pure line scanner over a KSP <c>.sfs</c> (design 4.5 "Cost"): tracks node depth and
    /// collects references only inside a <c>SCENARIO</c> node whose <c>name = ParsekScenario</c>.
    /// It builds no <c>ConfigNode</c>. A file with unbalanced braces (truncated mid-node) or
    /// with no node at all fails the scan, so the collector fails closed on it. A save with
    /// no Parsek scenario node is a valid referrer that names nothing.
    /// </summary>
    internal static class SaveReferenceScanner
    {
        internal const string ParsekScenarioName = "ParsekScenario";

        private sealed class Frame
        {
            public string Name;
            public string ScenarioName;
            public SaveReferences Buffer;
            public string RecordingId;
            public string PrecHash;
            public string VesselHash;
            public string GhostHash;
            public string RewindPointId;
            public string QuicksaveFilename;
        }

        internal static bool TryScan(TextReader reader, out SaveReferences references, out string failure)
        {
            references = new SaveReferences();
            failure = null;
            if (reader == null)
            {
                failure = "no-reader";
                return false;
            }

            var stack = new List<Frame>();
            string pendingName = null;
            bool sawNode = false;
            int lineNumber = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                string text = (comment >= 0 ? line.Substring(0, comment) : line).Trim();
                if (text.Length == 0)
                    continue;

                int eq = text.IndexOf('=');
                if (eq >= 0)
                {
                    pendingName = null;
                    if (stack.Count > 0)
                    {
                        HandleValue(stack,
                            text.Substring(0, eq).Trim(),
                            text.Substring(eq + 1).Trim());
                    }
                    continue;
                }

                // Structural line: a node name, a brace, or a name followed by a brace.
                int tokenStart = 0;
                for (int i = 0; i <= text.Length; i++)
                {
                    char c = i < text.Length ? text[i] : '\0';
                    if (i < text.Length && c != '{' && c != '}')
                        continue;

                    string token = text.Substring(tokenStart, i - tokenStart).Trim();
                    if (token.Length > 0)
                        pendingName = token;
                    tokenStart = i + 1;
                    if (i == text.Length)
                        break;

                    if (c == '{')
                    {
                        stack.Add(new Frame { Name = pendingName ?? string.Empty });
                        if (string.Equals(pendingName, "SCENARIO", StringComparison.Ordinal))
                            stack[stack.Count - 1].Buffer = new SaveReferences();
                        pendingName = null;
                        sawNode = true;
                    }
                    else
                    {
                        if (stack.Count == 0)
                        {
                            failure = "unbalanced-close line=" + lineNumber;
                            return false;
                        }
                        Frame closed = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        CloseFrame(stack, closed, references);
                        pendingName = null;
                    }
                }
            }

            if (stack.Count > 0)
            {
                failure = "truncated depth=" + stack.Count;
                return false;
            }
            if (!sawNode)
            {
                failure = "no-nodes";
                return false;
            }
            return true;
        }

        private static SaveReferences EnclosingParsekBuffer(List<Frame> stack)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].Buffer != null)
                    return stack[i].Buffer;
            }
            return null;
        }

        private static void HandleValue(List<Frame> stack, string key, string value)
        {
            Frame top = stack[stack.Count - 1];
            if (top.Buffer != null && string.Equals(key, "name", StringComparison.Ordinal))
            {
                top.ScenarioName = value;
                return;
            }

            SaveReferences buffer = EnclosingParsekBuffer(stack);
            if (buffer == null)
                return;

            if (string.Equals(key, "rewindSave", StringComparison.Ordinal)
                || string.Equals(key, "resumeRewindSave", StringComparison.Ordinal))
            {
                buffer.AddRewindSave(value);
            }

            if (string.Equals(top.Name, "RECORDING", StringComparison.Ordinal))
            {
                switch (key)
                {
                    case "recordingId": top.RecordingId = value; break;
                    case "precHash": top.PrecHash = value; break;
                    case "vesselHash": top.VesselHash = value; break;
                    case "ghostHash": top.GhostHash = value; break;
                }
                return;
            }

            if (string.Equals(top.Name, "POINT", StringComparison.Ordinal)
                && stack.Count >= 2
                && string.Equals(stack[stack.Count - 2].Name, "REWIND_POINTS", StringComparison.Ordinal))
            {
                if (string.Equals(key, "rewindPointId", StringComparison.Ordinal))
                    top.RewindPointId = value;
                else if (string.Equals(key, "quicksaveFilename", StringComparison.Ordinal))
                    top.QuicksaveFilename = value;
            }
        }

        private static void CloseFrame(List<Frame> stack, Frame closed, SaveReferences result)
        {
            if (closed.Buffer != null)
            {
                if (string.Equals(closed.ScenarioName, ParsekScenarioName, StringComparison.Ordinal))
                    result.UnionWith(closed.Buffer);
                return;
            }

            SaveReferences buffer = EnclosingParsekBuffer(stack);
            if (buffer == null)
                return;

            if (string.Equals(closed.Name, "RECORDING", StringComparison.Ordinal))
                buffer.AddRecording(closed.RecordingId, closed.PrecHash, closed.VesselHash, closed.GhostHash);
            else if (closed.RewindPointId != null || closed.QuicksaveFilename != null)
                buffer.AddRewindPoint(closed.RewindPointId, closed.QuicksaveFilename);
        }
    }
}
