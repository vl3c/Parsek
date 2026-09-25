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
    /// Control is still badged here.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class StockUiOverlayController : MonoBehaviour
    {
        private const string Tag = "StockUiOverlay";
        private const string ContractOverlayName = "Parsek_ContractOverlay";

        private static bool missionRowsWarned;

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
            LedgerOrchestrator.OnTimelineDataChanged += OnTimelineDataChanged;

            ParsekLog.Info(Tag,
                "StockUiOverlay: initialised, listening for R&D / Astronaut / MissionControl spawns + LedgerOrchestrator.OnTimelineDataChanged");
        }

        private void OnDestroy()
        {
            RDController.OnRDTreeSpawn.Remove(OnRdTreeSpawn);
            RDController.OnRDTreeDespawn.Remove(OnRdTreeDespawn);
            GameEvents.onGUIAstronautComplexSpawn.Remove(OnAstronautComplexSpawn);
            GameEvents.onGUIAstronautComplexDespawn.Remove(OnAstronautComplexDespawn);
            GameEvents.onGUIMissionControlSpawn.Remove(OnMissionControlSpawn);
            GameEvents.onGUIMissionControlDespawn.Remove(OnMissionControlDespawn);
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
                DecorateMissionControl(currentMissionControl ?? UnityEngine.Object.FindObjectOfType<MissionControl>());
        }

        private string DescribeOpenScreens()
        {
            if (rdOpen)
            {
                if (astronautOpen)
                    return missionOpen ? "R&D, Astronaut, MissionControl" : "R&D, Astronaut";

                return missionOpen ? "R&D, MissionControl" : "R&D";
            }

            if (astronautOpen)
                return missionOpen ? "Astronaut, MissionControl" : "Astronaut";

            return missionOpen ? "MissionControl" : "";
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

        private void OnMissionControlSpawn()
        {
            missionOpen = true;
            currentMissionControl = UnityEngine.Object.FindObjectOfType<MissionControl>();
            StartCoroutine(DecorateMissionControlNextFrame());
        }

        private IEnumerator DecorateMissionControlNextFrame()
        {
            yield return null;
            DecorateMissionControl(currentMissionControl ?? UnityEngine.Object.FindObjectOfType<MissionControl>());
        }

        private void OnMissionControlDespawn()
        {
            int stripped = StripOverlays(currentMissionControl != null ? currentMissionControl.transform : null, ContractOverlayName);
            ParsekLog.Verbose(Tag, $"StockUiOverlay: MissionControl despawn - stripped overlayCount={stripped}");
            missionOpen = false;
            currentMissionControl = null;
        }

        private void DecorateMissionControl(MissionControl missionControl)
        {
            if (missionControl == null)
                return;

            StripOverlays(missionControl.transform, ContractOverlayName);

            MCListItem[] rowItems = missionControl.GetComponentsInChildren<MCListItem>(true);
            var rows = new List<StockUiItem>();
            var rowsByKey = new Dictionary<string, List<MCListItem>>(StringComparer.Ordinal);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rowItems.Length; i++)
            {
                MCListItem row = rowItems[i];
                if (row == null)
                    continue;

                Contract contract;
                if (!TryGetMissionControlRowContract(row, out contract))
                    continue;

                string key = contract.ContractGuid.ToString();
                List<MCListItem> forKey;
                if (!rowsByKey.TryGetValue(key, out forKey))
                {
                    forKey = new List<MCListItem>();
                    rowsByKey[key] = forKey;
                }
                forKey.Add(row);
                if (seenKeys.Add(key))
                    rows.Add(new StockUiItem(key, MissionControlTabFor(contract.ContractState)));
            }

            var decorations = StockUiDecorationQuery.ForMissionControl(
                CommittedFutureIndexCache.Current,
                CommittedFutureIndexCache.CurrentUT(),
                rows,
                ReservationExplanation.DefaultDateFormatter);
            StockUiDecorationQuery.LogPass(StockUiScreen.MissionControl,
                new[] { StockUiDecorationQuery.MissionControlAvailableTab }, decorations);

            for (int i = 0; i < decorations.Count; i++)
            {
                var d = decorations[i];
                List<MCListItem> forKey;
                if (!d.Marked || !rowsByKey.TryGetValue(d.Id, out forKey))
                    continue;
                for (int j = 0; j < forKey.Count; j++)
                    AttachBadge(forKey[j].transform, ContractOverlayName, "MissionControl", d.Id, d.Why,
                        new Color(0.35f, 0.74f, 1.0f, 0.95f));
            }
        }

        /// <summary>The Mission Control tab a contract's row belongs to.</summary>
        internal static string MissionControlTabFor(Contract.State state)
        {
            switch (state)
            {
                case Contract.State.Offered:
                    return StockUiDecorationQuery.MissionControlAvailableTab;
                case Contract.State.Active:
                    return StockUiDecorationQuery.MissionControlActiveTab;
                default:
                    return StockUiDecorationQuery.MissionControlArchiveTab;
            }
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
                DismissalBlocked = name => LedgerOrchestrator.Kerbals?.ShouldBlockDismissal(name) ?? false,
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

        /// <summary>
        /// The contract behind a Mission Control list row. Stock (KSP 1.12.5,
        /// <c>MissionControl.AddItem</c>) stores a <c>MissionControl.MissionSelection</c>
        /// wrapper in <c>UIListItem.Data</c>, whose <c>contract</c> field is the row's
        /// contract; a bare <c>Contract</c> payload is accepted too so a build that stores
        /// the contract directly keeps working. Shared with the in-game overlay cells so
        /// the test reads rows exactly as the overlay does.
        /// </summary>
        internal static Contract ExtractMissionControlRowContract(MCListItem row)
        {
            object data = row != null && row.container != null ? row.container.Data : null;
            if (data == null)
                return null;
            if (data is MissionControl.MissionSelection selection)
                return selection.contract;
            return data as Contract;
        }

        private static bool TryGetMissionControlRowContract(MCListItem row, out Contract contract)
        {
            contract = null;
            if (row == null)
                return false;

            try
            {
                contract = ExtractMissionControlRowContract(row);
                if (contract != null)
                    return true;

                if (!missionRowsWarned)
                {
                    missionRowsWarned = true;
                    ParsekLog.Warn(Tag,
                        "StockUiOverlay: MissionControl row contract lookup failed - contract overlays disabled for rows whose UIListItem.Data is neither a MissionSelection nor a Contract");
                }
            }
            catch (Exception ex)
            {
                if (!missionRowsWarned)
                {
                    missionRowsWarned = true;
                    ParsekLog.Warn(Tag,
                        $"StockUiOverlay: MissionControl row contract lookup failed - contract overlays disabled for rows without UIListItem.Data Contract ({ex.Message})");
                }
            }

            return false;
        }

        private static void AttachBadge(
            Transform parent,
            string objectName,
            string screen,
            string itemName,
            string tooltip,
            Color color)
        {
            if (parent == null)
                return;

            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            var badge = go.AddComponent<OverlayBadge>();
            badge.Configure(screen, itemName, tooltip, color);
        }

        private static int StripOverlays(Transform root, string overlayName)
        {
            if (root == null)
                return 0;

            int stripped = 0;
            var toDestroy = new List<GameObject>();
            CollectOverlayChildren(root, overlayName, toDestroy);
            for (int i = 0; i < toDestroy.Count; i++)
            {
                if (toDestroy[i] == null)
                    continue;
                UnityEngine.Object.Destroy(toDestroy[i]);
                stripped++;
            }

            return stripped;
        }

        private static void CollectOverlayChildren(Transform root, string overlayName, List<GameObject> result)
        {
            if (root == null)
                return;

            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child == null)
                    continue;
                if (child.gameObject != null && string.Equals(child.gameObject.name, overlayName, StringComparison.Ordinal))
                    result.Add(child.gameObject);
                else
                    CollectOverlayChildren(child, overlayName, result);
            }
        }
    }
}
