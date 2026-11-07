using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Rewind-to-Staging persistence: the staged rewind points and row lists saved into and loaded
    // from the scenario node.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Persists the Rewind-to-Staging Phase 1 collections and singletons
        /// (design sections 5.1 - 5.9). Idempotent: always re-creates the four
        /// parent nodes from scratch so stale entries from a prior save do not
        /// leak into the new one.
        /// </summary>
        private void SaveRewindStagingState(ConfigNode node)
        {
            node.RemoveNodes("REWIND_POINTS");
            node.RemoveNodes("RECORDING_SUPERSEDES");
            node.RemoveNodes("RECORDING_REWIND_RETIREMENTS");
            node.RemoveNodes("LEDGER_TOMBSTONES");
            node.RemoveNodes(ReFlySessionMarker.NodeName);
            node.RemoveNodes(MergeJournal.NodeName);
            node.RemoveNodes(StockActionIntentMarker.NodeName);
            node.RemoveNodes(SwitchSegmentSession.NodeName);
            node.RemoveNodes(TestBatchMarker.NodeName);

            int rpCount = SaveStagingList(node, "REWIND_POINTS", RewindPoints, (item, parent) => item.SaveInto(parent));

            int supersedeCount = SaveStagingList(node, "RECORDING_SUPERSEDES", RecordingSupersedes, (item, parent) => item.SaveInto(parent));

            int retirementCount = SaveStagingList(node, "RECORDING_REWIND_RETIREMENTS", RecordingRewindRetirements, (item, parent) => item.SaveInto(parent));

            int tombCount = SaveStagingList(node, "LEDGER_TOMBSTONES", LedgerTombstones, (item, parent) => item.SaveInto(parent));

            bool markerWritten = false;
            string markerSessionId = null;
            if (ActiveReFlySessionMarker != null)
            {
                ActiveReFlySessionMarker.SaveInto(node);
                markerWritten = true;
                markerSessionId = ActiveReFlySessionMarker.SessionId;
            }

            bool journalWritten = false;
            string journalId = null;
            if (ActiveMergeJournal != null)
            {
                ActiveMergeJournal.SaveInto(node);
                journalWritten = true;
                journalId = ActiveMergeJournal.JournalId;
            }

            bool intentWritten = false;
            string intentId = null;
            if (activeStockActionIntent != null)
            {
                activeStockActionIntent.SaveInto(node);
                intentWritten = true;
                intentId = activeStockActionIntent.IntentId.ToString("D");
            }

            bool segmentWritten = false;
            string segmentSessionId = null;
            if (activeSwitchSegmentSession != null)
            {
                activeSwitchSegmentSession.SaveInto(node);
                segmentWritten = true;
                segmentSessionId = activeSwitchSegmentSession.SessionId.ToString("D");
            }

            bool testBatchWritten = false;
            if (ActiveTestBatchMarker != null)
            {
                ActiveTestBatchMarker.SaveInto(node);
                testBatchWritten = true;
            }
            ParsekLog.Info("TestBatch",
                $"marker saved: {(testBatchWritten ? (ActiveTestBatchMarker.ProcessSessionId ?? "<no-pid>") : "none")}");

            // Per-section tagged lines (design §10 tag conventions). Emitted
            // alongside the consolidated summary below so log-grep by tag still
            // works even when the summary line changes shape.
            ParsekLog.Info("Rewind", $"RewindPoints saved: {rpCount}");
            ParsekLog.Info("Supersede", $"RecordingSupersedes saved: {supersedeCount}");
            ParsekLog.Info("Rewind", $"RecordingRewindRetirements saved: {retirementCount}");
            ParsekLog.Info("LedgerSwap", $"LedgerTombstones saved: {tombCount}");
            ParsekLog.Info("ReFlySession",
                $"Marker saved: {(markerWritten ? (markerSessionId ?? "<no-id>") : "none")}");
            ParsekLog.Info("MergeJournal",
                $"Journal saved: {(journalWritten ? (journalId ?? "<no-id>") : "none")}");
            ParsekLog.Info("SwitchIntent",
                $"intent saved: {(intentWritten ? (intentId ?? "<no-id>") : "none")}");
            ParsekLog.Info("SwitchSegment",
                $"session saved: {(segmentWritten ? (segmentSessionId ?? "<no-id>") : "none")}");

            ParsekLog.Info("Scenario",
                $"OnSave: Re-Fly subsystem state persisted (saved collections, not a rewind action; " +
                $"reFlyInProgress={markerWritten}): rewindPoints={rpCount} supersedes={supersedeCount} " +
                $"rewindRetirements={retirementCount} " +
                $"tombstones={tombCount} marker={markerWritten} journal={journalWritten} " +
                $"switchIntent={intentWritten} switchSegment={segmentWritten}");
        }

        private static int SaveStagingList<T>(ConfigNode node, string nodeName, IReadOnlyList<T> list, Action<T, ConfigNode> save)
        {
            int count = 0;
            if (list != null && list.Count > 0)
            {
                var parent = node.AddNode(nodeName);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null) continue;
                    save(list[i], parent);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Restores the Rewind-to-Staging Phase 1 collections and singletons.
        /// Missing parent nodes yield empty lists and null singletons so
        /// pre-feature saves round-trip cleanly (design section 9).
        /// </summary>
        private void LoadRewindStagingState(ConfigNode node)
        {
            RewindPoints = new List<RewindPoint>();
            RecordingSupersedes = new List<RecordingSupersedeRelation>();
            RecordingRewindRetirements = new List<RecordingRewindRetirement>();
            LedgerTombstones = new List<LedgerTombstone>();
            ActiveReFlySessionMarker = null;
            ActiveMergeJournal = null;
            ActiveTestBatchMarker = null;
            // Phase 2 (ghost rendering pipeline): drop any prior session's
            // anchor ε map. If the just-loaded save carries a marker the
            // post-load Rebuild below repopulates; if it does not, this
            // Clear keeps stale anchors from a previous game leaking into
            // the new load (HR-9 visibility).
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            // Switch-segment auto-record statics are loaded fresh below from
            // their dedicated nodes; null here so a pre-feature save (no
            // serialized markers) leaves both fields cleared.
            activeStockActionIntent = null;
            activeSwitchSegmentSession = null;

            LoadStagingList(node, "REWIND_POINTS", "POINT", RewindPoints, RewindPoint.LoadFrom);
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();

            LoadStagingList(node, "RECORDING_SUPERSEDES", "ENTRY", RecordingSupersedes, RecordingSupersedeRelation.LoadFrom);

            LoadStagingList(node, "RECORDING_REWIND_RETIREMENTS", "ENTRY", RecordingRewindRetirements, RecordingRewindRetirement.LoadFrom);

            LoadStagingList(node, "LEDGER_TOMBSTONES", "ENTRY", LedgerTombstones, LedgerTombstone.LoadFrom);

            ConfigNode markerNode = node.GetNode(ReFlySessionMarker.NodeName);
            if (markerNode != null)
                ActiveReFlySessionMarker = ReFlySessionMarker.LoadFrom(markerNode);

            // Phase 2 (ghost rendering pipeline, design doc §17.2 / §18 Phase
            // 2): rebuild the in-memory anchor ε map from the just-loaded
            // marker so the OnLoad path matches the post-AtomicMarkerWrite
            // path in RewindInvoker.ConsumePostLoad. HR-9 visible-failure —
            // a throw inside the rebuild must not abort save load.
            if (ActiveReFlySessionMarker != null)
            {
                try
                {
                    Parsek.Rendering.RenderSessionState.RebuildFromMarker(ActiveReFlySessionMarker);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Pipeline-Session",
                        $"OnLoad RebuildFromMarker threw (non-fatal): {ex.Message}");
                }
            }

            // Re-fly Esc-menu button gate: covers the case where a save was
            // made mid-re-fly and is being loaded fresh. If the load lands in
            // a non-flight scene the call is a logged no-op; the next
            // onFlightReady (handled by ReFlyRevertButtonGate.Subscribe) will
            // re-assert when the player enters flight. If the load lands in
            // flight (rare — KSP normally loads to KSC first), this is the
            // immediate force.
            ReFlyRevertButtonGate.Apply("OnLoad:after-marker-load");

            ConfigNode journalNode = node.GetNode(MergeJournal.NodeName);
            if (journalNode != null)
                ActiveMergeJournal = MergeJournal.LoadFrom(journalNode);

            // Switch-segment auto-record markers (Phase A.3 wiring; consume
            // sites land in Phase C). The intent marker carries TTL + UT +
            // ProcessSessionId so we validate freshness here at load time and
            // clear stale-on-OnLoad rather than at consume site — that way
            // the player isn't surprised by a delayed auto-start after they
            // click around in TS.
            ConfigNode intentNode = node.GetNode(StockActionIntentMarker.NodeName);
            StockActionIntentMarker loadedIntent;
            if (intentNode != null && StockActionIntentMarker.TryLoadFrom(intentNode, out loadedIntent))
            {
                activeStockActionIntent = loadedIntent;
                ValidateLoadedStockActionIntentFreshness();
            }

            ConfigNode segmentNode = node.GetNode(SwitchSegmentSession.NodeName);
            SwitchSegmentSession loadedSession;
            if (segmentNode != null && SwitchSegmentSession.TryLoadFrom(segmentNode, out loadedSession))
            {
                activeSwitchSegmentSession = loadedSession;
                ParsekLog.Info("SwitchSegment",
                    $"session loaded: sessionId={loadedSession.SessionId:D} " +
                    $"intentId={loadedSession.IntentId:D} entryReason={loadedSession.EntryReason}");
            }

            ConfigNode testBatchNode = node.GetNode(TestBatchMarker.NodeName);
            if (testBatchNode != null)
                ActiveTestBatchMarker = TestBatchMarker.LoadFrom(testBatchNode);
            ParsekLog.Info("TestBatch",
                $"marker loaded: {(ActiveTestBatchMarker != null ? (ActiveTestBatchMarker.ProcessSessionId ?? "<no-pid>") : "none")}");

            // Per-section tagged lines (design §10 tag conventions). Emitted
            // alongside the consolidated summary below so log-grep by tag still
            // works even when the summary line changes shape.
            ParsekLog.Info("Rewind", $"RewindPoints loaded: {RewindPoints.Count}");
            ParsekLog.Info("Supersede",
                $"RecordingSupersedes loaded: {RecordingSupersedes.Count}");
            ParsekLog.Info("Rewind",
                $"RecordingRewindRetirements loaded: {RecordingRewindRetirements.Count}");
            ParsekLog.Info("LedgerSwap",
                $"LedgerTombstones loaded: {LedgerTombstones.Count}");
            ParsekLog.Info("ReFlySession",
                $"Marker loaded: {(ActiveReFlySessionMarker != null ? (ActiveReFlySessionMarker.SessionId ?? "<no-id>") : "none")}");
            ParsekLog.Info("MergeJournal",
                $"Journal loaded: {(ActiveMergeJournal != null ? (ActiveMergeJournal.JournalId ?? "<no-id>") : "none")}");
            ParsekLog.Info("SwitchIntent",
                $"intent loaded: {(activeStockActionIntent != null ? activeStockActionIntent.IntentId.ToString("D") : "none")}");
            ParsekLog.Info("SwitchSegment",
                $"session loaded summary: {(activeSwitchSegmentSession != null ? activeSwitchSegmentSession.SessionId.ToString("D") : "none")}");

            ParsekLog.Info("Scenario",
                $"OnLoad: Re-Fly subsystem state loaded (restored collections, not a rewind action; " +
                $"reFlyInProgress={(ActiveReFlySessionMarker != null)}): rewindPoints={RewindPoints.Count} " +
                $"supersedes={RecordingSupersedes.Count} rewindRetirements={RecordingRewindRetirements.Count} " +
                $"tombstones={LedgerTombstones.Count} " +
                $"marker={(ActiveReFlySessionMarker != null)} journal={(ActiveMergeJournal != null)} " +
                $"switchIntent={(activeStockActionIntent != null)} switchSegment={(activeSwitchSegmentSession != null)}");

            // Phase 2: a new load invalidates every derived cache. Bump both
            // counters so <see cref="EffectiveState.ComputeERS"/> and
            // <see cref="EffectiveState.ComputeELS"/> recompute on next access.
            BumpSupersedeStateVersion();
            BumpTombstoneStateVersion();
        }

        /// <summary>
        /// Loads a staging collection from a named parent node by appending each
        /// child node (matched by <paramref name="childName"/>) through
        /// <paramref name="load"/> into <paramref name="target"/>. Collapses the
        /// four parallel REWIND_POINTS / RECORDING_SUPERSEDES /
        /// RECORDING_REWIND_RETIREMENTS / LEDGER_TOMBSTONES load blocks in
        /// <see cref="LoadRewindStagingState"/>; the child name differs ("POINT"
        /// vs "ENTRY") so it is a parameter.
        /// </summary>
        private static void LoadStagingList<T>(ConfigNode node, string parentName, string childName, List<T> target, Func<ConfigNode, T> load)
        {
            ConfigNode parent = node.GetNode(parentName);
            if (parent != null)
            {
                var entries = parent.GetNodes(childName);
                for (int i = 0; i < entries.Length; i++)
                    target.Add(load(entries[i]));
            }
        }
    }
}
