using System.Collections.Generic;
using System.Linq;

namespace Parsek.InGameTests
{
    /// <summary>
    /// A stock rescue's roster flip, live: completing a rescue contract (KerbalRescue /
    /// RecoverAsset) turns the stranded kerbal from <c>KerbalType.Unowned</c> into
    /// <c>KerbalType.Crew</c> through the <c>ProtoCrewMember.type</c> setter, which fires
    /// <c>GameEvents.onKerbalTypeChange</c>. Parsek captures that one transition as a
    /// <c>KerbalRescued</c> event (<c>GameStateRecorder.OnKerbalTypeChange</c>), the commit
    /// converter turns it into a <c>KerbalRescue</c> ledger row, and the kerbals walk keeps the
    /// rescued kerbal as a ledger-created roster entry. No lane and no cell had run any of it
    /// against the live game. The full rendezvous (a rescue flight) is a later lane; this cell
    /// drives the stock flip on a fresh Unowned kerbal instead.
    ///
    /// <para>SAFETY: SPACECENTER batches restore <c>persistent.sfs</c> on disk only, so the cell
    /// undoes every in-memory change: the captured event is removed from the store and the kerbal
    /// from the roster (under the crew suppression, so the removal books nothing). The walk runs on
    /// a throwaway <see cref="KerbalsModule"/>, never the player's ledger.</para>
    /// </summary>
    public class RescuedKerbalLedgerTests
    {
        [InGameTest(Category = "Ledger", Scene = GameScenes.SPACECENTER,
            Description = "A stock rescue's roster flip (Unowned -> Crew through ProtoCrewMember.type) is captured as one KerbalRescued event with the kerbal's trait, converts into a KerbalRescue ledger row for the committing recording, and the kerbals walk keeps the rescued kerbal as a ledger-created roster entry; the cell removes the kerbal and the event afterwards.")]
        public void RescuedKerbalFlipBooksAKerbalRescueRow()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.CrewRoster == null)
            {
                InGameAssert.Skip("needs a loaded game with a crew roster");
                return;
            }
            if (GameStateRecorder.SuppressCrewEvents)
            {
                InGameAssert.Skip("crew event capture is suppressed right now (a replay or restore is running)");
                return;
            }

            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            ProtoCrewMember pcm = null;
            string name = null;
            try
            {
                // The stranded kerbal a rescue contract spawns: Unowned, not on the crew list.
                using (SuppressionGuard.Crew())
                    pcm = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Unowned);
                InGameAssert.IsNotNull(pcm, "KerbalRoster.GetNewKerbal(Unowned) should return a kerbal");
                InGameAssert.AreEqual(ProtoCrewMember.KerbalType.Unowned, pcm.type,
                    "The new kerbal should start Unowned");
                name = pcm.name;
                string trait = pcm.experienceTrait != null ? pcm.experienceTrait.TypeName : null;
                int before = GameStateStore.Events.Count(e =>
                    e.eventType == GameStateEventType.KerbalRescued && e.key == name);
                InGameAssert.AreEqual(0, before, $"No KerbalRescued event should name '{name}' yet");

                // ---- the stock flip: what a completed rescue does to the kerbal ----
                pcm.type = ProtoCrewMember.KerbalType.Crew;

                List<GameStateEvent> rescued = GameStateStore.Events
                    .Where(e => e.eventType == GameStateEventType.KerbalRescued && e.key == name)
                    .ToList();
                InGameAssert.AreEqual(1, rescued.Count,
                    $"The Unowned -> Crew flip should be captured as exactly one KerbalRescued event for '{name}'");
                GameStateEvent captured = rescued[0];
                InGameAssert.IsTrue((captured.detail ?? "").Contains("trait="),
                    $"The KerbalRescued event should carry the trait; detail='{captured.detail}'");
                if (!string.IsNullOrEmpty(trait))
                    InGameAssert.IsTrue(captured.detail.Contains("trait=" + trait),
                        $"The KerbalRescued event should carry the trait '{trait}'; detail='{captured.detail}'");

                // A second flip back and forth is not a second rescue: only Unowned -> Crew counts.
                using (SuppressionGuard.Crew())
                    pcm.type = ProtoCrewMember.KerbalType.Tourist;
                pcm.type = ProtoCrewMember.KerbalType.Crew;
                InGameAssert.AreEqual(1, GameStateStore.Events.Count(e =>
                        e.eventType == GameStateEventType.KerbalRescued && e.key == name),
                    "A Tourist -> Crew flip should book no second KerbalRescued event");

                // ---- the commit converter: the row the rescuing recording carries ----
                string recordingId = "rescue-cell-" + System.Guid.NewGuid().ToString("N");
                var tagged = new GameStateEvent
                {
                    ut = captured.ut,
                    eventType = captured.eventType,
                    key = captured.key,
                    detail = captured.detail,
                    recordingId = recordingId
                };
                List<GameAction> actions = GameStateEventConverter.ConvertEvents(
                    new List<GameStateEvent> { tagged }, recordingId, captured.ut - 1.0, captured.ut + 1.0);
                InGameAssert.AreEqual(1, actions.Count, "The KerbalRescued event should convert into one ledger row");
                GameAction row = actions[0];
                InGameAssert.AreEqual(GameActionType.KerbalRescue, row.Type, "The row should be a KerbalRescue");
                InGameAssert.AreEqual(name, row.KerbalName, "The row should name the rescued kerbal");
                InGameAssert.AreEqual(recordingId, row.RecordingId, "The row should belong to the committing recording");
                if (!string.IsNullOrEmpty(trait))
                    InGameAssert.AreEqual(trait, row.KerbalRole, "The row should carry the kerbal's trait");

                // ---- the walk: a ledger-created roster entry, kept by the ledger ----
                var module = new KerbalsModule();
                module.ProcessAction(row);
                InGameAssert.IsTrue(module.LedgerCreatedKerbals.Contains(name),
                    $"The kerbals walk should keep '{name}' as a ledger-created roster entry");
                ParsekLog.Info("TestRunner",
                    $"RescuedKerbal cell: '{name}' flipped Unowned -> Crew, one KerbalRescued event, " +
                    $"one KerbalRescue row, retained by the walk");
            }
            finally
            {
                if (!string.IsNullOrEmpty(name))
                {
                    // GameStateEvent is a struct, so every event the cell may have booked is
                    // removed by identity, including any a failed assert left behind.
                    var booked = GameStateStore.Events
                        .Where(e => e.eventType == GameStateEventType.KerbalRescued && e.key == name)
                        .ToList();
                    foreach (var e in booked)
                        GameStateStore.RemoveEvent(e);
                }
                if (pcm != null)
                {
                    using (SuppressionGuard.Crew())
                    {
                        try { roster.Remove(pcm); }
                        catch (System.Exception ex)
                        {
                            ParsekLog.Warn("TestRunner", $"RescuedKerbal cell: roster remove threw: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
