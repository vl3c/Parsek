using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Save-path hardening from the replay-theory audit.
    ///
    /// <para><b>R2.</b> Stock builds the flight state from every live vessel, ghost map ProtoVessels
    /// included, BEFORE it runs the scenario saves (decompiled <c>Game.Updated</c>: <c>new FlightState()</c>
    /// then <c>ScenarioRunner.GetUpdatedProtoModules</c>). <c>ParsekScenario.OnSave</c> is the only place
    /// they are taken back out, and nothing on the load side can tell a serialized ghost from a player
    /// craft, so the strip must run even when an earlier save phase throws.</para>
    ///
    /// <para><b>R6.</b> A save in the frame a deferred EVA / undock / decouple split parks the recorder
    /// must still carry the parent recording's unflushed data, and must leave the recording exactly as
    /// the deferred split expects to find it.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SavePathHardeningTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SavePathHardeningTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ------------------------------------------------------------------ R2

        private static ProtoVessel MakeProtoVessel(uint pid)
        {
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.persistentId = pid;
            return pv;
        }

        private static FlightState MakeFlightState(params uint[] pids)
        {
            var fs = (FlightState)FormatterServices.GetUninitializedObject(typeof(FlightState));
            fs.protoVessels = new List<ProtoVessel>();
            for (int i = 0; i < pids.Length; i++)
                fs.protoVessels.Add(MakeProtoVessel(pids[i]));
            return fs;
        }

        // Fails if: a save phase that threw makes the ghost strip skip, so ghost ProtoVessels stay in
        // the flight state stock writes as real vessels.
        [Fact]
        public void PhaseThrew_GhostsAreStillStripped_AndTheStripIsLogged()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(5001);
            FlightState fs = MakeFlightState(7, 5001);

            int stripped = ParsekScenario.StripGhostMapVesselsFailClosed(
                fs, "OnSave", "routes", phasesCompleted: false);

            Assert.Equal(1, stripped);
            Assert.Single(fs.protoVessels);
            Assert.Equal(7u, fs.protoVessels[0].persistentId);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("phase 'routes' threw")
                && l.Contains("fail-closed ghost strip"));
        }

        [Fact]
        public void PhasesCompleted_GhostsAreStripped_WithoutTheFaultLine()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(5001);
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(5002);
            FlightState fs = MakeFlightState(5001, 7, 5002);

            int stripped = ParsekScenario.StripGhostMapVesselsFailClosed(
                fs, "OnSave", "save-stamp", phasesCompleted: true);

            Assert.Equal(2, stripped);
            Assert.Single(fs.protoVessels);
            Assert.DoesNotContain(logLines, l => l.Contains("fail-closed ghost strip"));
        }

        [Fact]
        public void NoGhosts_NothingIsTouched()
        {
            FlightState fs = MakeFlightState(7, 8);
            Assert.Equal(0, ParsekScenario.StripGhostMapVesselsFailClosed(
                fs, "OnSave", "routes", phasesCompleted: false));
            Assert.Equal(2, fs.protoVessels.Count);
            Assert.Equal(0, ParsekScenario.StripGhostMapVesselsFailClosed(
                null, "OnSave", "routes", phasesCompleted: false));
        }

        // Fails if: a strip that itself fails while a phase exception is in flight replaces that
        // exception (the original fault would be lost from the log and the caller).
        [Fact]
        public void StripFailsAfterAFault_IsLoggedAndSwallowed()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(5001);
            var broken = (FlightState)FormatterServices.GetUninitializedObject(typeof(FlightState));

            int stripped = ParsekScenario.StripGhostMapVesselsFailClosed(
                broken, "OnSave", "routes", phasesCompleted: false);

            Assert.Equal(0, stripped);
            Assert.Contains(logLines, l => l.Contains("[ERROR]")
                && l.Contains("ghost ProtoVessel strip failed after phase 'routes'"));
        }

        // Fails if: a strip that fails on an otherwise clean save lets the save go on and write the
        // ghosts it could not remove.
        [Fact]
        public void StripFailsOnACleanSave_AbortsTheSave()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(5001);
            var broken = (FlightState)FormatterServices.GetUninitializedObject(typeof(FlightState));

            Assert.ThrowsAny<Exception>(() => ParsekScenario.StripGhostMapVesselsFailClosed(
                broken, "OnSave", "save-stamp", phasesCompleted: true));
            Assert.Contains(logLines, l => l.Contains("aborting the save so no ghost is written"));
        }

        // Fails if: the strip moves back into OnSave's try body (where a throwing phase skips it),
        // or the inert-mode early return stops stripping.
        [Fact]
        public void OnSave_StripsGhostsInItsFinally_AndOnTheInertPath()
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource("ParsekScenario.cs"));
            int sig = prepared.IndexOf("public override void OnSave(ConfigNode node)", StringComparison.Ordinal);
            Assert.True(sig >= 0, "OnSave signature not found");
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));

            Assert.DoesNotContain("GhostMapPresence.StripFromSave(", body);

            int lastFinally = body.LastIndexOf("finally", StringComparison.Ordinal);
            Assert.True(lastFinally >= 0, "OnSave has no finally block");
            string finallyBlock = SourceScanText.BraceMatchedBlock(body, body.IndexOf('{', lastFinally));
            Assert.Contains("StripGhostMapVesselsFailClosed(", finallyBlock);
            Assert.Contains("phasesCompleted", finallyBlock);

            int inert = body.IndexOf("ParsekGameModeGate.CheckInert(", StringComparison.Ordinal);
            Assert.True(inert >= 0, "inert gate not found");
            string inertBlock = SourceScanText.BraceMatchedBlock(body, body.IndexOf('{', inert));
            Assert.Contains("StripGhostMapVesselsFailClosed(", inertBlock);

            // phasesCompleted is set only as the last statement of the try body.
            int completed = body.IndexOf("phasesCompleted = true;", StringComparison.Ordinal);
            int stamp = body.IndexOf("WriteSaveStamp(node);", StringComparison.Ordinal);
            Assert.True(completed > stamp && stamp >= 0, "phasesCompleted must be set after the last phase");

            // Exactly one production caller of the raw strip: the fail-closed helper.
            Assert.Equal(1, CountOccurrencesInParsekSource("GhostMapPresence.StripFromSave("));
        }

        // ------------------------------------------------------------------ R6

        private static TrajectoryPoint Pt(double ut) => new TrajectoryPoint { ut = ut, bodyName = "Kerbin" };

        private static PartEvent Ev(double ut, PartEventType type, uint pid = 100000, int midx = 0)
            => new PartEvent { ut = ut, eventType = type, partPersistentId = pid, moduleIndex = midx, partName = "p" };

        private static Recording ParentWithFlushedHistory()
        {
            var rec = new Recording { RecordingId = "rec_parent", VesselPersistentId = 42 };
            rec.Points.Add(Pt(100));
            rec.Points.Add(Pt(101));
            rec.PartEvents.Add(Ev(100.5, PartEventType.EngineIgnited));
            rec.FilesDirty = false;
            return rec;
        }

        [Fact]
        public void ResolveSkipReason_CoversEveryGuard()
        {
            var tree = new RecordingTree { Id = "t1", ActiveRecordingId = "rec_parent" };
            Recording parent = ParentWithFlushedHistory();
            tree.Recordings[parent.RecordingId] = parent;
            var otherTree = new RecordingTree { Id = "t2" };

            Assert.Equal("live-recorder", ParkedSplitSaveOverlay.ResolveSkipReason(
                true, true, tree, tree, 42, out _));
            Assert.Equal("no-parked-recorder", ParkedSplitSaveOverlay.ResolveSkipReason(
                false, false, tree, tree, 42, out _));
            Assert.Equal("no-active-tree", ParkedSplitSaveOverlay.ResolveSkipReason(
                false, true, null, null, 42, out _));
            Assert.Equal("parked-recorder-other-tree", ParkedSplitSaveOverlay.ResolveSkipReason(
                false, true, tree, otherTree, 42, out _));
            Assert.Equal("parked-recorder-other-vessel", ParkedSplitSaveOverlay.ResolveSkipReason(
                false, true, tree, tree, 43, out _));

            var noId = new RecordingTree { Id = "t3" };
            Assert.Equal("no-active-recording-id", ParkedSplitSaveOverlay.ResolveSkipReason(
                false, true, noId, null, 42, out _));
            var missing = new RecordingTree { Id = "t4", ActiveRecordingId = "gone" };
            Assert.Equal("active-recording-missing", ParkedSplitSaveOverlay.ResolveSkipReason(
                false, true, missing, null, 42, out _));

            Assert.Null(ParkedSplitSaveOverlay.ResolveSkipReason(false, true, tree, tree, 42, out Recording target));
            Assert.Same(parent, target);
            // A recorder that carries no tree reference, or no pid yet, still overlays.
            Assert.Null(ParkedSplitSaveOverlay.ResolveSkipReason(false, true, tree, null, 0, out _));
        }

        // Fails if: the save written during a parked split lacks the parent's unflushed points,
        // carries the stop's terminal shutdown events (the saved recording is mid-flight), or the
        // overlay changes the parked buffers the deferred split still reads.
        [Fact]
        public void Apply_LaysTheBuffersOver_WithoutTheStopTerminals()
        {
            Recording parent = ParentWithFlushedHistory();
            var bufPoints = new List<TrajectoryPoint> { Pt(102), Pt(103), Pt(104) };
            var terminal = Ev(104, PartEventType.EngineShutdown);
            var bufEvents = new List<PartEvent> { Ev(102.5, PartEventType.EngineThrottle), terminal };
            var bufFlags = new List<FlagEvent> { new FlagEvent { ut = 103.5, flagSiteName = "f" } };
            var bufSections = new List<TrackSection> { new TrackSection { startUT = 102, endUT = 104 } };
            var terminals = new List<PartEvent> { terminal };

            ParkedSplitSaveOverlay overlay = ParkedSplitSaveOverlay.Apply(
                parent, bufPoints, new List<OrbitSegment>(), bufEvents, bufFlags, bufSections, terminals);

            Assert.Equal(5, parent.Points.Count);
            Assert.Equal(104, parent.Points[4].ut);
            Assert.Equal(2, parent.PartEvents.Count);
            Assert.DoesNotContain(parent.PartEvents, e => e.eventType == PartEventType.EngineShutdown);
            Assert.Equal(PartEventType.EngineIgnited, parent.PartEvents[0].eventType);
            Assert.Single(parent.FlagEvents);
            Assert.Single(parent.TrackSections);
            Assert.True(parent.FilesDirty);
            Assert.Equal(3, overlay.AddedPoints);
            Assert.Equal(1, overlay.AddedPartEvents);
            Assert.Equal(1, overlay.SkippedTerminalEvents);

            // The parked buffers are untouched.
            Assert.Equal(3, bufPoints.Count);
            Assert.Equal(2, bufEvents.Count);
        }

        // Fails if: the overlay leaves anything behind in memory, so the deferred split's own append
        // of the stop capture would double the parent's tail.
        [Fact]
        public void Restore_PutsTheRecordingBack_SoTheDeferredSplitAppendsOnce()
        {
            Recording parent = ParentWithFlushedHistory();
            Recording control = ParentWithFlushedHistory();
            var bufPoints = new List<TrajectoryPoint> { Pt(102), Pt(103) };
            var bufEvents = new List<PartEvent> { Ev(102.5, PartEventType.EngineThrottle) };
            var bufSections = new List<TrackSection> { new TrackSection { startUT = 102, endUT = 103 } };

            ParkedSplitSaveOverlay overlay = ParkedSplitSaveOverlay.Apply(
                parent, bufPoints, null, bufEvents, null, bufSections, null);
            overlay.Restore();
            overlay.Restore(); // idempotent

            Assert.Equal(control.Points.Count, parent.Points.Count);
            Assert.Equal(control.PartEvents.Count, parent.PartEvents.Count);
            Assert.Empty(parent.TrackSections);
            Assert.True(parent.FilesDirty); // the next save rewrites the sidecar from memory

            // The deferred split then appends its stop capture, exactly as with no save between.
            var capture = new Recording();
            capture.Points.AddRange(bufPoints);
            capture.PartEvents.AddRange(bufEvents);
            capture.TrackSections.AddRange(bufSections);
            ParsekFlight.AppendCapturedDataToRecording(parent, capture, 103);
            ParsekFlight.AppendCapturedDataToRecording(control, capture, 103);
            Assert.Equal(control.Points.Count, parent.Points.Count);
            Assert.Equal(control.PartEvents.Count, parent.PartEvents.Count);
            Assert.Equal(control.TrackSections.Count, parent.TrackSections.Count);
        }

        // Fails if: ParsekFlight stops reading the parked recorder's buffers (or its terminal list)
        // into the overlay, or stops logging when a save during a split cannot be overlaid.
        [Fact]
        public void BeginParkedSplitSaveOverlay_ReadsTheParkedRecorder()
        {
            var tree = new RecordingTree { Id = "t1", ActiveRecordingId = "rec_parent" };
            Recording parent = ParentWithFlushedHistory();
            tree.Recordings[parent.RecordingId] = parent;

            var parked = new FlightRecorder();
            parked.Recording.Add(Pt(102));
            var terminal = Ev(102, PartEventType.EngineShutdown);
            parked.PartEvents.Add(terminal);
            parked.lastEmittedTerminalEvents = new List<PartEvent> { terminal };
            parked.ActiveTree = tree;

            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            SetPrivateField(flight, "activeTree", tree);
            SetPrivateField(flight, "pendingSplitRecorder", parked);

            Assert.Same(parked, flight.ParkedSplitRecorderForSerialization);
            ParkedSplitSaveOverlay overlay = flight.BeginParkedSplitSaveOverlay();
            Assert.NotNull(overlay);
            Assert.Equal(3, parent.Points.Count);
            Assert.Single(parent.PartEvents);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("save during a deferred split")
                && l.Contains("addedPoints=1") && l.Contains("skippedTerminalEvents=1"));
            overlay.Restore();
            Assert.Equal(2, parent.Points.Count);

            // A parked recorder of a replaced tree is not laid over this one.
            parked.ActiveTree = new RecordingTree { Id = "old" };
            Assert.Null(flight.BeginParkedSplitSaveOverlay());
            Assert.Contains(logLines, l => l.Contains("not overlaid (parked-recorder-other-tree)"));
        }

        // Fails if: SaveActiveTreeIfAny stops overlaying the parked recorder, or restores it outside
        // a finally (a throwing write would leave the overlay in memory and double the tail).
        [Fact]
        public void SaveActiveTreeIfAny_OverlaysTheParkedRecorder_AndRestoresInAFinally()
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource("ParsekScenario.cs"));
            int sig = prepared.IndexOf("private static void SaveActiveTreeIfAny(ConfigNode node)", StringComparison.Ordinal);
            Assert.True(sig >= 0, "SaveActiveTreeIfAny signature not found");
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));

            int begin = body.IndexOf("BeginParkedSplitSaveOverlay()", StringComparison.Ordinal);
            Assert.True(begin >= 0, "SaveActiveTreeIfAny no longer applies the parked split overlay");
            int write = body.IndexOf("WriteActiveTreeNode(", begin, StringComparison.Ordinal);
            int fin = body.IndexOf("finally", begin, StringComparison.Ordinal);
            Assert.True(write > begin && fin > write, "the overlay must wrap the active-tree write");
            string finallyBlock = SourceScanText.BraceMatchedBlock(body, body.IndexOf('{', fin));
            Assert.Contains(".Restore()", finallyBlock);
        }

        // ------------------------------------------------------------------ helpers

        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, "field not found: " + name);
            f.SetValue(target, value);
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }

        private static string ReadParsekSource(string relPath)
        {
            string path = Path.Combine(ParsekSourceRoot(), relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return ParsekScenarioSourceFiles.ReadText(path);
        }

        private static int CountOccurrencesInParsekSource(string needle)
        {
            int count = 0;
            foreach (string path in Directory.GetFiles(ParsekSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(ParsekSourceRoot().Length).TrimStart('/', '\\');
                if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                    continue;
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                int at = 0;
                while ((at = prepared.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    at += needle.Length;
                }
            }
            return count;
        }
    }
}
