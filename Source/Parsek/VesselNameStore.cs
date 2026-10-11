// [ERS-exempt] VesselNameStore applies a vessel's player-chosen name to every recording
// leg of that physical vessel, superseded legs included (a decision names the ship, whatever
// the timeline; design-vessel-and-mission-names.md 6.2), so it reads the raw committed list
// for the recordings no tree holds.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>Who made a vessel name decision. Persisted by name; values are stable.</summary>
    internal enum VesselNameSource
    {
        StockDialog = 0,
        StockPartNaming = 1,
        ParsekRename = 2,
    }

    /// <summary>
    /// One player decision naming one vessel identity (design 6.1). <see cref="Seq"/> is the
    /// order of player choices (never game UT, which moves backwards on rewind and F9).
    /// Coverage: the legs that take <see cref="Name"/> and the later legs stock named
    /// automatically, each classified once by id.
    /// </summary>
    internal sealed class VesselNameDecision
    {
        internal long Seq;
        internal readonly List<string> Guids = new List<string>();
        internal string Name;
        internal readonly List<string> PriorNames = new List<string>();
        internal readonly HashSet<string> CoveredLegIds = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> UncoveredLegIds = new HashSet<string>(StringComparer.Ordinal);
        internal VesselNameSource Source;
        /// <summary>Diagnostics only.</summary>
        internal double DecidedUT;

        internal bool IsClassified(string legId)
            => legId != null && (CoveredLegIds.Contains(legId) || UncoveredLegIds.Contains(legId));

        /// <summary>
        /// The coverage rule for a leg the decision has not seen yet: its own captured name is
        /// the decision name or one the decision replaced (it continues the vessel the player
        /// named); any other name is stock's automatic naming (ruling 4). Raw ordinal compare.
        /// </summary>
        internal bool CoversCapturedName(string capturedName)
        {
            if (string.IsNullOrEmpty(capturedName)) return false;
            if (string.Equals(capturedName, Name, StringComparison.Ordinal)) return true;
            for (int i = 0; i < PriorNames.Count; i++)
                if (string.Equals(capturedName, PriorNames[i], StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// The one authority for a vessel identity's current name (design-vessel-and-mission-names.md
    /// 6.1): player decisions keyed by every launch guid the identity carried when the decision
    /// was made. <see cref="Recording.VesselName"/> is a derived copy the apply pass
    /// (<see cref="ApplyTo"/>) re-writes on every covered leg. Static memory like
    /// <see cref="RecordingStore"/>: a cold load reads the save's <c>VESSEL_NAMES</c> node,
    /// every in-session load keeps memory (<c>LoadReconcilePolicy</c> category
    /// <see cref="LoadStateCategory.VesselNames"/>).
    ///
    /// <para>Phase 1 scope: Parsek row renames only (no stock capture, no write-through to
    /// a live vessel, no link rows, no revert undo).</para>
    /// </summary>
    internal static class VesselNameStore
    {
        internal const string Tag = "VesselNames";
        internal const string NodeName = "VESSEL_NAMES";
        internal const string DecisionNodeName = "DECISION";

        internal const string RefusalBlank = "blank";
        internal const string RefusalUnchanged = "unchanged";
        internal const string RefusalEva = "eva";
        internal const string RefusalNoGuid = "no-guid";
        internal const string RefusalNoRecording = "no-recording";

        private static readonly List<VesselNameDecision> decisions = new List<VesselNameDecision>();
        private static readonly Dictionary<string, VesselNameDecision> byGuid =
            new Dictionary<string, VesselNameDecision>(StringComparer.OrdinalIgnoreCase);
        private static long nextSeq = 1;

        // Self-heal bookkeeping: the inputs the last apply pass saw.
        private static int lastAppliedStateVersion = int.MinValue;
        private static int lastLiveTreeSignature;
        private static float nextLiveTreeCheckRealtime;

        /// <summary>Moves on every store change (decision added / replaced / merged, a leg classified).</summary>
        internal static int Version { get; private set; }

        internal static IReadOnlyList<VesselNameDecision> Decisions => decisions;
        internal static long NextSeq => nextSeq;
        internal static bool HasDecisions => decisions.Count > 0;

        /// <summary>Test seam: the trees an apply pass resolves over (committed + pending + live in production).</summary>
        internal static Func<IEnumerable<RecordingTree>> TreesProviderForTesting;

        /// <summary>Test seam: the loose (tree-less) committed recordings.</summary>
        internal static Func<IEnumerable<Recording>> LooseRecordingsProviderForTesting;

        /// <summary>Test seam for the decision UT (diagnostics only).</summary>
        internal static Func<double> NowUtProviderForTesting;

        /// <summary>The decision indexed under <paramref name="guid"/>, or null.</summary>
        internal static VesselNameDecision FindByGuid(string guid)
        {
            string g = VesselLaunchIdentity.NormalizeGuid(guid);
            return g != null && byGuid.TryGetValue(g, out VesselNameDecision d) ? d : null;
        }

        // ------------------------------------------------------------------ resolution

        /// <summary>
        /// The decision naming <paramref name="identity"/>: every guid of the identity looked up
        /// in the guid index. When two components carrying separate decisions are now one
        /// identity, the highest <see cref="VesselNameDecision.Seq"/> wins and the loser is folded
        /// into it (its guids, its covered / uncovered legs and its names become the winner's
        /// guids, legs and prior names), with a Warn naming both.
        /// </summary>
        internal static VesselNameDecision ResolveDecision(VesselIdentity.Identity identity)
        {
            if (identity == null || identity.Guids.Count == 0 || decisions.Count == 0) return null;
            VesselNameDecision winner = null;
            List<VesselNameDecision> found = null;
            foreach (string g in identity.Guids)
            {
                if (!byGuid.TryGetValue(g, out VesselNameDecision d)) continue;
                if (found == null) found = new List<VesselNameDecision>(1);
                if (!found.Contains(d)) found.Add(d);
                if (winner == null || d.Seq > winner.Seq) winner = d;
            }
            if (found == null || found.Count < 2) return winner;

            foreach (VesselNameDecision loser in found)
            {
                if (ReferenceEquals(loser, winner)) continue;
                ParsekLog.Warn(Tag,
                    "Conflicting vessel name decisions joined in one identity=" + (identity.Key ?? "<none>") +
                    ": keeping seq=" + winner.Seq.ToString(CultureInfo.InvariantCulture) + " '" + winner.Name +
                    "', dropping seq=" + loser.Seq.ToString(CultureInfo.InvariantCulture) + " '" + loser.Name + "'");
                FoldInto(winner, loser);
            }
            Version++;
            return winner;
        }

        private static void FoldInto(VesselNameDecision winner, VesselNameDecision loser)
        {
            foreach (string g in loser.Guids)
            {
                if (!ContainsIgnoreCase(winner.Guids, g)) winner.Guids.Add(g);
                byGuid[g] = winner;
            }
            foreach (string id in loser.CoveredLegIds)
                if (!winner.UncoveredLegIds.Contains(id)) winner.CoveredLegIds.Add(id);
            foreach (string id in loser.UncoveredLegIds)
                if (!winner.CoveredLegIds.Contains(id)) winner.UncoveredLegIds.Add(id);
            AddPrior(winner, loser.Name);
            foreach (string p in loser.PriorNames) AddPrior(winner, p);
            decisions.Remove(loser);
        }

        private static void AddPrior(VesselNameDecision d, string name)
        {
            if (string.IsNullOrEmpty(name) || string.Equals(name, d.Name, StringComparison.Ordinal)) return;
            for (int i = 0; i < d.PriorNames.Count; i++)
                if (string.Equals(d.PriorNames[i], name, StringComparison.Ordinal)) return;
            d.PriorNames.Add(name);
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ------------------------------------------------------------------ decisions

        /// <summary>
        /// Records a player decision naming the identity of <paramref name="rec"/> in
        /// <paramref name="res"/>. Refused (nothing written, <paramref name="refusal"/> set) for
        /// a blank name, a name the row already shows, an EVA row (ruling 6) and an identity with
        /// no known launch guid (legacy recordings: keeps their name fallbacks valid). On success
        /// every leg the identity has now is covered (ruling 7: the launch leg too), the names
        /// its legs carried and the replaced decision's names become prior names, the replaced
        /// decision is removed, and the new one is indexed under every identity guid.
        /// </summary>
        internal static bool TryRecordDecision(
            VesselIdentity.Resolution res, Recording rec, string name, VesselNameSource source,
            double ut, out VesselNameDecision decision, out string refusal)
        {
            decision = null;
            refusal = null;
            string trimmed = name?.Trim();
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) refusal = RefusalNoRecording;
            else if (string.IsNullOrEmpty(trimmed)) refusal = RefusalBlank;
            else if (!string.IsNullOrEmpty(rec.EvaCrewName)) refusal = RefusalEva;
            else if (string.Equals(trimmed, rec.VesselName, StringComparison.Ordinal)) refusal = RefusalUnchanged;
            if (refusal != null) return false;

            if (res == null || !res.TryGetIdentity(rec.RecordingId, out VesselIdentity.Identity identity))
            {
                refusal = RefusalNoRecording;
                return false;
            }
            if (!identity.HasGuid)
            {
                refusal = RefusalNoGuid;
                return false;
            }

            VesselNameDecision replaced = ResolveDecision(identity);
            var d = new VesselNameDecision
            {
                Seq = nextSeq++,
                Name = trimmed,
                Source = source,
                DecidedUT = ut,
            };
            var guids = new List<string>(identity.Guids);
            guids.Sort(StringComparer.OrdinalIgnoreCase);
            d.Guids.AddRange(guids);
            var priors = new List<string>();
            foreach (string legId in identity.LegIds)
            {
                d.CoveredLegIds.Add(legId);
                List<Recording> copies = res.Copies(legId);
                if (copies == null) continue;
                foreach (Recording copy in copies)
                    if (copy != null && !string.IsNullOrEmpty(copy.VesselName)) priors.Add(copy.VesselName);
            }
            if (replaced != null)
            {
                priors.Add(replaced.Name);
                priors.AddRange(replaced.PriorNames);
            }
            priors.Sort(StringComparer.Ordinal);
            foreach (string p in priors) AddPrior(d, p);

            int keptGuids = 0;
            if (replaced != null)
            {
                // The new decision takes only THIS identity's guids. A guid of the replaced
                // decision the identity no longer carries (two components it once saw joined
                // that are apart now) keeps the replaced decision: renaming one vessel never
                // erases another's name.
                var remaining = new List<string>();
                foreach (string g in replaced.Guids)
                {
                    bool mine = identity.Guids.Contains(g);
                    if (byGuid.TryGetValue(g, out VesselNameDecision cur) && ReferenceEquals(cur, replaced))
                    {
                        if (mine) byGuid.Remove(g);
                        else remaining.Add(g);
                    }
                }
                replaced.Guids.Clear();
                replaced.Guids.AddRange(remaining);
                keptGuids = remaining.Count;
                if (keptGuids == 0)
                    decisions.Remove(replaced);
                else
                    ParsekLog.Info(Tag,
                        "Replaced decision seq=" + replaced.Seq.ToString(CultureInfo.InvariantCulture) +
                        " '" + replaced.Name + "' kept for " + keptGuids.ToString(CultureInfo.InvariantCulture) +
                        " guid(s) outside identity=" + (identity.Key ?? "<none>"));
            }
            decisions.Add(d);
            foreach (string g in d.Guids) byGuid[g] = d;
            Version++;

            ParsekLog.Info(Tag,
                "Vessel name decision recorded: identity=" + (identity.Key ?? "<none>") +
                " '" + (rec.VesselName ?? "") + "' -> '" + trimmed + "' source=" + source +
                " seq=" + d.Seq.ToString(CultureInfo.InvariantCulture) +
                " legs=" + identity.LegIds.Count.ToString(CultureInfo.InvariantCulture) +
                " guids=" + d.Guids.Count.ToString(CultureInfo.InvariantCulture) +
                " priors=" + d.PriorNames.Count.ToString(CultureInfo.InvariantCulture) +
                " replacedSeq=" + (replaced != null ? replaced.Seq.ToString(CultureInfo.InvariantCulture) : "none") +
                " rec=" + rec.RecordingId +
                " ut=" + ut.ToString("F1", CultureInfo.InvariantCulture));
            DisplayNames.NoteChanged("vessel-name-decision", rec.RecordingId);
            decision = d;
            return true;
        }

        /// <summary>
        /// A Parsek row rename (design 7.2; Recordings tab commit and seam
        /// <c>op=edit field=recordingname</c>): resolves the row's identity over the live trees
        /// (or <paramref name="universeForTesting"/>), records a ParsekRename decision and runs
        /// the apply pass. Phase 1 writes no live vessel. False with a logged refusal otherwise.
        /// </summary>
        internal static bool RecordParsekRename(
            Recording rec, string newName, IEnumerable<RecordingTree> universeForTesting, out string refusal)
        {
            List<RecordingTree> trees = universeForTesting != null
                ? new List<RecordingTree>(universeForTesting)
                : GatherTrees();
            List<Recording> loose = universeForTesting != null ? new List<Recording>() : GatherLoose(trees);
            VesselIdentity.Resolution res = VesselIdentity.Resolve(trees, loose);
            if (rec != null && !string.IsNullOrEmpty(rec.RecordingId)
                && !res.ByRecordingId.ContainsKey(rec.RecordingId) && VesselIdentity.IsVesselLeg(rec))
            {
                // A row the resolver's universe does not hold (a recording outside every tree
                // the store knows): it is its own identity.
                loose.Add(rec);
                res = VesselIdentity.Resolve(trees, loose);
            }

            string oldName = rec?.VesselName;
            if (!TryRecordDecision(res, rec, newName, VesselNameSource.ParsekRename, ReadNowUT(),
                    out VesselNameDecision decision, out refusal))
            {
                // Blank and unchanged are the commit of an untouched editor: quiet.
                string line = "Parsek rename refused: rec=" + (rec?.RecordingId ?? "<null>") +
                    " name='" + (newName ?? "") + "' reason=" + refusal;
                if (refusal == RefusalBlank || refusal == RefusalUnchanged)
                    ParsekLog.Verbose(Tag, line);
                else
                    ParsekLog.Info(Tag, line);
                return false;
            }

            res.TryGetIdentity(rec.RecordingId, out VesselIdentity.Identity identity);
            ParsekLog.Info("UI",
                "Recording '" + (oldName ?? "") + "' renamed to '" + decision.Name + "' identity=" +
                (identity?.Key ?? "<none>") + " legs=" +
                (identity != null ? identity.LegIds.Count : 0).ToString(CultureInfo.InvariantCulture));
            ApplyTo(res, "parsek-rename");
            return true;
        }

        // ------------------------------------------------------------------ apply pass

        /// <summary>What one apply pass did, for its summary line and the tests.</summary>
        internal struct ApplyResult
        {
            internal bool Ran;
            internal int Identities;
            internal int IdentitiesWithDecision;
            internal int NewlyCovered;
            internal int NewlyUncovered;
            internal int LegsRenamed;
        }

        /// <summary>
        /// The apply pass over the live recordings (committed trees, pending tree, live active
        /// tree, loose committed recordings). A no-op when the store holds no decision.
        /// </summary>
        internal static ApplyResult ApplyAll(string reason)
        {
            lastAppliedStateVersion = RecordingStore.StateVersion;
            if (decisions.Count == 0) return default;
            try
            {
                lastLiveTreeSignature = ComputeRecordingIdSetSignature(ReadLiveTreeIdsSafe());
                List<RecordingTree> trees = GatherTrees();
                VesselIdentity.Resolution res = VesselIdentity.Resolve(trees, GatherLoose(trees));
                return ApplyTo(res, reason);
            }
            catch (Exception ex)
            {
                // Runs inside OnLoad, CommitTree and the optimizer: a naming fault must never
                // take them down. Names fall back to the captured ones until the next pass.
                ParsekLog.Warn(Tag, "Apply pass (" + (reason ?? "<none>") + ") threw: " +
                    ex.GetType().Name + ": " + ex.Message);
                return default;
            }
        }

        /// <summary>
        /// Apply pass steps 1, 2 and 4 (design 7.4) over <paramref name="res"/>: classify every
        /// not-yet-classified leg of every identity with a decision from its own captured
        /// name (any copy), set every covered leg's <see cref="Recording.VesselName"/> (every
        /// object copy) to the decision name, bump <see cref="DisplayNames.Version"/> once when a
        /// name moved, and log one summary line. Uncovered legs keep their captured names.
        /// </summary>
        internal static ApplyResult ApplyTo(VesselIdentity.Resolution res, string reason)
        {
            var result = new ApplyResult();
            if (res == null || decisions.Count == 0) return result;
            result.Ran = true;
            result.Identities = res.Identities.Count;
            bool storeChanged = false;
            string firstRenamed = null;
            foreach (VesselIdentity.Identity identity in res.Identities)
            {
                VesselNameDecision d = ResolveDecision(identity);
                if (d == null) continue;
                result.IdentitiesWithDecision++;
                foreach (string legId in identity.LegIds)
                {
                    List<Recording> copies = res.Copies(legId);
                    if (copies == null || copies.Count == 0) continue;
                    if (!d.IsClassified(legId))
                    {
                        bool covered = false;
                        foreach (Recording copy in copies)
                            if (copy != null && d.CoversCapturedName(copy.VesselName)) { covered = true; break; }
                        if (covered) { d.CoveredLegIds.Add(legId); result.NewlyCovered++; }
                        else { d.UncoveredLegIds.Add(legId); result.NewlyUncovered++; }
                        storeChanged = true;
                    }
                    if (!d.CoveredLegIds.Contains(legId)) continue;
                    foreach (Recording copy in copies)
                    {
                        if (copy == null || string.Equals(copy.VesselName, d.Name, StringComparison.Ordinal))
                            continue;
                        copy.VesselName = d.Name;
                        result.LegsRenamed++;
                        if (firstRenamed == null) firstRenamed = legId;
                    }
                }
            }
            if (storeChanged) Version++;
            if (result.LegsRenamed > 0)
                DisplayNames.NoteChanged("vessel-names-apply", reason);
            ParsekLog.Verbose(Tag,
                "Apply pass (" + (reason ?? "<none>") + "): identities=" +
                result.Identities.ToString(CultureInfo.InvariantCulture) +
                " withDecision=" + result.IdentitiesWithDecision.ToString(CultureInfo.InvariantCulture) +
                " newlyCovered=" + result.NewlyCovered.ToString(CultureInfo.InvariantCulture) +
                " newlyUncovered=" + result.NewlyUncovered.ToString(CultureInfo.InvariantCulture) +
                " legsRenamed=" + result.LegsRenamed.ToString(CultureInfo.InvariantCulture) +
                (firstRenamed != null ? " firstRenamed=" + firstRenamed : "") +
                " decisions=" + decisions.Count.ToString(CultureInfo.InvariantCulture) +
                " displayVersion=" + DisplayNames.Version.ToString(CultureInfo.InvariantCulture));
            return result;
        }

        // ------------------------------------------------------------------ self-heal

        /// <summary>
        /// Per-frame host hook (design 7.4 "Runs"): re-runs the apply pass when
        /// <see cref="RecordingStore.StateVersion"/> moved since the last pass, or (FLIGHT only,
        /// checked once per real second) the live tree's recording-id set changed - a new
        /// split / EVA / switch leg moves no StateVersion. Inert while the store has no decision.
        /// </summary>
        internal static void SelfHealTick(bool inFlight, float realtime)
        {
            if (decisions.Count == 0) return;
            bool due = RecordingStore.StateVersion != lastAppliedStateVersion;
            string why = "state-version";
            if (!due && inFlight && realtime >= nextLiveTreeCheckRealtime)
            {
                nextLiveTreeCheckRealtime = realtime + 1f;
                int signature = ComputeRecordingIdSetSignature(ReadLiveTreeIdsSafe());
                if (signature != lastLiveTreeSignature)
                {
                    due = true;
                    why = "live-tree-legs";
                }
            }
            if (!due) return;
            try
            {
                ApplyAll("self-heal:" + why);
            }
            catch (Exception ex)
            {
                // Never take a host's Update down; the next change retries.
                lastAppliedStateVersion = RecordingStore.StateVersion;
                ParsekLog.WarnRateLimited(Tag, "self-heal-threw",
                    "Apply pass self-heal threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>An order-free signature of a recording-id set (count included). Pure.</summary>
        internal static int ComputeRecordingIdSetSignature(IEnumerable<string> ids)
        {
            unchecked
            {
                int sum = 0, count = 0;
                if (ids != null)
                    foreach (string id in ids)
                    {
                        if (id == null) continue;
                        int h = StringComparer.Ordinal.GetHashCode(id);
                        sum += (h * 0x2F0B3A49) ^ (h >> 7);
                        count++;
                    }
                return sum * 31 + count;
            }
        }

        // ------------------------------------------------------------------ universe

        internal static List<RecordingTree> GatherTrees()
        {
            var trees = new List<RecordingTree>();
            if (TreesProviderForTesting != null)
            {
                IEnumerable<RecordingTree> provided = TreesProviderForTesting();
                if (provided != null)
                    foreach (RecordingTree t in provided)
                        if (t != null) trees.Add(t);
                return trees;
            }
            foreach (RecordingTree t in RecordingStore.CommittedTrees)
                if (t != null) trees.Add(t);
            if (RecordingStore.PendingTree != null) trees.Add(RecordingStore.PendingTree);
            RecordingTree live = ReadLiveTreeSafe();
            if (live != null && !trees.Contains(live)) trees.Add(live);
            return trees;
        }

        internal static List<Recording> GatherLoose(List<RecordingTree> trees)
        {
            var loose = new List<Recording>();
            if (TreesProviderForTesting != null || LooseRecordingsProviderForTesting != null)
            {
                IEnumerable<Recording> provided = LooseRecordingsProviderForTesting?.Invoke();
                if (provided != null)
                    foreach (Recording r in provided)
                        if (r != null) loose.Add(r);
                return loose;
            }
            var inTrees = new HashSet<Recording>();
            if (trees != null)
                foreach (RecordingTree t in trees)
                    if (t?.Recordings != null)
                        foreach (Recording r in t.Recordings.Values)
                            if (r != null) inTrees.Add(r);
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && !inTrees.Contains(committed[i])) loose.Add(committed[i]);
            return loose;
        }

        private static RecordingTree ReadLiveTreeSafe()
        {
            try
            {
                return ReadLiveTreeCore();
            }
            catch (Exception)
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static RecordingTree ReadLiveTreeCore()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            return object.ReferenceEquals(flight, null) ? null : flight.ActiveTreeForSerialization;
        }

        private static IEnumerable<string> ReadLiveTreeIdsSafe()
        {
            if (TreesProviderForTesting != null) return null;
            RecordingTree live = ReadLiveTreeSafe();
            return live?.Recordings?.Keys;
        }

        private static double ReadNowUT()
        {
            Func<double> provider = NowUtProviderForTesting;
            if (provider != null) return provider();
            try
            {
                return ReadPlanetariumUT();
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadPlanetariumUT()
        {
            return Planetarium.GetUniversalTime();
        }

        // ------------------------------------------------------------------ persistence

        /// <summary>
        /// Writes the <c>VESSEL_NAMES</c> node into the ParsekScenario node (design 6.4): the
        /// next seq and every decision. Nothing is written for a store that never held a
        /// decision. Doubles use "R" + InvariantCulture.
        /// </summary>
        internal static void Save(ConfigNode scenarioNode)
        {
            if (scenarioNode == null) return;
            scenarioNode.RemoveNodes(NodeName);
            if (decisions.Count == 0 && nextSeq <= 1) return;
            scenarioNode.AddNode(EncodeNode());
            ParsekLog.Verbose(Tag,
                "Saved " + NodeName + ": decisions=" + decisions.Count.ToString(CultureInfo.InvariantCulture) +
                " nextSeq=" + nextSeq.ToString(CultureInfo.InvariantCulture));
        }

        internal static ConfigNode EncodeNode()
        {
            var ic = CultureInfo.InvariantCulture;
            var node = new ConfigNode(NodeName);
            node.AddValue("nextSeq", nextSeq.ToString(ic));
            var ordered = new List<VesselNameDecision>(decisions);
            ordered.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            foreach (VesselNameDecision d in ordered)
            {
                ConfigNode dn = node.AddNode(DecisionNodeName);
                dn.AddValue("seq", d.Seq.ToString(ic));
                foreach (string g in d.Guids) dn.AddValue("guid", g);
                dn.AddValue("name", d.Name ?? "");
                foreach (string p in d.PriorNames) dn.AddValue("prior", p);
                foreach (string id in Sorted(d.CoveredLegIds)) dn.AddValue("covered", id);
                foreach (string id in Sorted(d.UncoveredLegIds)) dn.AddValue("uncovered", id);
                dn.AddValue("source", d.Source.ToString());
                dn.AddValue("ut", d.DecidedUT.ToString("R", ic));
            }
            return node;
        }

        private static List<string> Sorted(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>
        /// Cold-path load (design 7.6: Cold = Save). Replaces the store with the save's node;
        /// an absent node is an empty store. A malformed decision is skipped with a Warn; a
        /// guid claimed by two decisions goes to the higher seq.
        /// </summary>
        internal static void Load(ConfigNode scenarioNode)
        {
            ClearState();
            ConfigNode node = scenarioNode?.GetNode(NodeName);
            if (node == null)
            {
                ParsekLog.Verbose(Tag, "Load: no " + NodeName + " node, empty store");
                Version++;
                return;
            }
            int skipped;
            try
            {
                DecodeInto(node, out skipped);
            }
            catch (Exception ex)
            {
                // Design 13: an unreadable node is an empty store; the game continues on the
                // captured names.
                ClearState();
                skipped = -1;
                ParsekLog.Warn(Tag, "Load: " + NodeName + " unreadable, empty store: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            Version++;
            DisplayNames.NoteChanged("vessel-names-load", null);
            ParsekLog.Info(Tag,
                "Loaded " + NodeName + ": decisions=" + decisions.Count.ToString(CultureInfo.InvariantCulture) +
                " skipped=" + skipped.ToString(CultureInfo.InvariantCulture) +
                " nextSeq=" + nextSeq.ToString(CultureInfo.InvariantCulture));
        }

        private static void DecodeInto(ConfigNode node, out int skipped)
        {
            var ic = CultureInfo.InvariantCulture;
            skipped = 0;
            long maxSeq = 0;
            string rawNext = node.GetValue("nextSeq");
            long storedNext = 1;
            if (rawNext != null && !long.TryParse(rawNext, NumberStyles.Integer, ic, out storedNext))
            {
                ParsekLog.Warn(Tag, "Load: unreadable nextSeq '" + rawNext + "', recomputed from the decisions");
                storedNext = 1;
            }
            var loaded = new List<VesselNameDecision>();
            foreach (ConfigNode dn in node.GetNodes(DecisionNodeName))
            {
                string rawSeq = dn.GetValue("seq");
                string name = dn.GetValue("name")?.Trim();
                if (rawSeq == null || !long.TryParse(rawSeq, NumberStyles.Integer, ic, out long seq) || seq <= 0
                    || string.IsNullOrEmpty(name))
                {
                    skipped++;
                    ParsekLog.Warn(Tag, "Load: skipped a malformed DECISION (seq='" + (rawSeq ?? "<null>") +
                        "' name='" + (name ?? "<null>") + "')");
                    continue;
                }
                var d = new VesselNameDecision { Seq = seq, Name = name };
                foreach (string g in dn.GetValues("guid"))
                {
                    string ng = VesselLaunchIdentity.NormalizeGuid(g);
                    if (ng != null && !ContainsIgnoreCase(d.Guids, ng)) d.Guids.Add(ng);
                }
                if (d.Guids.Count == 0)
                {
                    skipped++;
                    ParsekLog.Warn(Tag, "Load: skipped DECISION seq=" + seq.ToString(ic) + " '" + name +
                        "' with no guid");
                    continue;
                }
                foreach (string p in dn.GetValues("prior")) AddPrior(d, p);
                foreach (string id in dn.GetValues("covered")) if (!string.IsNullOrEmpty(id)) d.CoveredLegIds.Add(id);
                foreach (string id in dn.GetValues("uncovered"))
                    if (!string.IsNullOrEmpty(id) && !d.CoveredLegIds.Contains(id)) d.UncoveredLegIds.Add(id);
                string rawSource = dn.GetValue("source");
                if (rawSource != null && Enum.TryParse(rawSource, false, out VesselNameSource source)
                    && Enum.IsDefined(typeof(VesselNameSource), source))
                    d.Source = source;
                else
                    d.Source = VesselNameSource.ParsekRename;
                string rawUt = dn.GetValue("ut");
                if (rawUt != null && double.TryParse(rawUt, NumberStyles.Float, ic, out double ut))
                    d.DecidedUT = ut;
                if (seq > maxSeq) maxSeq = seq;
                loaded.Add(d);
            }
            InstallLoadedDecisions(loaded);
            nextSeq = Math.Max(storedNext, maxSeq + 1);
            if (nextSeq < 1) nextSeq = 1;
        }

        // Highest seq first, so a guid's first claimer always outranks a later one, which is
        // folded into it with a Warn (the same rule as a conflict found at resolution).
        private static void InstallLoadedDecisions(List<VesselNameDecision> loaded)
        {
            loaded.Sort((a, b) => b.Seq.CompareTo(a.Seq));
            foreach (VesselNameDecision d in loaded)
            {
                VesselNameDecision claimer = null;
                foreach (string g in d.Guids)
                    if (byGuid.TryGetValue(g, out claimer)) break;
                if (claimer == null)
                {
                    decisions.Add(d);
                    foreach (string g in d.Guids) byGuid[g] = d;
                    continue;
                }
                ParsekLog.Warn(Tag, "Load: a guid is claimed by two decisions; keeping seq=" +
                    claimer.Seq.ToString(CultureInfo.InvariantCulture) + " '" + claimer.Name +
                    "', folding seq=" + d.Seq.ToString(CultureInfo.InvariantCulture) + " '" + d.Name + "'");
                decisions.Add(d);
                FoldInto(claimer, d);
            }
            decisions.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        }

        private static void ClearState()
        {
            decisions.Clear();
            byGuid.Clear();
            nextSeq = 1;
            lastAppliedStateVersion = int.MinValue;
            lastLiveTreeSignature = 0;
            nextLiveTreeCheckRealtime = 0f;
        }

        /// <summary>Test seam: an empty store and no providers.</summary>
        internal static void ResetForTesting()
        {
            ClearState();
            Version++;
            TreesProviderForTesting = null;
            LooseRecordingsProviderForTesting = null;
            NowUtProviderForTesting = null;
        }
    }
}
