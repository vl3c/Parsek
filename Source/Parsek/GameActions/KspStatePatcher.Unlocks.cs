using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Add-only purchase-state unlocks: part purchases, resource scans and part upgrades.
    internal static partial class KspStatePatcher
    {
        // ---------------- part purchases (P1 state half) ----------------

        /// <summary>
        /// The UT up to which committed part purchases are applied to stock: the supplied
        /// cutoff (tech-tree cutoff, else walk cutoff), CLAMPED to the live clock whenever
        /// the clock is ready. The patch is add-only, so marking a purchase that lies after
        /// now could never be undone, and the block (which lifts once stock shows the part
        /// purchased) would hand the player the part before its committed date. The clamp
        /// is load-bearing on two shapes:
        /// <list type="bullet">
        /// <item>a cutoff-less walk (no committed row ahead): the KSC ledger cursor takes
        /// that path when the clock passes the LAST committed row, and the live clock is
        /// then the only cutoff there is;</item>
        /// <item>a full-timeline walk with a sentinel tech cutoff:
        /// <c>LedgerOrchestrator.RecalculateAndPatchAfterTombstones</c> passes
        /// <c>techPatchCutoff = double.MaxValue</c> with no walk cutoff whenever the pass
        /// retired a ScienceSpending row. A non-finite or <c>MaxValue</c> cutoff therefore
        /// counts as no cutoff.</item>
        /// </list>
        /// The block reads the same clock (<c>row.UT &gt; now</c>), so a purchase is marked
        /// exactly when its block would lift. With the clock not ready (a cold load reads
        /// UT 0) only an explicit finite cutoff is used; null (skip) when there is none. Pure.
        /// </summary>
        internal static double? ResolvePartPurchasePatchCutoff(
            double? walkCutoff, double? techPatchCutoff, double liveUT)
        {
            double? resolved = techPatchCutoff ?? walkCutoff;
            if (resolved.HasValue && !IsFiniteExplicitCutoff(resolved.Value))
                resolved = null;
            if (!LedgerOrchestrator.IsCurrentUtReadyForCutoff(liveUT))
                return resolved;
            return resolved.HasValue ? Math.Min(resolved.Value, liveUT) : liveUT;
        }

        private static bool IsFiniteExplicitCutoff(double cutoff)
        {
            return !double.IsNaN(cutoff) && !double.IsInfinity(cutoff) && cutoff < double.MaxValue;
        }

        /// <summary>
        /// The part names the patch marks purchased: every effective part-purchase row
        /// (<c>FundsSpending</c>, <c>FundsSpendingSource.Other</c>, key = the runtime
        /// <c>AvailablePart.name</c> in <c>DedupKey</c>) at or before
        /// <paramref name="cutoff"/>. Every such row counts whatever its amount: the walk
        /// charges it, and the purchase happened on the committed timeline. Sorted, so the
        /// apply order and its log are deterministic. Pure.
        /// </summary>
        internal static List<string> BuildPurchasedPartNamesForPatch(
            IReadOnlyList<GameAction> actions, double cutoff)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null || a.Type != GameActionType.FundsSpending) continue;
                    if (a.FundsSpendingSource != FundsSpendingSource.Other) continue;
                    if (string.IsNullOrEmpty(a.DedupKey)) continue;
                    if (a.UT > cutoff) continue;
                    names.Add(a.DedupKey);
                }
            }
            return new List<string>(names);
        }

        /// <summary>
        /// The names one <see cref="PatchPurchasedParts"/> pass would mark purchased, from the
        /// caller's cutoffs and the live clock (<see cref="ResolvePartPurchasePatchCutoff"/>
        /// then <see cref="BuildPurchasedPartNamesForPatch"/>). Null when there is no usable
        /// cutoff. Pure: the live wrapper only adds the stock reads and writes.
        /// </summary>
        internal static List<string> PlanPurchasedPartNamesForPatch(
            IReadOnlyList<GameAction> actions, double? walkCutoff, double? techPatchCutoff,
            double liveUT, out double? cutoff)
        {
            cutoff = ResolvePartPurchasePatchCutoff(walkCutoff, techPatchCutoff, liveUT);
            return cutoff.HasValue ? BuildPurchasedPartNamesForPatch(actions, cutoff.Value) : null;
        }

        /// <summary>What <see cref="ApplyPartPurchasePatch"/> did with one part name.</summary>
        internal enum PartPurchasePatchOutcome { Added, AlreadyPurchased, UnknownPart, TechNotResearched }

        /// <summary>The per-pass counters and the bounded identity lists the log prints.</summary>
        internal sealed class PartPurchasePatchResult
        {
            internal int Added;
            internal int AlreadyPurchased;
            internal int UnknownPart;
            internal int TechNotResearched;
            internal readonly List<string> AddedNames = new List<string>();
            internal readonly List<string> UnknownNames = new List<string>();
            internal readonly List<string> TechNotResearchedNames = new List<string>();
        }

        /// <summary>
        /// The add-only core of <see cref="PatchPurchasedParts"/>, over delegates so it is
        /// testable without KSP. For each name: an unknown part (no loaded part or no
        /// required tech) is skipped; a part whose tech is not researched in stock is
        /// skipped and logged, never forced (a committed purchase implies its tech is
        /// researched by then, so the gap is a ledger / tech-state disagreement to see,
        /// not to paper over); an already purchased part is left alone; otherwise
        /// <paramref name="markPurchased"/> adds it. Nothing is ever removed: a rewind
        /// reloads stock's purchased state from its quicksave. Running it twice adds
        /// nothing the second time.
        /// </summary>
        internal static PartPurchasePatchResult ApplyPartPurchasePatch(
            IEnumerable<string> partNames,
            Func<string, string> techOfPart,
            Func<string, bool> isTechResearched,
            Func<string, bool> isPurchased,
            Action<string> markPurchased)
        {
            var result = new PartPurchasePatchResult();
            if (partNames == null) return result;
            foreach (var name in partNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                switch (ClassifyPartPurchaseForPatch(name, techOfPart, isTechResearched, isPurchased))
                {
                    case PartPurchasePatchOutcome.UnknownPart:
                        result.UnknownPart++;
                        result.UnknownNames.Add(name);
                        break;
                    case PartPurchasePatchOutcome.TechNotResearched:
                        result.TechNotResearched++;
                        result.TechNotResearchedNames.Add(name);
                        break;
                    case PartPurchasePatchOutcome.AlreadyPurchased:
                        result.AlreadyPurchased++;
                        break;
                    default:
                        markPurchased?.Invoke(name);
                        result.Added++;
                        result.AddedNames.Add(name);
                        break;
                }
            }
            return result;
        }

        internal static PartPurchasePatchOutcome ClassifyPartPurchaseForPatch(
            string partName,
            Func<string, string> techOfPart,
            Func<string, bool> isTechResearched,
            Func<string, bool> isPurchased)
        {
            string techId = techOfPart != null ? techOfPart(partName) : null;
            if (string.IsNullOrEmpty(techId)) return PartPurchasePatchOutcome.UnknownPart;
            if (isTechResearched == null || !isTechResearched(techId)) return PartPurchasePatchOutcome.TechNotResearched;
            if (isPurchased != null && isPurchased(partName)) return PartPurchasePatchOutcome.AlreadyPurchased;
            return PartPurchasePatchOutcome.Added;
        }

        /// <summary>
        /// The state half of the part-purchase reservation (P1): marks every part whose
        /// committed purchase row the walk has reached as purchased in stock, the way
        /// Contract Configurator's <c>UnlockPart</c> does (add the <c>AvailablePart</c> to
        /// its tech's <c>ProtoTechNode.partsPurchased</c>, then <c>SetTechState</c>). Runs
        /// after <see cref="PatchTechTree"/> and <see cref="PatchCommittedTechUnlocks"/>
        /// because a node either newly unlocks gets an empty list. Only with bypass-entry-purchase OFF: with it on, stock rehydrates
        /// every part of a researched node (<see cref="EnsureAvailableProtoTechNode"/>).
        /// Writing the list fires no <c>OnPartPurchased</c>, so no ledger row and no funds
        /// change result (and <see cref="PatchAll"/>'s suppression covers the rest).
        ///
        /// <para>Load order: every OnLoad recalc runs inside <c>ScenarioRunner.LoadModules</c>,
        /// which loads the save's SCENARIO modules one at a time. A cold load also schedules
        /// <c>ParsekScenario.DeferredSeedAndRecalculate</c>, which repeats the recalc a frame
        /// later with every module loaded. A WARM load (scene change, revert, rewind) returns
        /// from <c>ParsekScenario.OnLoad</c> before that coroutine is scheduled, so its one
        /// recalc works only because <c>ResearchAndDevelopment</c> precedes
        /// <c>ParsekScenario</c> in SCENARIO order and has already loaded its tech state.
        /// <see cref="PatchTechTree"/> relies on the same order. If R&amp;D were absent
        /// the patch skips (logged) and the next recalc applies it.</para>
        ///
        /// <para>Clock: stock pauses <c>Planetarium</c> in the VAB/SPH
        /// (<c>PSystemSetup.SetEditor</c> sets <c>Planetarium.fetch.pause = true</c>), so a
        /// purchase row's UT cannot pass while the player sits in the editor. At the Space
        /// Center the clock runs, and the KSC ledger cursor recalculates in the same frame
        /// the clock reaches the next committed row (<c>currentUT &gt;= nextActionUT</c>).</para>
        /// </summary>
        internal static void PatchPurchasedParts(
            IReadOnlyList<GameAction> actions, double? walkCutoff, double? techPatchCutoff)
        {
            if (actions == null)
            {
                VerboseStablePatchState("patch-skip|parts|actions", "actions-null",
                    "PatchPurchasedParts: no action list supplied - skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                VerboseStablePatchState("patch-skip|parts|rnd", "rnd-null",
                    "PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping " +
                    "(the next recalc with R&D loaded applies it)");
                return;
            }

            if (GameStateRecorder.IsBypassEntryPurchaseAfterResearch())
            {
                VerboseStablePatchState("patch-skip|parts|bypass", "bypass-on",
                    "PatchPurchasedParts: bypass-entry-purchase is on - stock rehydrates researched parts, skipping");
                return;
            }

            var liveProvider = PartPurchaseLiveUtProviderForTesting;
            double liveUT = liveProvider != null ? liveProvider() : CommittedFutureIndexCache.CurrentUT();
            double? cutoff;
            var names = PlanPurchasedPartNamesForPatch(actions, walkCutoff, techPatchCutoff, liveUT, out cutoff);
            if (names == null)
            {
                VerboseStablePatchState("patch-skip|parts|cutoff", "cutoff-unknown",
                    "PatchPurchasedParts: no finite cutoff and the clock is not ready - skipping");
                return;
            }

            if (names.Count == 0)
            {
                VerboseStablePatchState("patch-noop|parts", "no-rows",
                    "PatchPurchasedParts: no committed part purchase at or before the cutoff");
                return;
            }

            var partCache = new Dictionary<string, AvailablePart>(StringComparer.Ordinal);
            Func<string, AvailablePart> part = n =>
            {
                AvailablePart ap;
                if (!partCache.TryGetValue(n, out ap))
                {
                    ap = PartLoader.getPartInfoByName(n);
                    partCache[n] = ap;
                }
                return ap;
            };
            var rnd = ResearchAndDevelopment.Instance;
            var result = ApplyPartPurchasePatch(
                names,
                n => part(n)?.TechRequired,
                techId =>
                {
                    ProtoTechNode proto = rnd.GetTechState(techId);
                    return proto != null && proto.state == RDTech.State.Available;
                },
                n =>
                {
                    AvailablePart ap = part(n);
                    ProtoTechNode proto = rnd.GetTechState(ap.TechRequired);
                    return proto != null && ContainsPurchasedPart(proto.partsPurchased, ap);
                },
                n =>
                {
                    AvailablePart ap = part(n);
                    ProtoTechNode proto = rnd.GetTechState(ap.TechRequired);
                    if (proto.partsPurchased == null)
                        proto.partsPurchased = new List<AvailablePart>();
                    proto.partsPurchased.Add(ap);
                    rnd.SetTechState(ap.TechRequired, proto);
                });

            string cutoffLabel = cutoff.Value.ToString("R", IC);
            string summary =
                $"PatchPurchasedParts: targets={names.Count.ToString(IC)}, " +
                $"added={result.Added.ToString(IC)}, " +
                $"alreadyPurchased={result.AlreadyPurchased.ToString(IC)}, " +
                $"unknownPart={result.UnknownPart.ToString(IC)}, " +
                $"techNotResearched={result.TechNotResearched.ToString(IC)}, " +
                $"cutoffUT={cutoffLabel}" +
                (result.AddedNames.Count > 0
                    ? $", addedParts=[{ComposeBoundedIdentitySample(result.AddedNames, IdentitySampleCap)}]"
                    : string.Empty);
            if (result.Added > 0)
                ParsekLog.Info(Tag, summary);
            else
                VerboseStablePatchState("patch-parts", summary, summary);

            if (result.TechNotResearched > 0)
            {
                ParsekLog.Warn(Tag,
                    "PatchPurchasedParts: committed purchase of a part whose tech is not researched in stock - " +
                    $"left unpurchased, not forced: parts=[{ComposeBoundedIdentitySample(result.TechNotResearchedNames, IdentitySampleCap)}], " +
                    $"cutoffUT={cutoffLabel}");
            }
            if (result.UnknownPart > 0)
            {
                ParsekLog.Verbose(Tag,
                    "PatchPurchasedParts: committed purchase of a part not loaded in this install - skipped: " +
                    $"parts=[{ComposeBoundedIdentitySample(result.UnknownNames, IdentitySampleCap)}]");
            }

            if (result.Added > 0)
            {
                RefreshTechTreeUi();
                RefreshEditorPartList();
            }
        }

        private static void RefreshEditorPartList()
        {
            try
            {
                if (HighLogic.LoadedScene == GameScenes.EDITOR && KSP.UI.Screens.EditorPartList.Instance != null)
                    KSP.UI.Screens.EditorPartList.Instance.Refresh();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"PatchPurchasedParts: EditorPartList.Refresh threw: {ex.Message}");
            }
        }

        /// <summary>One committed scan unlock: a whole body (<see cref="Biome"/> null) or one biome.</summary>
        internal struct ResourceScanUnlock
        {
            public string Body;
            public string Biome;
        }

        /// <summary>
        /// The scans the committed timeline has made by <paramref name="cutoff"/>: every
        /// <see cref="GameActionType.ResourceScanUnlocked"/> row with a body name at or before
        /// it, de-duplicated by (body, biome) and sorted, so the apply order and its log are
        /// deterministic. Pure.
        /// </summary>
        internal static List<ResourceScanUnlock> BuildResourceScanUnlocksForPatch(
            IReadOnlyList<GameAction> actions, double cutoff)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            var result = new List<ResourceScanUnlock>();
            if (actions == null) return result;
            var byKey = new Dictionary<string, ResourceScanUnlock>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.ResourceScanUnlocked) continue;
                if (string.IsNullOrEmpty(a.ScanBodyName)) continue;
                if (a.UT > cutoff) continue;
                string biome = string.IsNullOrEmpty(a.ScanBiomeName) ? null : a.ScanBiomeName;
                string key = a.ScanBodyName + "|" + (biome ?? "");
                if (keys.Add(key))
                    byKey[key] = new ResourceScanUnlock { Body = a.ScanBodyName, Biome = biome };
            }
            foreach (string key in keys)
                result.Add(byKey[key]);
            return result;
        }

        /// <summary>
        /// The scans one <see cref="PatchResourceScans"/> pass applies: the committed rows at or
        /// before the part-purchase cutoff (<see cref="ResolvePartPurchasePatchCutoff"/>: the
        /// supplied cutoff clamped to the live clock when it is ready). The patch is add-only
        /// (stock has no re-lock), so the same clamp keeps a future scan from ever reaching
        /// stock early; a rewind needs no re-lock because its quicksave predates every later
        /// scan. Null when there is no usable cutoff. Pure.
        /// </summary>
        internal static List<ResourceScanUnlock> PlanResourceScanUnlocksForPatch(
            IReadOnlyList<GameAction> actions, double? walkCutoff, double? techPatchCutoff,
            double liveUT, out double? cutoff)
        {
            cutoff = ResolvePartPurchasePatchCutoff(walkCutoff, techPatchCutoff, liveUT);
            return cutoff.HasValue ? BuildResourceScanUnlocksForPatch(actions, cutoff.Value) : null;
        }

        /// <summary>The per-pass counters and the bounded identity lists the scan patch logs.</summary>
        internal sealed class ResourceScanPatchResult
        {
            public int Unlocked;
            public int AlreadyUnlocked;
            public int UnknownBody;
            public readonly List<string> UnlockedIds = new List<string>();
            public readonly List<string> UnknownBodies = new List<string>();
        }

        /// <summary>
        /// Applies <paramref name="unlocks"/> through the supplied stock seams, add-only: a body
        /// or biome already unlocked is counted and left alone, a body the install does not have
        /// is counted and skipped. The live wrapper passes <c>ResourceMap</c>'s own methods;
        /// tests pass fakes. Pure apart from the seams.
        /// </summary>
        internal static ResourceScanPatchResult ApplyResourceScanUnlocks(
            IReadOnlyList<ResourceScanUnlock> unlocks,
            Func<string, int> planetIdForName,
            Func<int, bool> isPlanetScanned,
            Func<int, string, bool> isBiomeUnlocked,
            Action<int> unlockPlanet,
            Action<int, string> unlockBiome)
        {
            var result = new ResourceScanPatchResult();
            if (unlocks == null) return result;
            for (int i = 0; i < unlocks.Count; i++)
            {
                var u = unlocks[i];
                int planetId = planetIdForName(u.Body);
                if (planetId < 0)
                {
                    result.UnknownBody++;
                    result.UnknownBodies.Add(u.Body);
                    continue;
                }

                string id = u.Biome == null ? u.Body : u.Body + "/" + u.Biome;
                if (u.Biome == null)
                {
                    if (isPlanetScanned(planetId)) { result.AlreadyUnlocked++; continue; }
                    unlockPlanet(planetId);
                }
                else
                {
                    if (isBiomeUnlocked(planetId, u.Biome)) { result.AlreadyUnlocked++; continue; }
                    unlockBiome(planetId, u.Biome);
                }
                result.Unlocked++;
                result.UnlockedIds.Add(id);
            }
            return result;
        }

        /// <summary>
        /// The state half of the scan ledger (RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED): unlocks in
        /// stock's <c>ResourceMap</c> every body and biome the committed timeline scanned at or
        /// before the cutoff, so a committed future scan returns when the clock reaches it after
        /// a rewind. Add-only; runs inside <see cref="PatchAll"/>'s replay suppression, so the
        /// unlock is not captured again. Skips (the next recalc applies it) when the scan
        /// scenario is not loaded in this scene.
        /// </summary>
        internal static void PatchResourceScans(
            IReadOnlyList<GameAction> actions, double? walkCutoff, double? techPatchCutoff)
        {
            if (actions == null)
            {
                VerboseStablePatchState("patch-skip|scans|actions", "actions-null",
                    "PatchResourceScans: no action list supplied - skipping");
                return;
            }

            var liveProvider = ResourceScanLiveUtProviderForTesting;
            double liveUT = liveProvider != null ? liveProvider() : CommittedFutureIndexCache.CurrentUT();
            double? cutoff;
            var unlocks = PlanResourceScanUnlocksForPatch(actions, walkCutoff, techPatchCutoff, liveUT, out cutoff);
            if (unlocks == null)
            {
                VerboseStablePatchState("patch-skip|scans|cutoff", "cutoff-unknown",
                    "PatchResourceScans: no finite cutoff and the clock is not ready - skipping");
                return;
            }

            if (unlocks.Count == 0)
            {
                VerboseStablePatchState("patch-noop|scans", "no-rows",
                    "PatchResourceScans: no committed resource scan at or before the cutoff");
                return;
            }

            if (SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("patch-skip|scans|test", "suppressed",
                    "PatchResourceScans: Unity calls suppressed for testing - skipping");
                return;
            }

            if (!IsResourceScanStateLoaded())
            {
                VerboseStablePatchState("patch-skip|scans|scenario", "scenario-null",
                    "PatchResourceScans: ResourceMap or ResourceScenario not loaded in this scene - " +
                    "skipping (the next recalc with them loaded applies it)");
                return;
            }

            var result = ApplyLiveResourceScanUnlocks(unlocks);
            string cutoffLabel = cutoff.Value.ToString("R", IC);
            if (result.Unlocked == 0)
            {
                VerboseStablePatchState("patch-noop|scans",
                    unlocks.Count.ToString(IC) + "|" + result.UnknownBody.ToString(IC),
                    $"PatchResourceScans: all {result.AlreadyUnlocked.ToString(IC)} committed scan(s) already " +
                    $"unlocked (unknownBody={result.UnknownBody.ToString(IC)}, cutoff={cutoffLabel})");
            }
            else
            {
                ParsekLog.Info(Tag,
                    $"PatchResourceScans: unlocked={result.Unlocked.ToString(IC)}, " +
                    $"alreadyUnlocked={result.AlreadyUnlocked.ToString(IC)}, " +
                    $"unknownBody={result.UnknownBody.ToString(IC)}, cutoff={cutoffLabel}, " +
                    $"unlockedIds=[{ComposeBoundedIdentitySample(result.UnlockedIds, IdentitySampleCap)}]");
            }
            if (result.UnknownBody > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchResourceScans: {result.UnknownBody.ToString(IC)} committed scan(s) name a body " +
                    $"this install does not have: [{ComposeBoundedIdentitySample(result.UnknownBodies, IdentitySampleCap)}]");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool IsResourceScanStateLoaded()
        {
            return ResourceMap.Instance != null && ResourceScenario.Instance != null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ResourceScanPatchResult ApplyLiveResourceScanUnlocks(IReadOnlyList<ResourceScanUnlock> unlocks)
        {
            var map = ResourceMap.Instance;
            return ApplyResourceScanUnlocks(unlocks,
                Patches.ResourceScanBodies.PlanetIdForName,
                map.IsPlanetScanned,
                map.IsBiomeUnlocked,
                map.UnlockPlanet,
                map.UnlockBiome);
        }

        // ---------------- part upgrades (PART-UPGRADE-PURCHASE-UNRESERVED state half) ----------------

        /// <summary>
        /// The upgrade names the patch unlocks: every effective part-upgrade row
        /// (<c>FundsSpending</c>, <c>FundsSpendingSource.PartUpgrade</c>, key = the stock
        /// <c>PARTUPGRADE</c> name in <c>DedupKey</c>) at or before <paramref name="cutoff"/>,
        /// whatever its amount. Sorted, so the apply order and its log are deterministic. Pure.
        /// </summary>
        internal static List<string> BuildUnlockedUpgradeNamesForPatch(
            IReadOnlyList<GameAction> actions, double cutoff)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null || a.Type != GameActionType.FundsSpending) continue;
                    if (a.FundsSpendingSource != FundsSpendingSource.PartUpgrade) continue;
                    if (string.IsNullOrEmpty(a.DedupKey)) continue;
                    if (a.UT > cutoff) continue;
                    names.Add(a.DedupKey);
                }
            }
            return new List<string>(names);
        }

        /// <summary>
        /// The names one <see cref="PatchUnlockedUpgrades"/> pass would unlock, over the
        /// part-purchase cutoff (<see cref="ResolvePartPurchasePatchCutoff"/>: the supplied
        /// cutoff clamped to the live clock when it is ready). Stock has no re-lock, so the
        /// clamp keeps a committed future upgrade from reaching stock before its UT, which is
        /// also when its purchase block lifts. Null when there is no usable cutoff. Pure.
        /// </summary>
        internal static List<string> PlanUnlockedUpgradeNamesForPatch(
            IReadOnlyList<GameAction> actions, double? walkCutoff, double? techPatchCutoff,
            double liveUT, out double? cutoff)
        {
            cutoff = ResolvePartPurchasePatchCutoff(walkCutoff, techPatchCutoff, liveUT);
            return cutoff.HasValue ? BuildUnlockedUpgradeNamesForPatch(actions, cutoff.Value) : null;
        }

        /// <summary>What <see cref="ApplyUpgradeUnlockPatch"/> did with one upgrade name.</summary>
        internal enum UpgradeUnlockPatchOutcome { Added, AlreadyUnlocked, UnknownUpgrade, TechNotResearched }

        /// <summary>The per-pass counters and the bounded identity lists the upgrade patch logs.</summary>
        internal sealed class UpgradeUnlockPatchResult
        {
            internal int Added;
            internal int AlreadyUnlocked;
            internal int UnknownUpgrade;
            internal int TechNotResearched;
            internal readonly List<string> AddedNames = new List<string>();
            internal readonly List<string> UnknownNames = new List<string>();
            internal readonly List<string> TechNotResearchedNames = new List<string>();
        }

        /// <summary>
        /// Pure: the outcome for one upgrade. An upgrade this install does not define is
        /// skipped; one whose tech is not researched in stock is skipped and logged, never
        /// forced (a committed purchase implies its tech is researched by then); an unlocked
        /// one is left alone; otherwise it is unlocked.
        /// </summary>
        internal static UpgradeUnlockPatchOutcome ClassifyUpgradeUnlockForPatch(
            string upgradeName,
            Func<string, bool> isKnownUpgrade,
            Func<string, bool> isTechResearched,
            Func<string, bool> isUnlocked)
        {
            if (isKnownUpgrade == null || !isKnownUpgrade(upgradeName)) return UpgradeUnlockPatchOutcome.UnknownUpgrade;
            if (isTechResearched == null || !isTechResearched(upgradeName)) return UpgradeUnlockPatchOutcome.TechNotResearched;
            if (isUnlocked != null && isUnlocked(upgradeName)) return UpgradeUnlockPatchOutcome.AlreadyUnlocked;
            return UpgradeUnlockPatchOutcome.Added;
        }

        /// <summary>
        /// The add-only core of <see cref="PatchUnlockedUpgrades"/>, over delegates so it is
        /// testable without KSP. Nothing is ever re-locked: a rewind reloads stock's unlock
        /// state from its quicksave. Running it twice unlocks nothing the second time.
        /// </summary>
        internal static UpgradeUnlockPatchResult ApplyUpgradeUnlockPatch(
            IEnumerable<string> upgradeNames,
            Func<string, bool> isKnownUpgrade,
            Func<string, bool> isTechResearched,
            Func<string, bool> isUnlocked,
            Action<string> markUnlocked)
        {
            var result = new UpgradeUnlockPatchResult();
            if (upgradeNames == null) return result;
            foreach (var name in upgradeNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                switch (ClassifyUpgradeUnlockForPatch(name, isKnownUpgrade, isTechResearched, isUnlocked))
                {
                    case UpgradeUnlockPatchOutcome.UnknownUpgrade:
                        result.UnknownUpgrade++;
                        result.UnknownNames.Add(name);
                        break;
                    case UpgradeUnlockPatchOutcome.TechNotResearched:
                        result.TechNotResearched++;
                        result.TechNotResearchedNames.Add(name);
                        break;
                    case UpgradeUnlockPatchOutcome.AlreadyUnlocked:
                        result.AlreadyUnlocked++;
                        break;
                    default:
                        markUnlocked?.Invoke(name);
                        result.Added++;
                        result.AddedNames.Add(name);
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// The state half of the part-upgrade reservation (todo PART-UPGRADE-PURCHASE-UNRESERVED):
        /// unlocks in stock's <c>PartUpgradeHandler</c> every upgrade whose committed purchase
        /// row the walk has reached, the way the stock tooltip does minus its event
        /// (<c>SetUnlocked(name, true)</c> only: firing <c>OnPartUpgradePurchased</c> would make
        /// stock's Funding charge it again). Skipped with bypass-entry-purchase on (stock
        /// unlocks every researched upgrade itself), when upgrades are off for this game, and
        /// when <c>PartUpgradeManager</c> is not loaded in this scene (it is absent from the
        /// Tracking Station; the next recalc where it is loaded applies it).
        /// </summary>
        internal static void PatchUnlockedUpgrades(
            IReadOnlyList<GameAction> actions, double? walkCutoff, double? techPatchCutoff)
        {
            if (actions == null)
            {
                VerboseStablePatchState("patch-skip|upgrades|actions", "actions-null",
                    "PatchUnlockedUpgrades: no action list supplied - skipping");
                return;
            }

            var liveProvider = UpgradeUnlockLiveUtProviderForTesting;
            double liveUT = liveProvider != null ? liveProvider() : CommittedFutureIndexCache.CurrentUT();
            double? cutoff;
            var names = PlanUnlockedUpgradeNamesForPatch(actions, walkCutoff, techPatchCutoff, liveUT, out cutoff);
            if (names == null)
            {
                VerboseStablePatchState("patch-skip|upgrades|cutoff", "cutoff-unknown",
                    "PatchUnlockedUpgrades: no finite cutoff and the clock is not ready - skipping");
                return;
            }

            if (names.Count == 0)
            {
                VerboseStablePatchState("patch-noop|upgrades", "no-rows",
                    "PatchUnlockedUpgrades: no committed part upgrade purchase at or before the cutoff");
                return;
            }

            if (SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("patch-skip|upgrades|test", "suppressed",
                    "PatchUnlockedUpgrades: Unity calls suppressed for testing - skipping");
                return;
            }

            if (GameStateRecorder.IsBypassEntryPurchaseAfterResearch())
            {
                VerboseStablePatchState("patch-skip|upgrades|bypass", "bypass-on",
                    "PatchUnlockedUpgrades: bypass-entry-purchase is on - stock unlocks researched upgrades, skipping");
                return;
            }

            string skip;
            try { skip = ReadLiveUpgradeStateSkipReason(); }
            catch (Exception ex) { skip = "upgrade state unreadable (" + ex.GetType().Name + ")"; }
            if (skip != null)
            {
                VerboseStablePatchState("patch-skip|upgrades|state", skip,
                    "PatchUnlockedUpgrades: " + skip + " - skipping (the next recalc with it loaded applies it)");
                return;
            }

            var result = ApplyLiveUpgradeUnlocks(names);
            string cutoffLabel = cutoff.Value.ToString("R", IC);
            string summary =
                $"PatchUnlockedUpgrades: targets={names.Count.ToString(IC)}, " +
                $"added={result.Added.ToString(IC)}, " +
                $"alreadyUnlocked={result.AlreadyUnlocked.ToString(IC)}, " +
                $"unknownUpgrade={result.UnknownUpgrade.ToString(IC)}, " +
                $"techNotResearched={result.TechNotResearched.ToString(IC)}, " +
                $"cutoffUT={cutoffLabel}" +
                (result.AddedNames.Count > 0
                    ? $", addedUpgrades=[{ComposeBoundedIdentitySample(result.AddedNames, IdentitySampleCap)}]"
                    : string.Empty);
            if (result.Added > 0)
                ParsekLog.Info(Tag, summary);
            else
                VerboseStablePatchState("patch-upgrades", summary, summary);

            if (result.TechNotResearched > 0)
                ParsekLog.Warn(Tag,
                    "PatchUnlockedUpgrades: committed purchase of an upgrade whose tech is not researched in stock - " +
                    $"not unlocked: [{ComposeBoundedIdentitySample(result.TechNotResearchedNames, IdentitySampleCap)}]");
            if (result.UnknownUpgrade > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchUnlockedUpgrades: {result.UnknownUpgrade.ToString(IC)} committed upgrade(s) this install " +
                    $"does not define: [{ComposeBoundedIdentitySample(result.UnknownNames, IdentitySampleCap)}]");
        }

        /// <summary>Null when the live upgrade state can be patched, else why not.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string ReadLiveUpgradeStateSkipReason()
        {
            if (PartUpgradeManager.Instance == null) return "PartUpgradeManager not loaded in this scene";
            if (!PartUpgradeManager.Handler.CanHaveUpgrades()) return "part upgrades are off for this game";
            return null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static UpgradeUnlockPatchResult ApplyLiveUpgradeUnlocks(IEnumerable<string> names)
        {
            var handler = PartUpgradeManager.Handler;
            return ApplyUpgradeUnlockPatch(names,
                n => handler.GetUpgrade(n) != null,
                n =>
                {
                    string tech = handler.GetUpgrade(n).techRequired;
                    return string.IsNullOrEmpty(tech)
                        || ResearchAndDevelopment.GetTechnologyState(tech) == RDTech.State.Available;
                },
                handler.IsUnlocked,
                n => handler.SetUnlocked(n, true));
        }

        /// <summary>
        /// The add-only live mirror for resource scans (design 3.15 step 5, prevention): the
        /// scan patch over <paramref name="scanRows"/> at <paramref name="clockUT"/>, run while
        /// the KSP patch is deferred. Returns the count unlocked.
        /// </summary>
        internal static int MirrorCommittedResourceScans(IReadOnlyList<GameAction> scanRows, double clockUT)
        {
            if (scanRows == null || scanRows.Count == 0)
                return 0;
            List<ResourceScanUnlock> unlocks = BuildResourceScanUnlocksForPatch(scanRows, clockUT);
            if (unlocks.Count == 0)
                return 0;
            if (SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("mirror-skip|scans|test", "suppressed",
                    "MirrorCommittedResourceScans: Unity calls suppressed for testing - skipping");
                return 0;
            }
            if (!IsResourceScanStateLoaded())
            {
                VerboseStablePatchState("mirror-skip|scans|scenario", "scenario-null",
                    "MirrorCommittedResourceScans: ResourceMap or ResourceScenario not loaded in this scene - skipping");
                return 0;
            }
            var result = ApplyLiveResourceScanUnlocks(unlocks);
            string line =
                $"MirrorCommittedResourceScans: unlocked={result.Unlocked.ToString(IC)}, " +
                $"alreadyUnlocked={result.AlreadyUnlocked.ToString(IC)}, unknownBody={result.UnknownBody.ToString(IC)}, " +
                $"clock={clockUT.ToString("R", IC)}, " +
                $"unlockedIds=[{ComposeBoundedIdentitySample(result.UnlockedIds, IdentitySampleCap)}]";
            if (result.Unlocked > 0)
                ParsekLog.Info(Tag, line);
            else
                VerboseStablePatchState("mirror-noop|scans",
                    unlocks.Count.ToString(IC) + "|" + result.UnknownBody.ToString(IC), line);
            return result.Unlocked;
        }
    }
}
