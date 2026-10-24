using System;
using System.Collections.Generic;
using System.Globalization;
using Contracts;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Contracts x Re-Fly, live (roadmap XS-6), against today's rule (design section 2.12):
    /// a contract row is a sealing row, so a Re-Fly is never offered for a flight that
    /// accepted, completed, failed or cancelled a contract after its separation, and the
    /// broad contract tombstone the older form of this cell asserted is unreachable. The
    /// older form also drove the legacy <c>SupersedeCommit.CommitSupersede</c>, which has no
    /// production caller, and skipped on every host because the state it looked for cannot
    /// exist. Runs in FLIGHT during a live session (RF-6, RF-12*), skips without one.
    ///
    /// <para>Asserts:</para>
    /// <list type="number">
    ///   <item><description>The retire set the merge would tombstone now holds no contract
    ///       row (Rule 1: the slot would not have been offered).</description></item>
    ///   <item><description>No contract stock holds Active was accepted, in the committed
    ///       timeline, after the session's clock. The invoke walks the whole ledger, and a
    ///       contract accepted at the Space Center after the rewind point used to come back
    ///       Active, so the re-fly could complete or fail it before it was accepted (todo
    ///       REFLY-SESSION-SHOWS-CONTRACT-ACCEPTED-LATER). Vacuous on a host without a
    ///       contract system (sandbox).</description></item>
    /// </list>
    /// </summary>
    public class ContractTombstonesAcrossSupersedeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-fly session: no contract row in the retire set, and no contract accepted after the session clock is Active")]
        public void ContractTombstonesAcrossSupersede()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind first, finish the re-fly, and rerun.");
                return;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                InGameAssert.Skip("Marker has no ActiveReFlyRecordingId — cannot exercise the session.");
                return;
            }

            var ic = CultureInfo.InvariantCulture;

            // Invariant 1: no contract row in the merge's retire set.
            int closureCount;
            var retire = SupersedeCommit.BuildMergeRetireSet(
                marker, new HashSet<string> { provisionalId }, out closureCount);
            var contractRows = new List<string>();
            for (int i = 0; i < retire.Count; i++)
            {
                var a = retire[i];
                if (a == null) continue;
                if (a.Type == GameActionType.ContractAccept
                    || a.Type == GameActionType.ContractComplete
                    || a.Type == GameActionType.ContractFail
                    || a.Type == GameActionType.ContractCancel)
                    contractRows.Add(a.Type + ":" + (a.ActionId ?? "(no id)"));
            }
            InGameAssert.AreEqual(0, contractRows.Count,
                "A live Re-Fly session's retire set holds contract row(s), which Rule 1 seals: " +
                string.Join(", ", contractRows.ToArray()));

            // Invariant 2: nothing Active in stock that the committed timeline accepts later.
            double nowUT = Planetarium.GetUniversalTime();
            int activeChecked = 0;
            var acceptedLater = new List<string>();
            var system = ContractSystem.Instance;
            if (system != null && system.Contracts != null)
            {
                var latestAccept = new Dictionary<string, double>(StringComparer.Ordinal);
                var els = EffectiveState.ComputeELS();
                for (int i = 0; i < els.Count; i++)
                {
                    var a = els[i];
                    if (a == null || a.Type != GameActionType.ContractAccept
                        || string.IsNullOrEmpty(a.ContractId))
                        continue;
                    double prior;
                    if (!latestAccept.TryGetValue(a.ContractId, out prior) || a.UT > prior)
                        latestAccept[a.ContractId] = a.UT;
                }
                for (int i = 0; i < system.Contracts.Count; i++)
                {
                    var c = system.Contracts[i];
                    if (c == null || c.ContractState != Contract.State.Active) continue;
                    activeChecked++;
                    string key = c.ContractGuid.ToString();
                    double acceptUT;
                    if (latestAccept.TryGetValue(key, out acceptUT)
                        && acceptUT > nowUT + KspStatePatcher.ContractAcceptClockToleranceSeconds)
                        acceptedLater.Add(key + "@" + acceptUT.ToString("F1", ic));
                }
            }
            InGameAssert.AreEqual(0, acceptedLater.Count,
                "Contract(s) Active in stock during the Re-Fly session that the committed timeline accepts only after now=" +
                nowUT.ToString("F1", ic) + ": " + string.Join(", ", acceptedLater.ToArray()));

            ParsekLog.Info("RewindTest",
                $"ContractTombstonesAcrossSupersede: session={marker.SessionId} " +
                $"retireSet={retire.Count.ToString(ic)} closure={closureCount.ToString(ic)} contractRows=0 " +
                $"contractSystem={(system != null ? "present" : "absent")} " +
                $"activeChecked={activeChecked.ToString(ic)} acceptedLater=0 nowUT={nowUT.ToString("F1", ic)}");
        }
    }
}
