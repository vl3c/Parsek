using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 7 of Rewind-to-Staging (design §3.3.1 + §11.5): verify the
    /// kerbal dual-residence carve-out — a kerbal physically embodied on the
    /// provisional re-fly vessel is exempt from reservation / retirement lock
    /// for the session duration.
    ///
    /// <para>Preconditions: an active re-fly session + at least one kerbal
    /// aboard the currently-active re-fly vessel. The test auto-skips when
    /// the session is not live.</para>
    /// </summary>
    public class KerbalDualResidenceCarveOutTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "§3.3.1: live re-fly crew bypass reservation lock")]
        public void KerbalDualResidenceCarveOut()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind before running this test.");
                return;
            }

            var active = FlightGlobals.ActiveVessel;
            if (active == null)
            {
                InGameAssert.Skip("No active vessel in FLIGHT scene.");
                return;
            }

            if (!CrewReservationManager.ActiveVesselMatchesReFlyRecording(active, marker))
            {
                InGameAssert.Skip(
                    "Active vessel does not match the provisional re-fly recording's " +
                    "VesselPersistentId — the carve-out only applies to the re-fly vessel.");
                return;
            }

            var crew = active.GetVesselCrew();
            if (crew == null || crew.Count == 0)
            {
                InGameAssert.Skip("Active re-fly vessel has no crew aboard — nothing to carve out.");
                return;
            }

            var kerbals = LedgerOrchestrator.Kerbals;
            InGameAssert.IsNotNull(kerbals, "LedgerOrchestrator.Kerbals is null");

            int carvedOutCount = 0;
            int reservedOrRetiredCount = 0;
            for (int i = 0; i < crew.Count; i++)
            {
                var pcm = crew[i];
                if (pcm == null) continue;

                bool liveReFlyCrew = CrewReservationManager.IsLiveReFlyCrew(pcm, marker);
                InGameAssert.IsTrue(liveReFlyCrew,
                    $"IsLiveReFlyCrew expected true for '{pcm.name}' on re-fly vessel " +
                    $"pid={active.persistentId} (marker sess={marker.SessionId})");

                // Only kerbals actually present in the reservation / retired
                // sets exercise the carve-out; others naturally return false
                // from ShouldFilterFromCrewDialog.
                bool wasReservedOrRetired = kerbals.IsManaged(pcm.name);
                if (wasReservedOrRetired)
                {
                    reservedOrRetiredCount++;
                    bool filtered = kerbals.ShouldFilterFromCrewDialog(pcm.name);
                    InGameAssert.IsFalse(filtered,
                        $"ShouldFilterFromCrewDialog should return false for live re-fly crew '{pcm.name}' " +
                        $"(managed by reservation/retirement), but it returned true — carve-out is not applied.");
                    carvedOutCount++;
                }
            }

            ParsekLog.Info("RewindTest",
                $"KerbalDualResidenceCarveOut: asserted IsLiveReFlyCrew for {crew.Count} crew; " +
                $"reservedOrRetired={reservedOrRetiredCount} carvedOut={carvedOutCount}");

            if (reservedOrRetiredCount == 0)
            {
                ParsekLog.Info("RewindTest",
                    "KerbalDualResidenceCarveOut: no crew on active vessel were reserved/retired; " +
                    "IsLiveReFlyCrew predicate pass is the only assertion exercised.");
            }
        }
    }
}
