using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // The confirmation dialog and its Re-Fly advisory: sibling slot names, the advisory body, its log line and the capped name lists.
    internal static partial class RewindInvoker
    {
        /// <summary>
        /// Spawns the "Rewind?" confirmation PopupDialog. On accept, starts
        /// the <see cref="StartInvoke"/> pre-load phase; the post-load phase
        /// is driven by <see cref="ParsekScenario.OnLoad"/> calling
        /// <see cref="ConsumePostLoad"/> once the scene reload lands.
        /// <para>
        /// The body carries the pre-invoke advisory (audit item C2): what the
        /// revert takes back, which sibling craft become replays, and what is
        /// preserved. It is informational only — it changes no control flow and
        /// adds no button. See <see cref="ComposeReFlyAdvisoryBody"/>.
        /// </para>
        /// </summary>
        internal static void ShowDialog(RewindPoint rp, int selectedSlotListIndex)
        {
            if (rp == null)
            {
                ParsekLog.Warn(UITag, "ShowDialog called with null RP");
                return;
            }
            if (rp.ChildSlots == null || selectedSlotListIndex < 0 || selectedSlotListIndex >= rp.ChildSlots.Count)
            {
                ParsekLog.Warn(UITag,
                    $"ShowDialog: invalid slot list index {selectedSlotListIndex} (slots={rp.ChildSlots?.Count ?? 0}) for rp={rp.RewindPointId}");
                return;
            }

            var selected = rp.ChildSlots[selectedSlotListIndex];
            int selectedSlotId = selected != null ? selected.SlotIndex : selectedSlotListIndex;
            string title = "Confirm: Re-Fly";
            string message =
                "Do you want to fly this again? This will take you to the moment after " +
                "separation and you will be in control of the craft / Kerbal.";

            // Pre-invoke advisory (audit C2). Wrapped: a broken RecordingStore /
            // slot map must not cost the player the whole Re-Fly dialog, so the
            // advisory degrades to absent rather than throwing out of ShowDialog.
            //
            // Skipped when the selected slot is null: selectedSlotId then falls
            // back to the LIST index, which is not a SlotIndex, so the sibling
            // resolver could not exclude the re-fly target and would name it as
            // a craft being put away. StartInvoke refuses this shape anyway
            // ("invalid slot"); a wrong advisory is worse than none.
            if (selected == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Pre-invoke advisory: skipped rp={rp.RewindPointId ?? "<null>"} " +
                    $"listIndex={selectedSlotListIndex} reason=null-selected-slot");
            }
            else
            {
                try
                {
                    // Compose fully BEFORE mutating the dialog message so the outer
                    // catch's "shown without the advisory" claim is true on every
                    // path it can catch: a throw from name resolution or body
                    // composition leaves `message` untouched. The log emit runs
                    // after the append has succeeded, so a throw there would leave
                    // the advisory IN the dialog — it gets its own guard with the
                    // opposite wording instead of sharing the outer catch.
                    var siblingNames = ResolveReFlySiblingSlotNames(
                        rp, selectedSlotId, RecordingStore.CommittedRecordings);
                    string advisoryBody = ComposeReFlyAdvisoryBody(siblingNames);
                    message += "\n\n" + advisoryBody;
                    try
                    {
                        LogReFlyAdvisory(rp.RewindPointId, selectedSlotId, rp.UT, siblingNames);
                    }
                    catch (Exception logEx)
                    {
                        ParsekLog.Warn(InvokeTag,
                            $"Pre-invoke advisory: log emit failed rp={rp.RewindPointId ?? "<null>"} " +
                            $"slot={selectedSlotId} exType={logEx.GetType().Name} " +
                            $"message={logEx.Message ?? "<null>"} — dialog DOES carry the advisory");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Pre-invoke advisory: skipped rp={rp.RewindPointId ?? "<null>"} " +
                        $"slot={selectedSlotId} exType={ex.GetType().Name} " +
                        $"message={ex.Message ?? "<null>"} — dialog shown without the advisory");
                }
            }

            var capturedRp = rp;
            var capturedSlotListIdx = selectedSlotListIndex;
            var capturedSlotId = selectedSlotId;
            var capturedSelected = selected;

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRewindInvoke",
                    message,
                    title,
                    HighLogic.UISkin,
                    // Explicit width: MultiOptionDialog's no-width ctor defaults
                    // to a 300 px dialogRect, and the advisory body would render
                    // as a very tall, very narrow column at that width.
                    AdvisoryDialogWidth,
                    new DialogGUIButton("Fly", () =>
                    {
                        ParsekLog.Info(UITag,
                            $"Invoked rec={capturedSelected?.OriginChildRecordingId ?? "<none>"} " +
                            $"rp={capturedRp.RewindPointId} slot={capturedSlotId} listIndex={capturedSlotListIdx}");
                        StartInvoke(capturedRp, capturedSelected);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info(UITag,
                            $"Cancelled rp={capturedRp.RewindPointId} slot={capturedSlotId} listIndex={capturedSlotListIdx}");
                    })
                ),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Resolves the display names of the OTHER slots of this rewind point —
        /// the vessels the pre-load scrub removes from the loaded save so their
        /// recordings replay as ghosts (see
        /// <see cref="ScrubQuicksaveToSelectedSlotForReFly"/>).
        /// <para>
        /// Keyed on <see cref="ChildSlot.SlotIndex"/>, not the list index,
        /// because the scrub's own <c>BuildNonSelectedSlotPidSet</c> compares
        /// against the slot-map VALUES, which are <c>SlotIndex</c>. A slot that
        /// appears in NEITHER <see cref="RewindPoint.PidSlotMap"/> nor
        /// <see cref="RewindPoint.RootPartPidMap"/> is deliberately omitted: the
        /// scrub cannot match its vessel, so nothing of it is removed and
        /// promising the player otherwise would be a false statement.
        /// </para>
        /// <para>
        /// Names come from the origin recording's <c>VesselName</c> via a raw
        /// id lookup — deliberately NOT an ERS-visible lookup. A sibling slot
        /// superseded by an earlier Re-Fly is hidden from the ERS while its
        /// vessel is still in the quicksave and still gets removed, so the raw
        /// list is the correct source for "what leaves the world". Falls back to
        /// <c>slot N</c> when the origin recording cannot be resolved.
        /// </para>
        /// </summary>
        internal static List<string> ResolveReFlySiblingSlotNames(
            RewindPoint rp,
            int selectedSlotIndex,
            IReadOnlyList<Recording> recordings)
        {
            var names = new List<string>();
            if (rp == null || rp.ChildSlots == null || rp.ChildSlots.Count == 0)
                return names;

            var mappedSlotIndices = new HashSet<int>();
            AddMappedSlotIndices(rp.PidSlotMap, mappedSlotIndices);
            AddMappedSlotIndices(rp.RootPartPidMap, mappedSlotIndices);

            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    var rec = recordings[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    byId[rec.RecordingId] = rec;
                }
            }

            for (int i = 0; i < rp.ChildSlots.Count; i++)
            {
                var slot = rp.ChildSlots[i];
                if (slot == null) continue;
                if (slot.SlotIndex == selectedSlotIndex) continue;
                if (!mappedSlotIndices.Contains(slot.SlotIndex)) continue;

                string name = null;
                if (!string.IsNullOrEmpty(slot.OriginChildRecordingId)
                    && byId.TryGetValue(slot.OriginChildRecordingId, out Recording origin)
                    && origin != null
                    && !string.IsNullOrEmpty(origin.VesselName))
                {
                    name = origin.VesselName;
                }

                names.Add(name ?? $"slot {slot.SlotIndex.ToString(CultureInfo.InvariantCulture)}");
            }

            return names;
        }

        private static void AddMappedSlotIndices(
            Dictionary<uint, int> map, HashSet<int> into)
        {
            if (map == null) return;
            foreach (var kv in map)
            {
                if (kv.Key == 0u) continue;
                into.Add(kv.Value);
            }
        }

        /// <summary>
        /// Composes the player-facing advisory block appended to the Re-Fly
        /// confirmation dialog body. Three statements, in the order a player
        /// needs them: what the revert takes back, which of this rewind point's
        /// other craft become replays, and what is preserved. Pure — no KSP
        /// singletons, no <see cref="RecordingStore"/> read.
        /// </summary>
        internal static string ComposeReFlyAdvisoryBody(IReadOnlyList<string> siblingNames)
        {
            string affected = (siblingNames == null || siblingNames.Count == 0)
                ? "No other craft from this separation are put away."
                : "Put away and replayed as ghosts, from this same separation: "
                  + FormatCappedProseList(siblingNames, MaxAdvisorySlotNamesShown) + ".";

            return
                "The world goes back to how it stood at this rewind point. Your "
                + PatchedCareerFacetsSummary + " are carried forward. This flight "
                + "earned nothing after its separation, so a merge takes nothing back: "
                + "it only adds what the new flight earns. Anything outside "
                + "those goes back with the clock: kerbal experience earned since then, "
                + "resource-survey unlocks, contract waypoint progress, and "
                + "deployed-science gathered since then.\n\n"
                + affected + "\n\n"
                + "Craft you recovered after this point are back in flight, and their "
                + "recovery rewards are returned to the ledger.\n\n"
                + "Everything else stays where it is - your other vessels, stations, "
                + "tracked asteroids and comets, and planted flags are all preserved.";
        }

        /// <summary>
        /// Emits the advisory to KSP.log at <c>Info</c> under the
        /// <c>[Rewind]</c> subsystem. Separate from the formatter so the emit
        /// itself is exercised by a log-capture cell rather than only the string.
        /// </summary>
        internal static void LogReFlyAdvisory(
            string rewindPointId,
            int selectedSlotIndex,
            double rewindUT,
            IReadOnlyList<string> siblingNames)
        {
            ParsekLog.Info(InvokeTag,
                FormatReFlyAdvisoryLogLine(
                    rewindPointId, selectedSlotIndex, rewindUT, siblingNames));
        }

        /// <summary>
        /// Grep-stable single-line record of the same facts the dialog states,
        /// so a KSP.log shows what the player was told even when they Cancel.
        /// Emitted at <c>Info</c> under the <c>[Rewind]</c> subsystem.
        /// </summary>
        internal static string FormatReFlyAdvisoryLogLine(
            string rewindPointId,
            int selectedSlotIndex,
            double rewindUT,
            IReadOnlyList<string> siblingNames)
        {
            var ic = CultureInfo.InvariantCulture;
            int count = siblingNames != null ? siblingNames.Count : 0;
            return
                $"Pre-invoke advisory: rp={rewindPointId ?? "<null>"} " +
                $"slot={selectedSlotIndex.ToString(ic)} rewindUT={rewindUT.ToString("F1", ic)} " +
                $"worldRevertsOutsideFacets='{PatchedCareerFacetsSummary}' " +
                $"siblingSlotsPutAway={count.ToString(ic)} " +
                $"names={FormatCappedNameList(siblingNames, MaxAdvisorySlotNamesShown)} " +
                $"preserved=unrelated-vessels+stations+asteroids+comets+flags";
        }

        /// <summary>
        /// Player-facing sibling of <see cref="FormatCappedNameList"/>: same cap
        /// and same overflow count, rendered as a bare comma list with no
        /// brackets, because the dialog body is prose and the log line is not.
        /// Empty / null renders as an empty string (callers must not reach this
        /// with an empty list — <see cref="ComposeReFlyAdvisoryBody"/> branches
        /// to its own "no other craft" sentence first).
        /// </summary>
        internal static string FormatCappedProseList(IReadOnlyList<string> names, int cap)
        {
            if (names == null || names.Count == 0) return "";
            if (cap < 1) cap = 1;

            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < names.Count && shown < cap; i++)
            {
                if (shown > 0) sb.Append(", ");
                sb.Append(string.IsNullOrEmpty(names[i]) ? "<unnamed>" : names[i]);
                shown++;
            }
            if (names.Count > shown)
            {
                sb.Append(" (+")
                  .Append((names.Count - shown).ToString(CultureInfo.InvariantCulture))
                  .Append(" more)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Renders a name list as <c>[a, b, c]</c>, capped at
        /// <paramref name="cap"/> entries with the remainder reported as
        /// <c>(+N more)</c>. Empty / null renders as <c>[]</c>. Log-line form —
        /// the dialog uses <see cref="FormatCappedProseList"/>.
        /// </summary>
        internal static string FormatCappedNameList(IReadOnlyList<string> names, int cap)
        {
            if (names == null || names.Count == 0) return "[]";
            if (cap < 1) cap = 1;

            var sb = new System.Text.StringBuilder("[");
            int shown = 0;
            for (int i = 0; i < names.Count && shown < cap; i++)
            {
                if (shown > 0) sb.Append(", ");
                sb.Append(string.IsNullOrEmpty(names[i]) ? "<unnamed>" : names[i]);
                shown++;
            }
            sb.Append("]");
            if (names.Count > shown)
            {
                sb.Append(" (+")
                  .Append((names.Count - shown).ToString(CultureInfo.InvariantCulture))
                  .Append(" more)");
            }
            return sb.ToString();
        }
    }
}
