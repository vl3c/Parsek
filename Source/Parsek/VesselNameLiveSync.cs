using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>Which live stock vessels a live write pass walks.</summary>
    internal enum VesselNameLiveScope
    {
        /// <summary>The scene's own vessels: FLIGHT every <c>FlightGlobals.Vessels</c> entry (once
        /// the flight is ready and no Re-Fly strip is pending), Tracking Station / Space Center
        /// the game's proto vessels (through their Vessel object when one exists).</summary>
        Auto,
        /// <summary>The loaded game's proto vessels only (the OnLoad tails: scenario OnLoad runs
        /// before stock builds Vessel objects from them).</summary>
        ProtosOnly,
        /// <summary>Every <c>FlightGlobals.Vessels</c> entry, loaded or not (the post-load pass).</summary>
        FlightVessels,
    }

    /// <summary>One live stock vessel as the live write rule sees it.</summary>
    internal sealed class LiveVesselEntry
    {
        internal string Guid;
        internal uint Pid;
        internal string Name;
        /// <summary>The live tree's leg recording this vessel right now, or null.</summary>
        internal string RecordingLegId;
        internal bool IsGhostMap;
        /// <summary>Performs the stock-equivalent write; true when it wrote.</summary>
        internal Func<string, bool> Write;
    }

    /// <summary>What the live write rule decides for one vessel.</summary>
    internal enum LiveWriteVerdict
    {
        NoDecision,
        NoCurrentLeg,
        /// <summary>The current leg is not classified yet (the apply pass has not seen it).</summary>
        Unclassified,
        /// <summary>The current leg is stock's automatic name (ruling 4): the vessel keeps it.</summary>
        Uncovered,
        AlreadyNamed,
        /// <summary>The live name is stale (the decision, a prior name or the leg's captured name).</summary>
        Write,
        /// <summary>The live name is none of those: a name nobody chose, i.e. a stock automatic
        /// rename after the leg opened (a player rename records a decision first). Never written.</summary>
        UnchosenName,
    }

    /// <summary>Counts of one live write pass.</summary>
    internal struct LiveWriteResult
    {
        internal bool Ran;
        internal int Scanned;
        internal int Written;
        internal int AlreadyNamed;
        internal int Uncovered;
        internal int Unclassified;
        internal int NoCurrentLeg;
        internal int NoIdentity;
        internal int NoDecision;
        internal int SkippedGhost;
        internal int Failed;
        internal int UnchosenName;
        internal int LegsUncoveredByStockName;
    }

    /// <summary>
    /// Vessel names between Parsek and the live stock vessels (design-vessel-and-mission-names.md
    /// 7.1, 7.2, 7.4 step 3, 7.6):
    /// <list type="bullet">
    /// <item>the live write rule: a live vessel whose identity has a decision is renamed to it
    /// when its CURRENT leg (the leg recording it, else the newest leg carrying its launch guid)
    /// is covered; an uncovered current leg means stock named it automatically after the decision,
    /// so it keeps that name (ruling 4);</item>
    /// <item>the stock rename capture core behind <c>VesselRenameCapturePatch</c>;</item>
    /// <item>the post-load pass: once per flight load, after the Re-Fly strip when one consumes
    /// the load (<c>RewindInvoker.RunStripActivateMarker</c>), else on the first host tick after
    /// <c>onFlightReady</c>.</item>
    /// </list>
    /// The decisions (<see cref="ResolveCurrentLegId"/>, <see cref="DecideLiveWrite"/>,
    /// <see cref="ApplyLiveWriteRule"/>, <see cref="RecordStockRename"/>) are pure over a
    /// resolution and <see cref="LiveVesselEntry"/> rows; Unity reads sit in NoInlining cores.
    /// </summary>
    internal static class VesselNameLiveSync
    {
        internal const string Tag = "VesselNames";

        // A Re-Fly strip pass this recent stands for the onFlightReady pass of the same load.
        private const float StripPassCoversFlightReadySeconds = 5f;

        /// <summary>Test seam: the live vessels of a pass (null: production gather, or none when headless).</summary>
        internal static Func<VesselNameLiveScope, List<LiveVesselEntry>> LiveVesselsProviderForTesting;

        /// <summary>Test seam: legs the "newest leg" fallback skips (production: superseded legs).</summary>
        internal static Func<string, bool> ExcludedLegForTesting;

        /// <summary>Test seam for the capture localization (production: <see cref="Recording.ResolveLocalizedName"/>).</summary>
        internal static Func<string, string> LocalizeForTesting;

        private static bool flightReadyPassArmed;
        private static bool loggedWaitingForStrip;
        private static float lastStripPassRealtime = float.MinValue;

        // ------------------------------------------------------------------ pure

        /// <summary>
        /// The leg of the live tree recording vessel <paramref name="livePid"/>: the active
        /// recorder's leg when it records that vessel, else its background leg. Pure.
        /// </summary>
        internal static string ResolveRecordingLegId(
            RecordingTree tree, bool recorderLive, uint recorderVesselPid, uint livePid)
        {
            if (tree == null || livePid == 0) return null;
            if (recorderLive && recorderVesselPid == livePid && !string.IsNullOrEmpty(tree.ActiveRecordingId)
                && tree.Recordings != null && tree.Recordings.ContainsKey(tree.ActiveRecordingId))
                return tree.ActiveRecordingId;
            if (tree.BackgroundMap != null && tree.BackgroundMap.TryGetValue(livePid, out string bg)
                && !string.IsNullOrEmpty(bg))
                return bg;
            return null;
        }

        /// <summary>
        /// A live vessel's CURRENT leg (design 7.4 step 3): <paramref name="recordingLegId"/> when
        /// it is a leg of <paramref name="identity"/>, else the newest leg (latest start, then
        /// latest end, then id) carrying the vessel's launch guid, preferring legs
        /// <paramref name="isExcludedLeg"/> does not exclude (superseded legs). A re-keyed vessel
        /// no leg carries yet (a Parsek spawn, an undocked ship before its first leg) follows its
        /// link rows (<see cref="VesselIdentity.Resolution.LinkTargets"/>) to the guid it
        /// continues: for a spawned vessel that is the spawning leg's guid. Null when none. Pure.
        /// </summary>
        internal static string ResolveCurrentLegId(
            VesselIdentity.Identity identity, VesselIdentity.Resolution res,
            string liveGuid, string recordingLegId, Func<string, bool> isExcludedLeg)
        {
            if (identity == null || res == null) return null;
            if (!string.IsNullOrEmpty(recordingLegId) && identity.LegIds.Contains(recordingLegId))
                return recordingLegId;
            string g = VesselLaunchIdentity.NormalizeGuid(liveGuid);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (g != null && visited.Add(g))
            {
                string leg = NewestLegCarryingGuid(identity, res, g, isExcludedLeg);
                if (leg != null) return leg;
                g = res.LinkTargets.TryGetValue(g, out string to) ? to : null;
            }
            return null;
        }

        private static string NewestLegCarryingGuid(
            VesselIdentity.Identity identity, VesselIdentity.Resolution res, string g,
            Func<string, bool> isExcludedLeg)
        {
            string best = null, bestExcluded = null;
            Recording bestRec = null, bestExcludedRec = null;
            foreach (string legId in identity.LegIds)
            {
                List<Recording> copies = res.Copies(legId);
                if (copies == null || copies.Count == 0) continue;
                Recording carrier = null;
                foreach (Recording copy in copies)
                    if (copy != null && string.Equals(VesselIdentity.LegGuid(copy), g, StringComparison.OrdinalIgnoreCase))
                    {
                        carrier = copy;
                        break;
                    }
                if (carrier == null) continue;
                bool excluded = isExcludedLeg != null && isExcludedLeg(legId);
                if (excluded)
                {
                    if (bestExcluded == null || IsNewer(carrier, legId, bestExcludedRec, bestExcluded))
                    {
                        bestExcluded = legId;
                        bestExcludedRec = carrier;
                    }
                }
                else if (best == null || IsNewer(carrier, legId, bestRec, best))
                {
                    best = legId;
                    bestRec = carrier;
                }
            }
            return best ?? bestExcluded;
        }

        private static bool IsNewer(Recording a, string aId, Recording b, string bId)
        {
            int c = a.StartUT.CompareTo(b.StartUT);
            if (c != 0) return c > 0;
            c = a.EndUT.CompareTo(b.EndUT);
            if (c != 0) return c > 0;
            return string.CompareOrdinal(aId, bId) > 0;
        }

        /// <summary>
        /// The live write rule for one vessel (design 7.4 step 3). A covered current leg is
        /// written only when the live name is STALE: the decision name, one of the decision's
        /// prior names, or the leg's own captured name (a quicksave name after a load, a
        /// stock-restored pre-dock name), compared raw and then localized the way a leg captures
        /// it (<paramref name="localize"/>, null: <see cref="Recording.ResolveLocalizedName"/>).
        /// Any other live name is a name nobody chose - a player rename always records a decision
        /// first through the capture Postfix, and a Parsek write is the decision itself - so it is
        /// stock's automatic rename after the leg opened (the old vessel renamed one Update after
        /// a staging split, design 8.17) and is never written over (ruling 4). Pure apart from
        /// the localizer.
        /// </summary>
        internal static LiveWriteVerdict DecideLiveWrite(
            VesselNameDecision decision, string currentLegId, string liveName, Func<string, string> localize)
        {
            if (decision == null) return LiveWriteVerdict.NoDecision;
            if (string.IsNullOrEmpty(currentLegId)) return LiveWriteVerdict.NoCurrentLeg;
            if (decision.UncoveredLegIds.Contains(currentLegId)) return LiveWriteVerdict.Uncovered;
            if (!decision.CoveredLegIds.Contains(currentLegId)) return LiveWriteVerdict.Unclassified;
            if (string.Equals(liveName, decision.Name, StringComparison.Ordinal)) return LiveWriteVerdict.AlreadyNamed;
            if (IsStaleKnownName(decision, currentLegId, liveName)) return LiveWriteVerdict.Write;
            string localized = LocalizeCaptureName(liveName, localize);
            if (!string.Equals(localized, liveName, StringComparison.Ordinal)
                && IsStaleKnownName(decision, currentLegId, localized))
                return LiveWriteVerdict.Write;
            return LiveWriteVerdict.UnchosenName;
        }

        /// <summary>The decision name, a prior name, or the leg's captured name. Raw ordinal. Pure.</summary>
        internal static bool IsStaleKnownName(VesselNameDecision d, string legId, string name)
        {
            if (d == null || string.IsNullOrEmpty(name)) return false;
            if (d.CoversCapturedName(name)) return true;
            return legId != null && d.CapturedNames.TryGetValue(legId, out string captured)
                && string.Equals(captured, name, StringComparison.Ordinal);
        }

        /// <summary>
        /// A live name in the form a leg captures it (<see cref="Recording.ResolveLocalizedName"/>,
        /// which only resolves <c>#</c> tags); <paramref name="localize"/> overrides it.
        /// </summary>
        internal static string LocalizeCaptureName(string name, Func<string, string> localize)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (localize != null) return localize(name) ?? name;
            if (name[0] != '#') return name;
            try
            {
                return Recording.ResolveLocalizedName(name);
            }
            catch (Exception)
            {
                return name;
            }
        }

        /// <summary>
        /// Walks <paramref name="vessels"/>: ghost map vessels are skipped, a vessel resolves to
        /// an identity only through a positive launch-guid match (never a bare pid), and a vessel
        /// whose verdict is <see cref="LiveWriteVerdict.Write"/> is written through. One summary
        /// line. Inert (no line) while the store holds no decision.
        /// </summary>
        internal static LiveWriteResult ApplyLiveWriteRule(
            VesselIdentity.Resolution res, IList<LiveVesselEntry> vessels, Func<string, bool> isExcludedLeg,
            string reason, string scopeLabel)
        {
            var r = new LiveWriteResult();
            if (res == null || vessels == null || VesselNameStore.DecisionCount == 0) return r;
            r.Ran = true;
            foreach (LiveVesselEntry v in vessels)
            {
                if (v == null) continue;
                r.Scanned++;
                if (v.IsGhostMap)
                {
                    r.SkippedGhost++;
                    continue;
                }
                VesselIdentity.Identity identity = res.ResolveLiveVessel(v.Guid, v.Pid, null);
                if (identity == null)
                {
                    r.NoIdentity++;
                    continue;
                }
                VesselNameDecision d = VesselNameStore.ResolveDecision(identity);
                string leg = d != null ? ResolveCurrentLegId(identity, res, v.Guid, v.RecordingLegId, isExcludedLeg) : null;
                switch (DecideLiveWrite(d, leg, v.Name, LocalizeForTesting))
                {
                    case LiveWriteVerdict.UnchosenName:
                        r.UnchosenName++;
                        if (KeepStockAutomaticName(res, d, leg, v, identity, reason))
                            r.LegsUncoveredByStockName++;
                        break;
                    case LiveWriteVerdict.NoDecision: r.NoDecision++; break;
                    case LiveWriteVerdict.NoCurrentLeg: r.NoCurrentLeg++; break;
                    case LiveWriteVerdict.Unclassified: r.Unclassified++; break;
                    case LiveWriteVerdict.Uncovered: r.Uncovered++; break;
                    case LiveWriteVerdict.AlreadyNamed: r.AlreadyNamed++; break;
                    case LiveWriteVerdict.Write:
                        ParsekLog.Verbose(Tag, "Live write rule: pid=" + v.Pid.ToString(CultureInfo.InvariantCulture) +
                            " '" + (v.Name ?? "") + "' -> '" + d.Name + "' currentLeg=" + leg +
                            " identity=" + (identity.Key ?? "<none>") + " seq=" + d.Seq.ToString(CultureInfo.InvariantCulture) +
                            " reason=" + (reason ?? "<none>"));
                        bool ok;
                        try
                        {
                            ok = v.Write != null && v.Write(d.Name);
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            ParsekLog.Warn(Tag, "Write-through threw for pid=" + v.Pid.ToString(CultureInfo.InvariantCulture) +
                                ": " + ex.GetType().Name + ": " + ex.Message);
                        }
                        if (ok) r.Written++; else r.Failed++;
                        break;
                }
            }
            ParsekLog.Verbose(Tag,
                "Live write pass (" + (reason ?? "<none>") + ") scope=" + (scopeLabel ?? "?") +
                ": scanned=" + r.Scanned.ToString(CultureInfo.InvariantCulture) +
                " written=" + r.Written.ToString(CultureInfo.InvariantCulture) +
                " alreadyNamed=" + r.AlreadyNamed.ToString(CultureInfo.InvariantCulture) +
                " uncovered=" + r.Uncovered.ToString(CultureInfo.InvariantCulture) +
                " unclassified=" + r.Unclassified.ToString(CultureInfo.InvariantCulture) +
                " noCurrentLeg=" + r.NoCurrentLeg.ToString(CultureInfo.InvariantCulture) +
                " noIdentity=" + r.NoIdentity.ToString(CultureInfo.InvariantCulture) +
                " noDecision=" + r.NoDecision.ToString(CultureInfo.InvariantCulture) +
                " skippedGhost=" + r.SkippedGhost.ToString(CultureInfo.InvariantCulture) +
                " failed=" + r.Failed.ToString(CultureInfo.InvariantCulture) +
                " unchosenName=" + r.UnchosenName.ToString(CultureInfo.InvariantCulture) +
                " legsUncoveredByStockName=" + r.LegsUncoveredByStockName.ToString(CultureInfo.InvariantCulture));
            if (r.LegsUncoveredByStockName > 0)
                DisplayNames.NoteChanged("vessel-names-stock-auto-name", reason);
            return r;
        }

        /// <summary>
        /// A covered current leg whose live vessel carries a name nobody chose (stock's automatic
        /// rename after the leg opened): never written over. When the leg is still open (the
        /// recorder is recording this vessel on it) it is marked uncovered and its
        /// <see cref="Recording.VesselName"/> (every copy) takes the live name in capture form,
        /// exactly as if the leg had opened after the rename; a closed leg keeps its coverage.
        /// True when a leg was uncovered.
        /// </summary>
        private static bool KeepStockAutomaticName(
            VesselIdentity.Resolution res, VesselNameDecision d, string leg, LiveVesselEntry v,
            VesselIdentity.Identity identity, string reason)
        {
            string captured = LocalizeCaptureName(v.Name, LocalizeForTesting);
            bool open = !string.IsNullOrEmpty(v.RecordingLegId)
                && string.Equals(v.RecordingLegId, leg, StringComparison.Ordinal);
            List<Recording> copies = res.Copies(leg);
            string old = copies != null && copies.Count > 0 && copies[0] != null ? copies[0].VesselName : null;
            string head = "Live write rule: stock automatic name kept on pid=" +
                v.Pid.ToString(CultureInfo.InvariantCulture) + " leg=" + leg + " '" + (old ?? "") + "' -> '" +
                (captured ?? "") + "' (not the decision, a prior or the captured name): ";
            string tail = " identity=" + (identity.Key ?? "<none>") + " seq=" + d.Seq.ToString(CultureInfo.InvariantCulture) +
                " reason=" + (reason ?? "<none>");
            if (!open)
            {
                ParsekLog.Info(Tag, head + "leg closed, coverage kept, no write" + tail);
                return false;
            }
            VesselNameStore.MarkLegUncoveredByStockName(d, leg);
            if (copies != null && !string.IsNullOrEmpty(captured))
                foreach (Recording copy in copies)
                    if (copy != null) copy.VesselName = captured;
            ParsekLog.Info(Tag, head + "open leg marked uncovered, no write" + tail);
            return true;
        }

        /// <summary>
        /// The stock rename capture core (design 7.1), run by the Postfix after the accept body:
        /// a ghost map vessel, an unchanged name (a type-only change) and a vessel no recording
        /// knows by its launch guid are ignored; otherwise a decision of
        /// <paramref name="source"/> is recorded for the vessel's identity (its prior names: the
        /// name the vessel carried, the identity's leg names and the replaced decision's names)
        /// and the apply pass runs over <paramref name="res"/>. <paramref name="outcome"/> names
        /// what happened. Pure over the resolution and the store.
        /// </summary>
        internal static bool RecordStockRename(
            VesselIdentity.Resolution res, string liveGuid, uint livePid, bool isGhostMap,
            string oldName, string newName, VesselNameSource source, string recordingLegId,
            Func<string, bool> isExcludedLeg, double ut,
            out VesselNameDecision decision, out string outcome)
        {
            decision = null;
            if (isGhostMap) { outcome = "ghost-map"; return false; }
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) { outcome = "name-unchanged"; return false; }
            if (!VesselRenameWriter.IsWritableName(newName)) { outcome = VesselNameStore.RefusalBlank; return false; }
            VesselIdentity.Identity identity = res?.ResolveLiveVessel(liveGuid, livePid, null);
            if (identity == null) { outcome = "no-identity"; return false; }
            string legId = ResolveCurrentLegId(identity, res, liveGuid, recordingLegId, isExcludedLeg)
                ?? (identity.LegIds.Count > 0 ? identity.LegIds[0] : null);
            List<Recording> copies = res.Copies(legId);
            Recording rep = copies != null && copies.Count > 0 ? copies[0] : null;
            if (!VesselNameStore.TryRecordDecision(res, rep, newName, source, ut, out decision, out string refusal,
                    extraPriorNames: new[] { oldName }, refuseUnchangedRow: false))
            {
                outcome = refusal;
                return false;
            }
            VesselNameStore.ApplyTo(res, "stock-rename");
            outcome = "recorded";
            return true;
        }

        /// <summary>
        /// The names the live vessels resolving to <paramref name="identity"/> carry now (raw,
        /// plus the capture-localized form when it differs), for a Parsek rename's prior names.
        /// <paramref name="headless"/> reads only <see cref="LiveVesselsProviderForTesting"/>.
        /// Never throws.
        /// </summary>
        internal static List<string> LiveNamesOfIdentity(
            VesselIdentity.Resolution res, VesselIdentity.Identity identity, bool headless)
        {
            var names = new List<string>();
            if (res == null || identity == null) return names;
            try
            {
                List<LiveVesselEntry> vessels;
                Func<VesselNameLiveScope, List<LiveVesselEntry>> provider = LiveVesselsProviderForTesting;
                if (provider != null) vessels = provider(VesselNameLiveScope.Auto);
                else if (headless || VesselNameStore.TreesProviderForTesting != null) return names;
                else
                {
                    try
                    {
                        vessels = GatherLiveVesselsCore(VesselNameLiveScope.Auto, "parsek-rename-priors", out _);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException
                        || ex is TypeInitializationException || ex is MissingMethodException)
                    {
                        vessels = null;
                    }
                }
                if (vessels == null) return names;
                foreach (LiveVesselEntry v in vessels)
                {
                    if (v == null || v.IsGhostMap || string.IsNullOrEmpty(v.Name)) continue;
                    if (!ReferenceEquals(res.ResolveLiveVessel(v.Guid, v.Pid, null), identity)) continue;
                    names.Add(v.Name);
                    string localized = LocalizeCaptureName(v.Name, LocalizeForTesting);
                    if (!string.IsNullOrEmpty(localized) && localized != v.Name) names.Add(localized);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Live names of an identity threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            return names;
        }

        // ------------------------------------------------------------------ production

        /// <summary>
        /// The Postfix's production wrapper: resolves over the live recordings, records the
        /// decision, sets the vessel's priority naming part to it (so the post-load
        /// <c>vesselNamedBy == null</c> trap cannot revert the name), and runs the live write
        /// rule for the scene. Never throws into the stock dialog callback.
        /// </summary>
        internal static void OnStockRenameAccepted(Vessel v, string oldName, VesselNameSource source)
        {
            if (v == null) return;
            try
            {
                string newName = v.vesselName;
                uint pid = v.persistentId;
                bool ghost = GhostMapPresence.IsGhostMapVessel(pid);
                if (ghost || string.Equals(oldName, newName, StringComparison.Ordinal))
                {
                    ParsekLog.Verbose(Tag, "Stock rename ignored: source=" + source + " pid=" +
                        pid.ToString(CultureInfo.InvariantCulture) + " reason=" + (ghost ? "ghost-map" : "name-unchanged"));
                    return;
                }
                List<RecordingTree> trees = VesselNameStore.GatherTrees();
                VesselIdentity.Resolution res = VesselNameStore.ResolveUniverse(trees, VesselNameStore.GatherLoose(trees));
                string guid = v.id.ToString("N");
                string recordingLegId = ReadRecordingLegIdSafe(pid);
                Func<string, bool> excluded = BuildExcludedLegPredicate(res);
                if (!RecordStockRename(res, guid, pid, false, oldName, newName, source, recordingLegId, excluded,
                        VesselNameStore.ReadNowUT(), out VesselNameDecision d, out string outcome))
                {
                    ParsekLog.Verbose(Tag, "Stock rename ignored: source=" + source + " pid=" +
                        pid.ToString(CultureInfo.InvariantCulture) + " guid=" + guid + " '" + (oldName ?? "") +
                        "' -> '" + (newName ?? "") + "' reason=" + outcome);
                    return;
                }
                string naming = VesselRenameWriter.UpdatePriorityNamingPart(v, d.Name);
                ParsekLog.Info(Tag, "Stock rename captured: source=" + source + " pid=" +
                    pid.ToString(CultureInfo.InvariantCulture) + " guid=" + guid + " '" + (oldName ?? "") +
                    "' -> '" + d.Name + "' seq=" + d.Seq.ToString(CultureInfo.InvariantCulture) +
                    " namingPart=" + naming + " currentLeg=" + (recordingLegId ?? "<none>"));
                RunLiveWritePass(res, "stock-rename", VesselNameLiveScope.Auto, headless: false);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Stock rename capture threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The live write rule over <paramref name="scope"/>'s vessels. <paramref name="headless"/>
        /// (a unit-test universe) reads only <see cref="LiveVesselsProviderForTesting"/>. Never
        /// throws.
        /// </summary>
        internal static LiveWriteResult RunLiveWritePass(
            VesselIdentity.Resolution res, string reason, VesselNameLiveScope scope, bool headless)
        {
            if (res == null || VesselNameStore.DecisionCount == 0) return default;
            try
            {
                List<LiveVesselEntry> vessels;
                string scopeLabel;
                Func<string, bool> excluded;
                Func<VesselNameLiveScope, List<LiveVesselEntry>> provider = LiveVesselsProviderForTesting;
                if (provider != null)
                {
                    vessels = provider(scope);
                    scopeLabel = "test:" + scope;
                    excluded = ExcludedLegForTesting;
                }
                else
                {
                    if (headless || VesselNameStore.TreesProviderForTesting != null) return default;
                    try
                    {
                        vessels = GatherLiveVesselsCore(scope, reason, out scopeLabel);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException
                        || ex is TypeInitializationException || ex is MissingMethodException)
                    {
                        // Outside the Unity player (xUnit): no live vessels to walk.
                        vessels = null;
                        scopeLabel = "no-unity-player";
                    }
                    if (vessels == null)
                    {
                        ParsekLog.Verbose(Tag, "Live write pass (" + (reason ?? "<none>") + ") skipped: " + scopeLabel);
                        return default;
                    }
                    excluded = BuildExcludedLegPredicate(res);
                }
                return ApplyLiveWriteRule(res, vessels, excluded, reason, scopeLabel);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Live write pass (" + (reason ?? "<none>") + ") threw: " +
                    ex.GetType().Name + ": " + ex.Message);
                return default;
            }
        }

        /// <summary>
        /// The post-load pass (design 7.6 step 2) over every vessel of the flight scene: the
        /// apply pass first (a revert undo or the load may have moved leg names), then the live
        /// write rule over all of <c>FlightGlobals.Vessels</c>.
        /// </summary>
        internal static LiveWriteResult RunFlightPostLoadPass(string reason)
        {
            if (VesselNameStore.DecisionCount == 0)
            {
                ParsekLog.Verbose(Tag, "Post-load live write pass (" + (reason ?? "<none>") + "): no decisions");
                return default;
            }
            try
            {
                List<RecordingTree> trees = VesselNameStore.GatherTrees();
                VesselIdentity.Resolution res = VesselNameStore.ResolveUniverse(trees, VesselNameStore.GatherLoose(trees));
                VesselNameStore.ApplyTo(res, reason);
                return RunLiveWritePass(res, reason, VesselNameLiveScope.FlightVessels, headless: false);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Post-load live write pass (" + (reason ?? "<none>") + ") threw: " +
                    ex.GetType().Name + ": " + ex.Message);
                return default;
            }
        }

        /// <summary>
        /// <c>onFlightReady</c>: a new stock revert target starts a new flight epoch
        /// (<see cref="VesselNameStore.NoteFlightReady"/>), and the post-load pass is armed for
        /// the first host tick unless a Re-Fly strip pass already ran for this load.
        /// </summary>
        internal static void OnFlightReady(object postInitState, float realtime)
        {
            try
            {
                VesselNameStore.NoteFlightReady(postInitState);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Flight epoch update threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (realtime - lastStripPassRealtime <= StripPassCoversFlightReadySeconds)
            {
                flightReadyPassArmed = false;
                ParsekLog.Verbose(Tag, "Post-load live write pass not armed: the Re-Fly strip pass already ran for this load");
                return;
            }
            flightReadyPassArmed = true;
            loggedWaitingForStrip = false;
            ParsekLog.Verbose(Tag, "Post-load live write pass armed for the first flight host tick");
        }

        /// <summary>
        /// FLIGHT host tick: runs the armed post-load pass, unless a Re-Fly invocation is still
        /// pending (its strip runs the pass itself; a strip that never completes clears the
        /// pending flag, and the pass then runs here).
        /// </summary>
        internal static void FlightHostTick(float realtime)
        {
            if (!flightReadyPassArmed) return;
            if (RewindInvokeContext.Pending)
            {
                if (!loggedWaitingForStrip)
                {
                    loggedWaitingForStrip = true;
                    ParsekLog.Verbose(Tag, "Post-load live write pass waiting: a Re-Fly strip is pending");
                }
                return;
            }
            flightReadyPassArmed = false;
            RunFlightPostLoadPass("post-load:flight-ready");
        }

        /// <summary>
        /// End of <c>RewindInvoker.RunStripActivateMarker</c>: a Re-Fly attempt starts (new
        /// flight epoch, so Discard Re-fly and Retry cancel exactly its stock renames), and the
        /// post-load pass runs now that the strip has finished.
        /// </summary>
        internal static void OnReFlyStripComplete(string sessionId, float realtime)
        {
            VesselNameStore.BeginReFlyAttemptEpoch(sessionId);
            flightReadyPassArmed = false;
            lastStripPassRealtime = realtime;
            RunFlightPostLoadPass("post-load:refly-strip");
        }

        /// <summary>
        /// <c>onPartCouple</c> (design 7.7): records the dock-absorb row of the vessel being
        /// absorbed (its root part flightID at dock time and its launch guid). An EVA kerbal
        /// and a ghost map vessel are skipped. Never throws.
        /// </summary>
        internal static void OnDockCouple(Vessel absorbed, Vessel dominant)
        {
            try
            {
                if (absorbed == null || ReferenceEquals(absorbed, dominant)) return;
                if (absorbed.isEVA || GhostMapPresence.IsGhostMapVessel(absorbed.persistentId))
                {
                    ParsekLog.Verbose(Tag, "Dock absorb row skipped: pid=" +
                        absorbed.persistentId.ToString(CultureInfo.InvariantCulture) +
                        (absorbed.isEVA ? " (EVA kerbal)" : " (ghost map vessel)"));
                    return;
                }
                Part root = absorbed.rootPart;
                VesselNameStore.RecordDockAbsorb(root != null ? root.flightID : 0u, absorbed.id.ToString("N"),
                    "dock pid=" + absorbed.persistentId.ToString(CultureInfo.InvariantCulture) +
                    " into=" + (dominant != null ? dominant.persistentId.ToString(CultureInfo.InvariantCulture) : "<none>"));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Dock absorb row threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// <c>onVesselsUndocking</c> (design 7.7): the vessel that left (stock re-roots it on the
        /// pre-dock root part when that part is in its subtree, <c>Part.Undock</c>) is linked to
        /// the absorbed identity when a dock-absorb row names its root part. The link schedules
        /// an apply pass, whose live write rule writes the decision through when the leaving
        /// ship's leg is covered. Never throws.
        /// </summary>
        internal static void OnUndockSplit(Vessel leaving)
        {
            try
            {
                if (leaving == null || GhostMapPresence.IsGhostMapVessel(leaving.persistentId)) return;
                Part root = leaving.rootPart;
                VesselNameStore.TryLinkUndockReturn(root != null ? root.flightID : 0u, leaving.id.ToString("N"),
                    "undock pid=" + leaving.persistentId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Undock link threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void ResetForTesting()
        {
            LiveVesselsProviderForTesting = null;
            ExcludedLegForTesting = null;
            LocalizeForTesting = null;
            flightReadyPassArmed = false;
            loggedWaitingForStrip = false;
            lastStripPassRealtime = float.MinValue;
        }

        internal static bool FlightReadyPassArmedForTesting => flightReadyPassArmed;

        // ------------------------------------------------------------------ Unity reads

        private static Func<string, bool> BuildExcludedLegPredicate(VesselIdentity.Resolution res)
        {
            IReadOnlyList<RecordingSupersedeRelation> supersedes = ReadSupersedesSafe();
            if (supersedes == null || supersedes.Count == 0) return null;
            return legId =>
            {
                List<Recording> copies = res.Copies(legId);
                if (copies == null) return false;
                foreach (Recording c in copies)
                    if (EffectiveState.IsSupersededByRelation(c, supersedes)) return true;
                return false;
            };
        }

        private static IReadOnlyList<RecordingSupersedeRelation> ReadSupersedesSafe()
        {
            try
            {
                ParsekScenario scenario = ParsekScenario.Instance;
                return ReferenceEquals(scenario, null) ? null : scenario.RecordingSupersedes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadRecordingLegIdSafe(uint pid)
        {
            try
            {
                ParsekFlight flight = ParsekFlight.Instance;
                return ReferenceEquals(flight, null) ? null : flight.ResolveRecordingLegIdForLiveVessel(pid);
            }
            catch (Exception)
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<LiveVesselEntry> GatherLiveVesselsCore(VesselNameLiveScope scope, string reason, out string scopeLabel)
        {
            GameScenes scene = HighLogic.LoadedScene;
            bool flight = scene == GameScenes.FLIGHT;
            if (scope == VesselNameLiveScope.ProtosOnly
                || (scope == VesselNameLiveScope.Auto && (scene == GameScenes.TRACKSTATION || scene == GameScenes.SPACECENTER)))
            {
                scopeLabel = "protos:" + scene;
                return GatherProtoEntries(reason);
            }
            if (scope == VesselNameLiveScope.FlightVessels || (scope == VesselNameLiveScope.Auto && flight))
            {
                if (!flight) { scopeLabel = "not-flight:" + scene; return null; }
                if (!FlightGlobals.ready) { scopeLabel = "flight-not-ready"; return null; }
                if (scope == VesselNameLiveScope.Auto && RewindInvokeContext.Pending)
                {
                    scopeLabel = "refly-strip-pending";
                    return null;
                }
                scopeLabel = "flight-vessels";
                var list = new List<LiveVesselEntry>();
                List<Vessel> vessels = FlightGlobals.Vessels;
                if (vessels == null) return list;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v == null) continue;
                    list.Add(VesselEntry(v, ReadRecordingLegIdSafe(v.persistentId), reason));
                }
                return list;
            }
            scopeLabel = "no-vessels:" + scene;
            return null;
        }

        /// <summary>
        /// The loaded game's proto vessels for the spawn-link backfill (OnLoad tail), or the test
        /// provider's <see cref="VesselNameLiveScope.ProtosOnly"/> rows. Null outside the Unity
        /// player or in a headless test universe.
        /// </summary>
        internal static List<LiveVesselEntry> GatherProtoVesselsForBackfill(string reason)
        {
            Func<VesselNameLiveScope, List<LiveVesselEntry>> provider = LiveVesselsProviderForTesting;
            if (provider != null) return provider(VesselNameLiveScope.ProtosOnly);
            if (VesselNameStore.TreesProviderForTesting != null) return null;
            try
            {
                return GatherProtoEntriesCore(reason);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                || ex is TypeInitializationException || ex is MissingMethodException)
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<LiveVesselEntry> GatherProtoEntriesCore(string reason)
            => GatherProtoEntries(reason);

        private static List<LiveVesselEntry> GatherProtoEntries(string reason)
        {
            var list = new List<LiveVesselEntry>();
            List<ProtoVessel> protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos == null) return list;
            for (int i = 0; i < protos.Count; i++)
            {
                ProtoVessel pv = protos[i];
                if (pv == null) continue;
                if (pv.vesselRef != null)
                {
                    list.Add(VesselEntry(pv.vesselRef, null, reason));
                    continue;
                }
                ProtoVessel captured = pv;
                list.Add(new LiveVesselEntry
                {
                    Guid = pv.vesselID.ToString("N"),
                    Pid = pv.persistentId,
                    Name = pv.vesselName,
                    RecordingLegId = null,
                    IsGhostMap = GhostMapPresence.IsGhostMapVessel(pv.persistentId),
                    Write = n => VesselRenameWriter.ApplyProtoName(captured, n, reason),
                });
            }
            return list;
        }

        private static LiveVesselEntry VesselEntry(Vessel v, string recordingLegId, string reason)
        {
            Vessel captured = v;
            return new LiveVesselEntry
            {
                Guid = v.id.ToString("N"),
                Pid = v.persistentId,
                Name = v.vesselName,
                RecordingLegId = recordingLegId,
                IsGhostMap = GhostMapPresence.IsGhostMapVessel(v.persistentId),
                Write = n => VesselRenameWriter.ApplyStockName(captured, n, reason),
            };
        }
    }
}
