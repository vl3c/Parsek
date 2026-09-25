using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Contracts;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Space Center host for the stock-screen reservation annotations. R&amp;D and the
    /// Astronaut Complex are annotated by Harmony postfixes on the stock methods that build
    /// their rows (<c>StockUiRnDDecoration</c>, <c>StockUiAstronautDecoration</c>); this
    /// addon only re-runs those stock refreshes when the committed timeline changes while
    /// a screen is open, and logs the R&amp;D pass once after the tree spawns. Mission
    /// Control is annotated the same way (<c>MissionControlStockUi</c>), and so is the KSC
    /// facility context menu (<c>StockUiFacilityDecoration</c>). No screen gets a
    /// Parsek-drawn badge.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class StockUiOverlayController : MonoBehaviour
    {
        private const string Tag = "StockUiOverlay";

        private RDController currentRdController;
        private AstronautComplex currentAstronautComplex;
        private MissionControl currentMissionControl;
        private bool rdOpen;
        private bool astronautOpen;
        private bool missionOpen;

        private void Awake()
        {
            RDController.OnRDTreeSpawn.Add(OnRdTreeSpawn);
            RDController.OnRDTreeDespawn.Add(OnRdTreeDespawn);
            GameEvents.onGUIAstronautComplexSpawn.Add(OnAstronautComplexSpawn);
            GameEvents.onGUIAstronautComplexDespawn.Add(OnAstronautComplexDespawn);
            GameEvents.onGUIMissionControlSpawn.Add(OnMissionControlSpawn);
            GameEvents.onGUIMissionControlDespawn.Add(OnMissionControlDespawn);
            GameEvents.onFacilityContextMenuSpawn.Add(OnFacilityMenuSpawn);
            GameEvents.onFacilityContextMenuDespawn.Add(OnFacilityMenuDespawn);
            LedgerOrchestrator.OnTimelineDataChanged += OnTimelineDataChanged;

            ParsekLog.Info(Tag,
                "StockUiOverlay: initialised, listening for R&D / Astronaut / MissionControl / facility menu spawns + LedgerOrchestrator.OnTimelineDataChanged");
        }

        private void OnDestroy()
        {
            RDController.OnRDTreeSpawn.Remove(OnRdTreeSpawn);
            RDController.OnRDTreeDespawn.Remove(OnRdTreeDespawn);
            GameEvents.onGUIAstronautComplexSpawn.Remove(OnAstronautComplexSpawn);
            GameEvents.onGUIAstronautComplexDespawn.Remove(OnAstronautComplexDespawn);
            GameEvents.onGUIMissionControlSpawn.Remove(OnMissionControlSpawn);
            GameEvents.onGUIMissionControlDespawn.Remove(OnMissionControlDespawn);
            GameEvents.onFacilityContextMenuSpawn.Remove(OnFacilityMenuSpawn);
            GameEvents.onFacilityContextMenuDespawn.Remove(OnFacilityMenuDespawn);
            LedgerOrchestrator.OnTimelineDataChanged -= OnTimelineDataChanged;
        }

        private void OnTimelineDataChanged()
        {
            CommittedFutureIndexCache.Invalidate("timeline changed");
            StockUiLiveSnapshot.Invalidate();
            ScheduleRebuildAllVisible("timeline changed");
        }

        private void ScheduleRebuildAllVisible(string reason)
        {
            string screens = DescribeOpenScreens();
            if (string.IsNullOrEmpty(screens))
            {
                ParsekLog.Verbose(Tag,
                    $"StockUiOverlay: {reason} but no tracked screen open - RebuildAllVisible no-op");
                return;
            }

            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: {reason} - scheduling RebuildAllVisible for {screens}");
            StartCoroutine(RebuildAllVisibleNextFrame());
        }

        private IEnumerator RebuildAllVisibleNextFrame()
        {
            yield return null;
            RebuildAllVisible();
        }

        private void RebuildAllVisible()
        {
            StockUiLiveSnapshot.Invalidate();
            if (rdOpen)
                RefreshRnD(currentRdController ?? RDController.Instance);
            if (astronautOpen)
                RefreshAstronaut(currentAstronautComplex ?? UnityEngine.Object.FindObjectOfType<AstronautComplex>());
            if (missionOpen)
                MissionControlStockUi.RefreshOpenScreen(
                    currentMissionControl ?? MissionControl.Instance ?? UnityEngine.Object.FindObjectOfType<MissionControl>(),
                    "timeline changed");
            StockUiFacilityDecoration.RefreshOpenMenus("timeline changed");
        }

        private string DescribeOpenScreens()
        {
            var open = new List<string>();
            if (rdOpen) open.Add("R&D");
            if (astronautOpen) open.Add("Astronaut");
            if (missionOpen) open.Add("MissionControl");
            if (StockUiFacilityDecoration.OpenMenuCount > 0) open.Add("FacilityMenu");
            return string.Join(", ", open.ToArray());
        }

        private void OnRdTreeSpawn(RDController controller)
        {
            rdOpen = true;
            currentRdController = controller ?? RDController.Instance;
            StartCoroutine(LogRnDPassAfterNodesUpdate());
        }

        /// <summary>
        /// Each node runs its first <c>UpdateGraphics</c> one frame after it registers, so
        /// the spawn pass log waits two frames and then records what the tree shows.
        /// </summary>
        private IEnumerator LogRnDPassAfterNodesUpdate()
        {
            yield return null;
            yield return null;
            if (rdOpen)
                StockUiRnDDecoration.LogPass(currentRdController ?? RDController.Instance, "tree spawn");
        }

        private void OnRdTreeDespawn(RDController controller)
        {
            ParsekLog.Verbose(Tag, "StockUiOverlay: R&D despawn");
            rdOpen = false;
            currentRdController = null;
        }

        /// <summary>
        /// Re-runs stock's own tree refresh (every node's <c>UpdateGraphics</c>, then the
        /// panel), so the annotation postfixes re-apply against the new timeline.
        /// </summary>
        private static void RefreshRnD(RDController controller)
        {
            if (controller == null || controller.techTree == null)
            {
                ParsekLog.Verbose(Tag, "StockUiOverlay: R&D refresh skipped - no controller or tech tree");
                return;
            }
            try
            {
                controller.techTree.RefreshUI();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "rnd-refresh-failed",
                    "StockUiOverlay: R&D refresh after a timeline change failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        private void OnAstronautComplexSpawn()
        {
            astronautOpen = true;
            currentAstronautComplex = UnityEngine.Object.FindObjectOfType<AstronautComplex>();
            ParsekLog.Verbose(Tag, "StockUiOverlay: Astronaut Complex spawn");
        }

        private void OnAstronautComplexDespawn()
        {
            ParsekLog.Verbose(Tag, "StockUiOverlay: Astronaut Complex despawn");
            astronautOpen = false;
            currentAstronautComplex = null;
        }

        private static MethodInfo updateCrewCountsMethod;
        private static bool updateCrewCountsWarned;

        /// <summary>
        /// Re-runs stock's private <c>AstronautComplex.UpdateCrewCounts</c>, which re-sets
        /// every applicant's lock; its postfix re-decorates every row.
        /// </summary>
        internal static void RefreshAstronaut(AstronautComplex complex)
        {
            if (complex == null)
            {
                ParsekLog.Verbose(Tag, "StockUiOverlay: Astronaut Complex refresh skipped - no complex");
                return;
            }
            if (updateCrewCountsMethod == null)
                updateCrewCountsMethod = typeof(AstronautComplex).GetMethod("UpdateCrewCounts",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (updateCrewCountsMethod == null)
            {
                if (!updateCrewCountsWarned)
                {
                    updateCrewCountsWarned = true;
                    ParsekLog.Warn(Tag,
                        "StockUiOverlay: AstronautComplex.UpdateCrewCounts not found - rows re-annotate only when stock rebuilds them");
                }
                StockUiAstronautDecoration.DecorateAllRows(complex, "timeline changed");
                return;
            }
            try
            {
                updateCrewCountsMethod.Invoke(complex, null);
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                ParsekLog.WarnRateLimited(Tag, "ac-refresh-failed",
                    "StockUiOverlay: Astronaut Complex refresh after a timeline change failed (" + inner.GetType().Name + ": " + inner.Message + ")");
            }
        }

        // Mission Control is annotated through stock mechanisms by the Harmony patches in
        // Patches/MissionControlStockUiPatches.cs, which run on every row rebuild and panel
        // fill. This controller only tracks that the screen is open, so a committed-timeline
        // change can re-apply the annotations to the rows already on screen.
        private void OnMissionControlSpawn()
        {
            missionOpen = true;
            currentMissionControl = MissionControl.Instance ?? UnityEngine.Object.FindObjectOfType<MissionControl>();
            MissionControlStockUi.OnScreenOpened();
        }

        // The facility menu is decorated by FacilityMenuUpgradeBlockPatch on every stock
        // button fill; the spawn event fires before that fill, so here it is only tracked.
        private void OnFacilityMenuSpawn(KSCFacilityContextMenu menu)
        {
            StockUiFacilityDecoration.OnMenuSpawned(menu);
            ParsekLog.Verbose(Tag, "StockUiOverlay: facility menu spawn (" + (menu != null ? menu.name : "null") + ")");
        }

        private void OnFacilityMenuDespawn(KSCFacilityContextMenu menu)
        {
            StockUiFacilityDecoration.OnMenuDespawned(menu);
            ParsekLog.Verbose(Tag, "StockUiOverlay: facility menu despawn");
        }

        private void OnMissionControlDespawn()
        {
            MissionControlStockUi.OnScreenClosed(currentMissionControl);
            missionOpen = false;
            currentMissionControl = null;
        }

        /// <summary>
        /// The live lookups the Astronaut Complex decoration and the dismissal refusal
        /// read: the ledger's kerbal reservations, slots and dismissal predicate, and
        /// whether a committed flight's chain loops.
        /// </summary>
        internal static AstronautComplexContext BuildLiveAstronautContext(ISet<string> liveCrewOrTourist)
        {
            return new AstronautComplexContext
            {
                ReservationKind = ResolveReservationKind,
                Reservation = ResolveReservation,
                SlotOwner = ResolveReservationSlotOwner,
                DismissalRefusal = name => Patches.KerbalDismissalPatch.DescribeDismissalRefusal(LedgerOrchestrator.Kerbals, name),
                ActiveStandInOwner = name => LedgerOrchestrator.Kerbals?.FindActiveStandInOwner(name),
                SeatSharedOwner = StandInSeatCount.LiveSeatSharedOwner,
                IsLoopingRecording = IsRecordingInLoopingChain,
                LiveCrewOrTourist = liveCrewOrTourist
            };
        }

        /// <summary>Names in the live Crew and Tourist lists (a committed future hire of one
        /// of them is moot, so it is not marked).</summary>
        internal static HashSet<string> CollectActiveCrewOrTouristNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
                return names;

            AddRosterNames(names, roster.Crew);
            AddRosterNames(names, roster.Tourist);
            return names;
        }

        private static void AddRosterNames(HashSet<string> names, IEnumerable<ProtoCrewMember> crew)
        {
            if (crew == null)
                return;

            foreach (ProtoCrewMember member in crew)
            {
                if (member != null && !string.IsNullOrEmpty(member.name))
                    names.Add(member.name);
            }
        }

        private static KerbalReservationKind ResolveReservationKind(string name)
        {
            return LedgerOrchestrator.Kerbals?.GetReservationKind(name)
                ?? KerbalReservationKind.NotManaged;
        }

        private static KerbalsModule.KerbalReservation ResolveReservation(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var reservations = LedgerOrchestrator.Kerbals?.Reservations;
            KerbalsModule.KerbalReservation reservation;
            return reservations != null && reservations.TryGetValue(name, out reservation)
                ? reservation
                : null;
        }

        /// <summary>
        /// True when the committed recording, or any committed recording of its chain,
        /// plays as a loop: the case KerbalsModule holds a chain's crew open-ended for.
        /// </summary>
        internal static bool IsRecordingInLoopingChain(string recordingId)
        {
            Recording rec = LedgerOrchestrator.FindRecordingById(recordingId);
            if (rec == null) return false;
            if (rec.LoopPlayback) return true;
            if (string.IsNullOrEmpty(rec.ChainId)) return false;
            var ers = EffectiveState.ComputeERS();
            for (int i = 0; i < ers.Count; i++)
            {
                var other = ers[i];
                if (other != null && other.LoopPlayback
                    && string.Equals(other.ChainId, rec.ChainId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string ResolveReservationSlotOwner(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var kerbals = LedgerOrchestrator.Kerbals;
            var slots = kerbals != null ? kerbals.Slots : null;
            if (slots == null)
                return null;

            if (slots.ContainsKey(name))
                return name;

            foreach (var slot in slots.Values)
            {
                if (slot == null || string.IsNullOrEmpty(slot.OwnerName) || slot.Chain == null)
                    continue;

                for (int i = 0; i < slot.Chain.Count; i++)
                {
                    if (string.Equals(slot.Chain[i], name, StringComparison.Ordinal))
                        return slot.OwnerName;
                }
            }

            return null;
        }
    }
}
