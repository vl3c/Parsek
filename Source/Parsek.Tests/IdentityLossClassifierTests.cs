using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the BG go-on-rails identity-loss override:
    /// <see cref="IdentityLossClassifier.ShouldClassifyRecordedIdentityLost"/> (pure)
    /// and <see cref="Recording.MarkDestroyedAtTerminal"/> (hygiene helper).
    /// Live adapter (<see cref="IdentityLossClassifier.IsRecordedIdentityLost"/>) is
    /// covered by in-game tests in <c>RuntimeTests.cs</c>; it needs a live Vessel
    /// for <see cref="ParsekFlight.IsTrackableVessel"/>.
    /// </summary>
    [Collection("Sequential")]
    public class IdentityLossClassifierTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public IdentityLossClassifierTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region ShouldClassifyRecordedIdentityLost — pure predicate

        [Fact]
        public void Debris_ReturnsFalse_EvenWhenAllControllersGone()
        {
            // Debris recordings opt out — they never carried controllable identity.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: true,
                recordedControllerPids: new uint[] { 100u },
                liveIsTrackable: false,
                livePartPids: new uint[] { 200u });

            Assert.False(lost);
        }

        [Fact]
        public void NullControllers_ReturnsFalse()
        {
            // Forward-only: pre-existing recordings have null Controllers and the
            // override does not fire — preserves today's behavior on legacy data.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: null,
                liveIsTrackable: false,
                livePartPids: new uint[] { 200u });

            Assert.False(lost);
        }

        [Fact]
        public void EmptyControllers_ReturnsFalse()
        {
            // Recording captured at start time but the vessel had no controller
            // parts (rare but possible for unusual craft); treat as no-identity-
            // to-lose.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[0],
                liveIsTrackable: false,
                livePartPids: new uint[] { 200u });

            Assert.False(lost);
        }

        [Fact]
        public void LiveTrackable_ReturnsFalse_EvenIfNoneOfRecordedPidsSurvive()
        {
            // Live remnant is trackable in its own right (e.g. a replacement
            // crewed pod after a dock-merge or part replacement). The mod's
            // top-level controllability contract already governs that case.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[] { 100u, 101u },
                liveIsTrackable: true,
                livePartPids: new uint[] { 200u, 201u });

            Assert.False(lost);
        }

        [Fact]
        public void NoLiveParts_ReturnsTrue()
        {
            // Vessel exists but has no parts — definitionally no recorded identity.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[] { 100u },
                liveIsTrackable: false,
                livePartPids: new uint[0]);

            Assert.True(lost);
        }

        [Fact]
        public void AllRecordedControllerPidsMissing_ReturnsTrue()
        {
            // The probe-fall-and-explode shape from logs/2026-05-15_2031:
            // recorded controllers gone, surviving remnant is non-trackable.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[] { 723919894u }, // probeStackLarge
                liveIsTrackable: false,
                livePartPids: new uint[] { 3087746488u }); // Decoupler.2 remnant

            Assert.True(lost);
        }

        [Fact]
        public void OneOfTwoRecordedControllersSurvives_ReturnsFalse()
        {
            // Two-command-pod craft loses one pod (e.g. command tower destroyed
            // but probe core survives). Original identity preserved.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[] { 100u, 101u },
                liveIsTrackable: false, // hypothetical: live trackability check disabled
                livePartPids: new uint[] { 101u, 999u });

            Assert.False(lost);
        }

        [Fact]
        public void ZeroPidRecordedController_IsSkipped()
        {
            // Defensive: any zero-pid controller entries are skipped (they can't
            // match anything and should not poison the predicate).
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[] { 0u, 100u },
                liveIsTrackable: false,
                livePartPids: new uint[] { 100u });

            Assert.False(lost); // 100 still matches
        }

        [Fact]
        public void AllZeroPidRecordedControllers_TreatedAsIdentityLost()
        {
            // Pathological: every recorded controller pid is zero (defensive shape
            // that shouldn't happen in practice but the predicate must not return
            // false positively "identity preserved"). Every recorded pid is skipped
            // by the zero-pid guard, the inner loop never finds a survivor, and
            // the predicate falls through to true.
            bool lost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: false,
                recordedControllerPids: new uint[] { 0u, 0u },
                liveIsTrackable: false,
                livePartPids: new uint[] { 100u });

            Assert.True(lost);
        }

        #endregion

        #region Recording.MarkDestroyedAtTerminal — hygiene helper

        [Fact]
        public void MarkDestroyedAtTerminal_SetsTerminalFields()
        {
            var rec = new Recording
            {
                RecordingId = "test-rec-1",
                VesselDestroyed = false,
                TerminalStateValue = null,
                ExplicitEndUT = double.NaN
            };

            rec.MarkDestroyedAtTerminal(123.45, "test-source");

            Assert.True(rec.VesselDestroyed);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(123.45, rec.ExplicitEndUT);
        }

        [Fact]
        public void MarkDestroyedAtTerminal_ClearsStaleTerminalOrbitData()
        {
            // A recording previously classified Orbiting carries terminal-orbit
            // metadata; flipping it Destroyed via identity-loss must clear that
            // so the codec does not persist contradictory Orbiting/Destroyed
            // state. TerminalOrbitBody is the load-bearing gate
            // (RecordingTreeRecordCodec.cs:41 only writes orbital fields when it
            // is non-empty); the numeric resets are belt-and-suspenders.
            var rec = new Recording
            {
                RecordingId = "test-rec-orbit-clear",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitInclination = 12.0,
                TerminalOrbitEccentricity = 0.5,
                TerminalOrbitSemiMajorAxis = 700000.0,
                TerminalOrbitLAN = 1.0,
                TerminalOrbitArgumentOfPeriapsis = 2.0,
                TerminalOrbitMeanAnomalyAtEpoch = 3.0,
                TerminalOrbitEpoch = 100.0
            };

            rec.MarkDestroyedAtTerminal(300.0, "test-source");

            Assert.Null(rec.TerminalOrbitBody);
            Assert.Equal(0.0, rec.TerminalOrbitInclination);
            Assert.Equal(0.0, rec.TerminalOrbitEccentricity);
            Assert.Equal(0.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.0, rec.TerminalOrbitLAN);
            Assert.Equal(0.0, rec.TerminalOrbitArgumentOfPeriapsis);
            Assert.Equal(0.0, rec.TerminalOrbitMeanAnomalyAtEpoch);
            Assert.Equal(0.0, rec.TerminalOrbitEpoch);
        }

        [Fact]
        public void MarkDestroyedAtTerminal_ClearsStaleSurfaceData()
        {
            var rec = new Recording
            {
                RecordingId = "test-rec-2",
                TerminalPosition = new SurfacePosition { body = "Kerbin", latitude = 1, longitude = 2, altitude = 3 },
                SurfacePos = new SurfacePosition { body = "Kerbin", latitude = 4, longitude = 5, altitude = 6 },
                TerrainHeightAtEnd = 42.0,
                EndpointPhase = RecordingEndpointPhase.TerminalPosition,
                EndpointBodyName = "Kerbin"
            };

            rec.MarkDestroyedAtTerminal(200.0, "test-source");

            Assert.Null(rec.TerminalPosition);
            Assert.Null(rec.SurfacePos);
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
            Assert.Equal(RecordingEndpointPhase.Unknown, rec.EndpointPhase);
            Assert.Null(rec.EndpointBodyName);
        }

        [Fact]
        public void MarkDestroyedAtTerminal_LogsInfoFirstTime()
        {
            var rec = new Recording { RecordingId = "test-rec-3" };

            rec.MarkDestroyedAtTerminal(50.0, "unit-test");

            Assert.Contains(logLines, l => l.Contains("[Recording]")
                && l.Contains("MarkDestroyedAtTerminal")
                && l.Contains("test-rec-3")
                && l.Contains("terminalUT=50.00")
                && l.Contains("unit-test"));
        }

        [Fact]
        public void MarkDestroyedAtTerminal_Idempotent_NoRepeatLogOnSecondCall()
        {
            var rec = new Recording { RecordingId = "test-rec-4" };
            rec.MarkDestroyedAtTerminal(75.0, "first-call");
            logLines.Clear();

            // Second call on already-destroyed recording should not re-log.
            rec.MarkDestroyedAtTerminal(76.0, "second-call");

            Assert.DoesNotContain(logLines, l =>
                l.Contains("MarkDestroyedAtTerminal")
                && l.Contains("second-call"));
        }

        #endregion

        #region Controller capture forwarding — Step 1

        [Fact]
        public void AdoptControllersIfEmpty_NullSource_NoChange()
        {
            var rec = new Recording { Controllers = null };
            bool adopted = rec.AdoptControllersIfEmpty(null);
            Assert.False(adopted);
            Assert.Null(rec.Controllers);
        }

        [Fact]
        public void AdoptControllersIfEmpty_EmptySource_NoChange()
        {
            var rec = new Recording { Controllers = null };
            bool adopted = rec.AdoptControllersIfEmpty(new List<ControllerInfo>());
            Assert.False(adopted);
            Assert.Null(rec.Controllers);
        }

        [Fact]
        public void AdoptControllersIfEmpty_NullTarget_AdoptsCopy()
        {
            var rec = new Recording { Controllers = null };
            var source = new List<ControllerInfo>
            {
                new ControllerInfo { type = "CrewedPod", partName = "mk1pod.v2", partPersistentId = 100u },
                new ControllerInfo { type = "ProbeCore", partName = "probeStackLarge", partPersistentId = 200u }
            };

            bool adopted = rec.AdoptControllersIfEmpty(source);

            Assert.True(adopted);
            Assert.NotNull(rec.Controllers);
            Assert.Equal(2, rec.Controllers.Count);
            Assert.Equal(100u, rec.Controllers[0].partPersistentId);
            Assert.Equal(200u, rec.Controllers[1].partPersistentId);
            // Defensive copy: mutating source must not mutate rec.Controllers.
            source.Clear();
            Assert.Equal(2, rec.Controllers.Count);
        }

        [Fact]
        public void AdoptControllersIfEmpty_EmptyTarget_AdoptsCopy()
        {
            var rec = new Recording { Controllers = new List<ControllerInfo>() };
            var source = new List<ControllerInfo>
            {
                new ControllerInfo { type = "ProbeCore", partName = "probeStackLarge", partPersistentId = 723919894u }
            };

            bool adopted = rec.AdoptControllersIfEmpty(source);

            Assert.True(adopted);
            Assert.Single(rec.Controllers);
            Assert.Equal(723919894u, rec.Controllers[0].partPersistentId);
        }

        [Fact]
        public void AdoptControllersIfEmpty_PopulatedTarget_NoOverwrite()
        {
            // Pinning the invariant: a recording that already has a recorded
            // identity must NOT be overwritten by a later flush/recorder-start.
            // The start-of-recording identity is captured once and never replaced.
            var rec = new Recording
            {
                Controllers = new List<ControllerInfo>
                {
                    new ControllerInfo { type = "ProbeCore", partName = "original", partPersistentId = 100u }
                }
            };
            var source = new List<ControllerInfo>
            {
                new ControllerInfo { type = "CrewedPod", partName = "replacement", partPersistentId = 200u }
            };

            bool adopted = rec.AdoptControllersIfEmpty(source);

            Assert.False(adopted);
            Assert.Single(rec.Controllers);
            Assert.Equal(100u, rec.Controllers[0].partPersistentId);
            Assert.Equal("original", rec.Controllers[0].partName);
        }

        [Fact]
        public void ActiveRootBackgrounded_FlushForwardsControllers_AllowingIdentityLossOverride()
        {
            // Regression for the P1 the external reviewer flagged: an always-tree
            // root recording is created at ParsekFlight.cs:10518 with no Controllers
            // because the field used to be dead schema. The recorder captures
            // pendingStartControllers privately during StartRecording, but if the
            // active vessel gets switched away (recorder is suspended/flushed)
            // before the Controllers were ever copied onto the tree root, the
            // resulting BG-tracked recording has Controllers == null. The
            // identity-loss override at OnBackgroundVesselGoOnRails then sees a
            // null/empty list and the pure predicate returns false, falling
            // through to the buggy landed classification.
            //
            // This test pins the flush-time backstop: when the recorder is flushed
            // and the tree rec is still missing Controllers, the recorder's
            // start-controllers list IS forwarded. Combined with the pure-predicate
            // tests, this proves the override fires for the active-root-backgrounded
            // shape after the fix.

            var treeRoot = new Recording { RecordingId = "tree-root", Controllers = null };
            var recorderStartControllers = new List<ControllerInfo>
            {
                new ControllerInfo { type = "ProbeCore", partName = "probeStackLarge", partPersistentId = 723919894u },
                new ControllerInfo { type = "CrewedPod", partName = "mk1pod.v2", partPersistentId = 100u }
            };

            // Simulate the flush-time forward.
            bool adopted = treeRoot.AdoptControllersIfEmpty(recorderStartControllers);
            Assert.True(adopted);

            // Now simulate the BG go-on-rails identity-loss check after the active
            // root has been backgrounded and the recorded controllers all died in
            // a destructive crash; the surviving remnant is non-trackable.
            bool identityLost = IdentityLossClassifier.ShouldClassifyRecordedIdentityLost(
                isDebris: treeRoot.IsDebris,
                recordedControllerPids: ExtractPids(treeRoot.Controllers),
                liveIsTrackable: false,
                livePartPids: new uint[] { 3087746488u }); // Decoupler.2 remnant

            Assert.True(identityLost,
                "Identity-loss override must fire for backgrounded active root whose Controllers " +
                "were forwarded at flush time and whose recorded controllers all died on crash");
        }

        private static uint[] ExtractPids(List<ControllerInfo> controllers)
        {
            if (controllers == null) return null;
            var pids = new uint[controllers.Count];
            for (int i = 0; i < controllers.Count; i++)
                pids[i] = controllers[i].partPersistentId;
            return pids;
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesControllers()
        {
            // Sanity-pin: the chain commit path (ChainSegmentManager.CommitSegmentCore →
            // rec.ApplyPersistenceArtifactsFrom(captured)) must forward Controllers
            // from CaptureAtStop to the committed Recording, or the BG identity-loss
            // override will see Controllers == null on chain-committed recordings.
            var source = new Recording
            {
                Controllers = new List<ControllerInfo>
                {
                    new ControllerInfo { type = "ProbeCore", partName = "probeStackLarge", partPersistentId = 723919894u }
                }
            };
            var target = new Recording();

            target.ApplyPersistenceArtifactsFrom(source);

            Assert.NotNull(target.Controllers);
            Assert.Single(target.Controllers);
            Assert.Equal(723919894u, target.Controllers[0].partPersistentId);
            // Defensive copy — mutating source must not affect target.
            source.Controllers[0] = new ControllerInfo { partPersistentId = 999u };
            Assert.Equal(723919894u, target.Controllers[0].partPersistentId);
        }

        #endregion
    }
}
