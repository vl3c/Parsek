using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-18 (RULE 2, never drop a separation): the crash coalescer holds a
    /// separation for up to 0.5 s before it becomes a branch point, and every recorder start
    /// (<c>PrepareSessionStateForRecorderStart</c>) resets that window. Todo
    /// SPLIT-WINDOW-DROPPED-ON-RECORDER-START: staging and then undocking / EVA / docking inside
    /// the window lost the booster, because no recorder-start caller emitted the window first.
    /// These gates pin that every caller flushes the pending split before it starts the new
    /// recorder (and before it moves the tree's active recording), that the reset itself never
    /// drops a window, and that a save or scene exit flushes it too
    /// (todo SPLIT-WINDOW-MISSING-FROM-QUICKSAVE). The live paths need KSP; the decisions they
    /// read are pinned by the pure cells in <see cref="SeparationNeverDroppedTests"/>.
    /// </summary>
    public class PendingSplitFlushWiringTests
    {
        private const string Flush = "FlushPendingSplits(";
        private const string RecorderStart = "PrepareSessionStateForRecorderStart(";

        // Every method that starts a recorder session. A new one reds here until it flushes the
        // pending split first and is listed.
        private static readonly string[] ExpectedRecorderStartCallers =
        {
            "BindLiveRecorderToSwitchSegment",
            "CreateMergeBranch",
            "CreateSplitBranch",
            "CreateSplitBranchFromBackgroundParent",
            "PromoteRecordingFromBackground",
            "RestoreActiveTreeFromPending",
            "ResumeCommittedActiveRecording",
            "StartRecording",
        };

        [Fact]
        public void EveryRecorderStartCallerFlushesThePendingSplitFirst()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(ParsekSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                int idx = 0;
                while ((idx = prepared.IndexOf(RecorderStart, idx, StringComparison.Ordinal)) >= 0)
                {
                    int callIdx = idx;
                    idx += RecorderStart.Length;
                    if (IsDeclaration(prepared, callIdx))
                        continue;

                    int bodyStart = SourceScanText.EnclosingMethodBodyStart(prepared, callIdx);
                    Assert.True(bodyStart >= 0, "no enclosing method for a recorder start in " + path);
                    string name = MethodNameBefore(prepared, bodyStart);
                    found.Add(name);

                    string body = SourceScanText.BraceMatchedBlock(prepared, bodyStart);
                    int callInBody = callIdx - bodyStart;
                    int flush = body.IndexOf(Flush, StringComparison.Ordinal);
                    Assert.True(flush >= 0 && flush < callInBody,
                        name + " (" + Path.GetFileName(path) + ") starts a recorder session without "
                        + "FlushPendingSplits before it: an open coalescer window would be reset unseen");

                    // The flush must also run before the recorder object starts sampling...
                    int newRecorder = body.IndexOf("new FlightRecorder()", StringComparison.Ordinal);
                    if (newRecorder >= 0)
                        Assert.True(flush < newRecorder,
                            name + ": FlushPendingSplits must run before the new FlightRecorder starts");

                    // ...and before the tree's active recording moves to the new recording (the
                    // flushed branch point attaches to the active recording). A swap back to the
                    // prior recording around the flush is the one allowed assignment before it.
                    foreach (Match m in Regex.Matches(body.Substring(0, flush), @"ActiveRecordingId\s*=(?!=)\s*([A-Za-z_][A-Za-z0-9_]*)"))
                    {
                        Assert.True(string.Equals(m.Groups[1].Value, "priorActiveRecordingId", StringComparison.Ordinal),
                            name + ": ActiveRecordingId is reassigned (to '" + m.Groups[1].Value
                            + "') before FlushPendingSplits, so the pending split would attach to the wrong recording");
                    }
                }
            }

            Assert.Equal(ExpectedRecorderStartCallers, found.ToArray());
        }

        [Fact]
        public void RecorderStartEmitsAnOpenWindowBeforeTheReset()
        {
            string body = MethodBody("Controllers/ParsekFlight.RecorderSession.cs",
                "void PrepareSessionStateForRecorderStart(string reason)");
            int emit = IndexOrFail(body, "EmitPendingSplitNow(");
            int reset = IndexOrFail(body, "crashCoalescer.Reset()");
            Assert.True(emit < reset, "PrepareSessionStateForRecorderStart must emit an open window before Reset()");
        }

        [Fact]
        public void SwitchSegmentBindFlushesUnderThePriorRecording()
        {
            string body = MethodBody("Controllers/ParsekFlightSwitchContinuation.cs",
                "bool BindLiveRecorderToSwitchSegment(");
            int swap = IndexOrFail(body, "host.activeTree.ActiveRecordingId = priorActiveRecordingId");
            int flush = IndexOrFail(body, Flush);
            int restore = IndexOrFail(body, "host.activeTree.ActiveRecordingId = activeIdBeforeSplitFlush");
            Assert.True(swap < flush && flush < restore,
                "the switch-segment bind must flush the pending split while the prior recording is active");
        }

        [Fact]
        public void SaveFlushesThePendingSplitBeforeSerializingTheTree()
        {
            string body = MethodBody("Controllers/ParsekScenario.Save.cs", "void SaveActiveTreeIfAny(");
            int flush = IndexOrFail(body, "FlushPendingSplitsForSave()");
            int recorder = IndexOrFail(body, "ActiveRecorderForSerialization");
            int serialize = IndexOrFail(body, "FlushRecorderIntoActiveTreeForSerialization()");
            Assert.True(flush < recorder && flush < serialize,
                "SaveActiveTreeIfAny must flush the pending split before it serializes the active tree");

            string forSave = MethodBody("Controllers/ParsekFlight.cs", "void FlushPendingSplitsForSave()");
            IndexOrFail(forSave, Flush);
        }

        [Fact]
        public void SceneExitFlushesThePendingSplitBeforeTheTransientReset()
        {
            string onScene = MethodBody("Controllers/ParsekFlight.cs", "void OnSceneChangeRequested(");
            int flush = IndexOrFail(onScene, Flush);
            int clear = IndexOrFail(onScene, "ClearSceneChangeTransientState()");
            Assert.True(flush < clear, "OnSceneChangeRequested must flush before ClearSceneChangeTransientState");

            string forCallback = MethodBody("Controllers/ParsekFlightMergeFlow.cs",
                "void FinalizeTreeOnSceneChangeForCallback(");
            Assert.True(IndexOrFail(forCallback, "host.FlushPendingSplits(")
                    < IndexOrFail(forCallback, "host.ClearSceneChangeTransientState()"),
                "FinalizeTreeOnSceneChangeForCallback must flush before the transient reset");

            string core = MethodBody("Controllers/ParsekFlightMergeFlow.cs", "void FinalizeTreeOnSceneChangeCore(");
            Assert.True(IndexOrFail(core, "host.FlushPendingSplits(")
                    < IndexOrFail(core, "backgroundRecorder"),
                "FinalizeTreeOnSceneChangeCore must flush before the background checkpoint and commit");
        }

        private static bool IsDeclaration(string prepared, int callIdx)
        {
            int lineStart = prepared.LastIndexOf('\n', callIdx) + 1;
            string before = prepared.Substring(lineStart, callIdx - lineStart);
            return Regex.IsMatch(before, @"\b(void|bool|int)\s+$");
        }

        private static string MethodNameBefore(string prepared, int bodyStart)
        {
            int from = Math.Max(
                prepared.LastIndexOf(';', bodyStart - 1),
                Math.Max(prepared.LastIndexOf('}', bodyStart - 1), prepared.LastIndexOf('{', bodyStart - 1)));
            string signature = prepared.Substring(from + 1, bodyStart - from - 1);
            signature = Regex.Replace(signature, @"\[[^\]]*\]", " ");
            Match m = Regex.Match(signature, @"([A-Za-z_][A-Za-z0-9_]*)\s*\(");
            Assert.True(m.Success, "cannot read the method name of: " + signature.Trim());
            return m.Groups[1].Value;
        }

        private static string MethodBody(string relPath, string signatureFragment)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                ParsekFlightSourceFiles.ReadText(Path.Combine(ParsekSourceRoot(), relPath)));
            int sigIdx = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "signature not found in " + relPath + ": " + signatureFragment);
            int open = prepared.IndexOf('{', sigIdx);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "expected to find '" + needle + "'");
            return idx;
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }
    }
}
