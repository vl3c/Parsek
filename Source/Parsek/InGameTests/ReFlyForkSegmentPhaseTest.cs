using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Behaviour-level test for the Re-Fly fork SegmentPhase tagger.
    ///
    /// Verifies that <see cref="RewindInvoker.TagForkInitialSegmentPhase"/>
    /// correctly classifies a provisional <see cref="Recording"/> from the
    /// live <c>FlightGlobals.ActiveVessel</c>, using the same vocabulary
    /// (<c>"atmo"</c> / <c>"exo"</c> / <c>"approach"</c> / <c>"surface"</c>)
    /// the rest of the runtime taggers use. Lives in the in-game framework
    /// because <c>Vessel</c> is a Unity type.
    ///
    /// xUnit covers the inheritance-drop contract and the null-vessel
    /// fallback (see <c>RewindForkSegmentPhaseTests</c>); this test plus
    /// any future fork-creation flow with a live vessel exercises the
    /// positive classification path end to end.
    ///
    /// See <c>docs/dev/plans/fix-refly-fork-segment-phase-inheritance.md</c>
    /// for the rationale.
    /// </summary>
    public class ReFlyForkSegmentPhaseTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-Fly fork: TagForkInitialSegmentPhase classifies from live vessel")]
        public void TagForkInitialSegmentPhase_FromLiveActiveVessel_ProducesPhaseAndBody()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(v, "FlightGlobals.ActiveVessel should exist in Flight scene");
            InGameAssert.IsNotNull(v.mainBody, "Active vessel should have a mainBody");

            var provisional = new Recording
            {
                RecordingId = "ingame-test-fork-rec",
                // Phase fields start null — same shape as a fresh provisional
                // emerging from BuildProvisionalRecording before
                // CopyInheritedIdentityForFork runs.
            };

            string sessionId = "sess_ingame_test_phase_tag";
            RewindInvoker.TagForkInitialSegmentPhase(provisional, v, sessionId);

            // Body is always set to the vessel's mainBody.name when classification fires.
            InGameAssert.IsTrue(
                provisional.SegmentBodyName == v.mainBody.name,
                $"SegmentBodyName expected '{v.mainBody.name}'; got '{provisional.SegmentBodyName ?? "<null>"}'");

            // Phase comes from the same vocabulary used by every other tagger.
            // The exact value depends on the active vessel's situation, so the
            // test asserts membership rather than a specific value — that way
            // it works whether the player runs it from launchpad (surface),
            // ascent (atmo/exo), or orbit (exo). A future regression where the
            // helper produces an off-vocabulary string (e.g. "Atmospheric") or
            // null while the body is non-null will fail the membership check.
            //
            // We deliberately do NOT inline-reimplement the classifier here to
            // cross-check phase=expected — that would just assert f(x)==f(x)
            // since the helper delegates to the same TagSegmentPhaseIfMissing
            // we'd be reimplementing. Vocabulary membership + non-null body is
            // the load-bearing contract.
            string phase = provisional.SegmentPhase;
            InGameAssert.IsTrue(
                phase == "atmo"
                    || phase == "exo"
                    || phase == "approach"
                    || phase == "surface",
                $"SegmentPhase expected one of [atmo, exo, approach, surface]; got '{phase ?? "<null>"}'");

            ParsekLog.Info("RewindTest",
                $"TagForkInitialSegmentPhase_FromLiveActiveVessel: classifier produced " +
                $"body={provisional.SegmentBodyName} phase={phase} for vessel '{v.vesselName}' " +
                $"(situation={v.situation} alt={v.altitude:F0}m)");
        }
    }
}
