using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Revert epochs and the revert undo (design-vessel-and-mission-names.md 7.6, ruling 5):
    /// a stock rename made inside a flight the player then reverts is cancelled with it, a
    /// Parsek rename always survives, and rewinds / F9 keep every rename.
    ///
    /// <para>A flight epoch is the span the stock Revert button would cancel. Stock rebuilds
    /// <c>FlightDriver.PostInitState</c> in <c>FlightDriver.PostInit</c> on every flight
    /// start, just before <c>onFlightReady</c>, and both revert buttons go back to it (or to
    /// the pre-launch state of that same flight), so the epoch moves exactly when that
    /// object changes (<see cref="NoteFlightReady"/>). A Re-Fly attempt start moves it too:
    /// Discard Re-fly and Retry cancel the attempt the way a revert cancels a flight.</para>
    ///
    /// <para>Every decision pushes an undo entry (full copy of the decision it replaced, or
    /// none, plus the added decision's seq) onto its epoch. Only the current epoch can be
    /// cancelled, so a bump drops the older entries. Memory only: a revert never crosses a
    /// cold load.</para>
    /// </summary>
    internal static partial class VesselNameStore
    {
        /// <summary>One decision recorded during a flight epoch, as the undo needs it.</summary>
        internal sealed class UndoEntry
        {
            internal int Epoch;
            internal VesselNameSource Source;
            internal long AddedSeq;
            internal string AddedName;
            /// <summary>The decision the added one replaced, as it was before (null: none).</summary>
            internal VesselNameDecision ReplacedCopy;
        }

        /// <summary>What one undo did, for its summary line and the tests.</summary>
        internal struct UndoResult
        {
            internal int Entries;
            internal int Undone;
            internal int KeptParsekRename;
            internal int KeptReplacedLater;
            internal int LegsRestored;
        }

        private static readonly List<UndoEntry> undoEntries = new List<UndoEntry>();
        private static object lastPostInitState;

        /// <summary>The current flight epoch (memory only; starts at 0 each session).</summary>
        internal static int FlightEpoch { get; private set; }

        internal static IReadOnlyList<UndoEntry> UndoEntries => undoEntries;

        /// <summary>
        /// The epoch moves when stock's revert target is a different object than at the
        /// previous <c>onFlightReady</c>. A null target never moves it. Pure.
        /// </summary>
        internal static bool ShouldBumpFlightEpoch(object lastPostInitState, object currentPostInitState)
            => currentPostInitState != null && !ReferenceEquals(lastPostInitState, currentPostInitState);

        /// <summary>
        /// <c>onFlightReady</c> hook: bumps the epoch when <paramref name="postInitState"/>
        /// (<c>FlightDriver.PostInitState</c>) changed. True when it bumped.
        /// </summary>
        internal static bool NoteFlightReady(object postInitState)
        {
            if (!ShouldBumpFlightEpoch(lastPostInitState, postInitState))
            {
                ParsekLog.Verbose(Tag, "Flight epoch kept at " + FlightEpoch.ToString(CultureInfo.InvariantCulture) +
                    " on flight ready (" + (postInitState == null ? "no revert target" : "same revert target") + ")");
                return false;
            }
            lastPostInitState = postInitState;
            BumpFlightEpoch("flight-ready:new-revert-target");
            return true;
        }

        /// <summary>Starts a new flight epoch and drops the undo entries of every older one.</summary>
        internal static void BumpFlightEpoch(string reason)
        {
            unchecked { FlightEpoch++; }
            int dropped = undoEntries.RemoveAll(e => e.Epoch != FlightEpoch);
            ParsekLog.Info(Tag, "Flight epoch -> " + FlightEpoch.ToString(CultureInfo.InvariantCulture) +
                " reason=" + (reason ?? "<none>") + " droppedUndoEntries=" + dropped.ToString(CultureInfo.InvariantCulture));
        }

        private static void PushUndoEntry(VesselNameSource source, VesselNameDecision added, VesselNameDecision replacedCopy)
        {
            undoEntries.Add(new UndoEntry
            {
                Epoch = FlightEpoch,
                Source = source,
                AddedSeq = added.Seq,
                AddedName = added.Name,
                ReplacedCopy = replacedCopy,
            });
        }

        /// <summary>A deep copy of a decision (every set and list copied).</summary>
        internal static VesselNameDecision CloneDecision(VesselNameDecision d)
        {
            if (d == null) return null;
            var c = new VesselNameDecision
            {
                Seq = d.Seq,
                Name = d.Name,
                Source = d.Source,
                DecidedUT = d.DecidedUT,
                FlightEpoch = d.FlightEpoch,
            };
            c.Guids.AddRange(d.Guids);
            c.PriorNames.AddRange(d.PriorNames);
            foreach (string id in d.CoveredLegIds) c.CoveredLegIds.Add(id);
            foreach (string id in d.UncoveredLegIds) c.UncoveredLegIds.Add(id);
            foreach (KeyValuePair<string, string> kv in d.CapturedNames) c.CapturedNames[kv.Key] = kv.Value;
            return c;
        }

        private static VesselNameDecision FindBySeq(long seq)
        {
            for (int i = 0; i < decisions.Count; i++)
                if (decisions[i].Seq == seq) return decisions[i];
            return null;
        }

        private static void RemoveDecision(VesselNameDecision d)
        {
            decisions.Remove(d);
            var stale = new List<string>();
            foreach (KeyValuePair<string, VesselNameDecision> kv in byGuid)
                if (ReferenceEquals(kv.Value, d)) stale.Add(kv.Key);
            foreach (string g in stale) byGuid.Remove(g);
        }

        /// <summary>
        /// Undoes the current epoch (a stock revert, Discard Re-fly or a Re-Fly Retry cancelled
        /// it) over the live recordings. Never throws.
        /// </summary>
        internal static UndoResult UndoCurrentFlightEpoch(string reason)
        {
            try
            {
                VesselIdentity.Resolution res = null;
                if (HasCurrentEpochStockEntries())
                {
                    List<RecordingTree> trees = GatherTrees();
                    res = ResolveUniverse(trees, GatherLoose(trees));
                }
                return UndoEpoch(FlightEpoch, res, reason);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Revert undo (" + (reason ?? "<none>") + ") threw: " +
                    ex.GetType().Name + ": " + ex.Message);
                return default;
            }
        }

        private static bool HasCurrentEpochStockEntries()
        {
            for (int i = 0; i < undoEntries.Count; i++)
                if (undoEntries[i].Epoch == FlightEpoch && undoEntries[i].Source != VesselNameSource.ParsekRename)
                    return true;
            return false;
        }

        /// <summary>
        /// Walks <paramref name="epoch"/>'s entries newest first. A ParsekRename entry stays. A
        /// stock entry whose added decision is no longer current (a later decision replaced it,
        /// a ParsekRename among them) stays too. Otherwise the added decision is removed and the
        /// decision it replaced comes back exactly as it was (coverage sets included); a guid
        /// another current decision holds now stays with that one. The legs the removed decision
        /// had renamed get their own captured name back, then the apply pass over
        /// <paramref name="res"/> (null: no recordings to touch) re-applies what is current.
        /// The epoch's entries are consumed.
        /// </summary>
        internal static UndoResult UndoEpoch(int epoch, VesselIdentity.Resolution res, string reason)
        {
            var result = new UndoResult();
            var legRestores = new List<KeyValuePair<string, KeyValuePair<string, string>>>();
            for (int i = undoEntries.Count - 1; i >= 0; i--)
            {
                UndoEntry e = undoEntries[i];
                if (e.Epoch != epoch) continue;
                result.Entries++;
                if (e.Source == VesselNameSource.ParsekRename)
                {
                    result.KeptParsekRename++;
                    continue;
                }
                VesselNameDecision added = FindBySeq(e.AddedSeq);
                if (added == null)
                {
                    result.KeptReplacedLater++;
                    ParsekLog.Info(Tag, "Revert undo kept: stock decision seq=" +
                        e.AddedSeq.ToString(CultureInfo.InvariantCulture) + " '" + (e.AddedName ?? "") +
                        "' is no longer current (a later decision replaced it)");
                    continue;
                }

                RemoveDecision(added);
                foreach (string legId in added.CoveredLegIds)
                    if (added.CapturedNames.TryGetValue(legId, out string captured) && !string.IsNullOrEmpty(captured))
                        legRestores.Add(new KeyValuePair<string, KeyValuePair<string, string>>(
                            legId, new KeyValuePair<string, string>(captured, added.Name)));

                string restoredDesc = "none";
                if (e.ReplacedCopy != null)
                {
                    VesselNameDecision remainder = FindBySeq(e.ReplacedCopy.Seq);
                    if (remainder != null) RemoveDecision(remainder);
                    VesselNameDecision restored = CloneDecision(e.ReplacedCopy);
                    int taken = 0;
                    for (int g = restored.Guids.Count - 1; g >= 0; g--)
                    {
                        string guid = restored.Guids[g];
                        if (byGuid.TryGetValue(guid, out VesselNameDecision holder) && !ReferenceEquals(holder, restored))
                        {
                            restored.Guids.RemoveAt(g);
                            taken++;
                        }
                    }
                    if (restored.Guids.Count > 0)
                    {
                        decisions.Add(restored);
                        foreach (string guid in restored.Guids) byGuid[guid] = restored;
                    }
                    restoredDesc = "seq=" + restored.Seq.ToString(CultureInfo.InvariantCulture) + " '" + restored.Name +
                        "' guids=" + restored.Guids.Count.ToString(CultureInfo.InvariantCulture) +
                        (taken > 0 ? " heldElsewhere=" + taken.ToString(CultureInfo.InvariantCulture) : "");
                }
                result.Undone++;
                ParsekLog.Info(Tag, "Revert undo: dropped " + added.Source + " decision seq=" +
                    added.Seq.ToString(CultureInfo.InvariantCulture) + " '" + added.Name + "', restored " + restoredDesc);
            }
            undoEntries.RemoveAll(x => x.Epoch == epoch);
            if (result.Undone > 0)
            {
                decisions.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                Version++;
                if (res != null)
                {
                    foreach (KeyValuePair<string, KeyValuePair<string, string>> r in legRestores)
                    {
                        List<Recording> copies = res.Copies(r.Key);
                        if (copies == null) continue;
                        foreach (Recording copy in copies)
                        {
                            if (copy == null || !string.Equals(copy.VesselName, r.Value.Value, StringComparison.Ordinal))
                                continue;
                            copy.VesselName = r.Value.Key;
                            result.LegsRestored++;
                        }
                    }
                    ApplyTo(res, "revert-undo");
                }
                DisplayNames.NoteChanged("vessel-names-revert-undo", reason);
            }
            ParsekLog.Info(Tag, "Revert undo (" + (reason ?? "<none>") + "): epoch=" +
                epoch.ToString(CultureInfo.InvariantCulture) +
                " entries=" + result.Entries.ToString(CultureInfo.InvariantCulture) +
                " undone=" + result.Undone.ToString(CultureInfo.InvariantCulture) +
                " keptParsekRename=" + result.KeptParsekRename.ToString(CultureInfo.InvariantCulture) +
                " keptReplacedLater=" + result.KeptReplacedLater.ToString(CultureInfo.InvariantCulture) +
                " legsRestored=" + result.LegsRestored.ToString(CultureInfo.InvariantCulture) +
                " decisions=" + decisions.Count.ToString(CultureInfo.InvariantCulture));
            return result;
        }

        /// <summary>
        /// The live write rule found the open current leg's vessel under a name nobody chose
        /// (stock's automatic rename after the leg opened): the leg is stock-named, not covered.
        /// </summary>
        internal static void MarkLegUncoveredByStockName(VesselNameDecision d, string legId)
        {
            if (d == null || string.IsNullOrEmpty(legId)) return;
            d.CoveredLegIds.Remove(legId);
            d.CapturedNames.Remove(legId);
            d.UncoveredLegIds.Add(legId);
            Version++;
        }

        private static void ClearRevertUndoState(bool resetEpoch)
        {
            undoEntries.Clear();
            if (!resetEpoch) return;
            FlightEpoch = 0;
            lastPostInitState = null;
        }
    }
}
