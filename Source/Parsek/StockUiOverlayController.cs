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
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class StockUiOverlayController : MonoBehaviour
    {
        private const string Tag = "StockUiOverlay";
        private const string TechOverlayName = "Parsek_TechOverlay";
        private const string KerbalOverlayName = "Parsek_KerbalOverlay";
        private const string ContractOverlayName = "Parsek_ContractOverlay";

        private static bool rdNodesWarned;
        private static bool astronautListsWarned;
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
            if (rdOpen)
                DecorateRnD(currentRdController ?? RDController.Instance);
            if (astronautOpen)
                DecorateAstronaut(currentAstronautComplex ?? UnityEngine.Object.FindObjectOfType<AstronautComplex>());
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
            DecorateRnD(currentRdController);
        }

        private void OnRdTreeDespawn(RDController controller)
        {
            Transform root = (controller ?? currentRdController)?.transform;
            int stripped = StripOverlays(root, TechOverlayName);
            ParsekLog.Verbose(Tag, $"StockUiOverlay: R&D despawn - stripped overlayCount={stripped}");
            rdOpen = false;
            currentRdController = null;
        }

        private void OnAstronautComplexSpawn()
        {
            astronautOpen = true;
            currentAstronautComplex = UnityEngine.Object.FindObjectOfType<AstronautComplex>();
            StartCoroutine(DecorateAstronautNextFrame());
        }

        private IEnumerator DecorateAstronautNextFrame()
        {
            yield return null;
            DecorateAstronaut(currentAstronautComplex ?? UnityEngine.Object.FindObjectOfType<AstronautComplex>());
        }

        private void OnAstronautComplexDespawn()
        {
            int stripped = StripOverlays(currentAstronautComplex != null ? currentAstronautComplex.transform : null, KerbalOverlayName);
            ParsekLog.Verbose(Tag, $"StockUiOverlay: Astronaut despawn - stripped overlayCount={stripped}");
            astronautOpen = false;
            currentAstronautComplex = null;
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

        private void DecorateRnD(RDController controller)
        {
            if (controller == null)
                return;

            StripOverlays(controller.transform, TechOverlayName);

            List<RDNode> nodes;
            if (!TryGetRdNodes(controller, out nodes))
                return;

            var nodeById = new Dictionary<string, RDNode>(StringComparer.Ordinal);
            var techIds = new List<string>();
            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    RDNode node = nodes[i];
                    if (node == null || node.tech == null || string.IsNullOrEmpty(node.tech.techID))
                        continue;
                    if (nodeById.ContainsKey(node.tech.techID))
                        continue;
                    nodeById[node.tech.techID] = node;
                    techIds.Add(node.tech.techID);
                }
            }

            var decorations = StockUiDecorationQuery.ForRnD(
                CommittedFutureIndexCache.Current,
                CommittedFutureIndexCache.CurrentUT(),
                techIds,
                ReservationExplanation.DefaultDateFormatter);
            StockUiDecorationQuery.LogPass(StockUiScreen.RnD,
                new[] { StockUiDecorationQuery.RnDTab }, decorations);

            for (int i = 0; i < decorations.Count; i++)
            {
                var d = decorations[i];
                RDNode node;
                if (!d.Marked || !nodeById.TryGetValue(d.Id, out node))
                    continue;
                AttachBadge(node.transform, TechOverlayName, "R&D", d.Id, d.Why,
                    new Color(1.0f, 0.84f, 0.22f, 0.95f));
            }
        }

        private void DecorateAstronaut(AstronautComplex complex)
        {
            if (complex == null)
                return;

            StripOverlays(complex.transform, KerbalOverlayName);

            List<KeyValuePair<string, Transform>> listRoots;
            if (!TryGetAstronautListRoots(complex, out listRoots))
                return;

            var rosterNames = CollectRosterNames();
            var rows = new List<StockUiItem>();
            var itemsByName = new Dictionary<string, List<CrewListItem>>(StringComparer.Ordinal);
            var seenItems = new HashSet<CrewListItem>();
            var seenRows = new HashSet<string>(StringComparer.Ordinal);
            int unknownNames = 0;
            for (int r = 0; r < listRoots.Count; r++)
            {
                Transform root = listRoots[r].Value;
                if (root == null)
                    continue;
                string tab = listRoots[r].Key;
                CrewListItem[] items = root.GetComponentsInChildren<CrewListItem>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    CrewListItem item = items[i];
                    if (item == null || !seenItems.Add(item))
                        continue;
                    string name = SafeGetCrewListItemName(item);
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (!rosterNames.Contains(name))
                    {
                        unknownNames++;
                        continue;
                    }

                    List<CrewListItem> forName;
                    if (!itemsByName.TryGetValue(name, out forName))
                    {
                        forName = new List<CrewListItem>();
                        itemsByName[name] = forName;
                    }
                    forName.Add(item);
                    if (seenRows.Add(tab + "|" + name))
                        rows.Add(new StockUiItem(name, tab));
                }
            }

            if (unknownNames > 0)
                ParsekLog.VerboseRateLimited(Tag, "applicant-row-names-not-in-roster",
                    $"StockUiOverlay: {unknownNames} Astronaut Complex row name(s) not found in CrewRoster - overlay skipped for them");

            var decorations = StockUiDecorationQuery.ForAstronautComplex(
                CommittedFutureIndexCache.Current,
                CommittedFutureIndexCache.CurrentUT(),
                rows,
                BuildLiveAstronautContext(CollectActiveCrewOrTouristNames()),
                ReservationExplanation.DefaultDateFormatter);
            StockUiDecorationQuery.LogPass(StockUiScreen.AstronautComplex,
                AstronautTabs, decorations);

            var badged = new HashSet<CrewListItem>();
            for (int i = 0; i < decorations.Count; i++)
            {
                var d = decorations[i];
                List<CrewListItem> forName;
                if (!d.Marked || !itemsByName.TryGetValue(d.Id, out forName))
                    continue;
                for (int j = 0; j < forName.Count; j++)
                {
                    if (!badged.Add(forName[j]))
                        continue;
                    AttachBadge(forName[j].transform, KerbalOverlayName, "Astronaut", d.Id, d.Why,
                        ColorForKerbalKind(d.Kind));
                }
            }
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

        private static readonly string[] AstronautTabs =
        {
            StockUiDecorationQuery.AstronautApplicantsTab,
            StockUiDecorationQuery.AstronautAvailableTab,
            StockUiDecorationQuery.AstronautAssignedTab,
            StockUiDecorationQuery.AstronautKiaTab
        };

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

        private static bool TryGetRdNodes(RDController controller, out List<RDNode> nodes)
        {
            nodes = null;
            try
            {
                FieldInfo field = typeof(RDController).GetField("nodes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    throw new MissingFieldException(typeof(RDController).FullName, "nodes");
                nodes = field.GetValue(controller) as List<RDNode>;
                if (nodes == null)
                    throw new InvalidCastException("RDController.nodes was null or not List<RDNode>");
                return true;
            }
            catch (Exception ex)
            {
                if (!rdNodesWarned)
                {
                    rdNodesWarned = true;
                    ParsekLog.Warn(Tag,
                        $"StockUiOverlay: RDController.nodes reflection failed - tech overlays disabled this session ({ex.Message})");
                }

                return false;
            }
        }

        private static bool TryGetAstronautListRoots(
            AstronautComplex complex, out List<KeyValuePair<string, Transform>> roots)
        {
            roots = new List<KeyValuePair<string, Transform>>();
            string[] fieldNames =
            {
                "scrollListApplicants",
                "scrollListAvailable",
                "scrollListAssigned",
                "scrollListKia"
            };

            try
            {
                for (int i = 0; i < fieldNames.Length; i++)
                {
                    FieldInfo field = typeof(AstronautComplex).GetField(
                        fieldNames[i],
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null)
                        throw new MissingFieldException(typeof(AstronautComplex).FullName, fieldNames[i]);

                    var component = field.GetValue(complex) as Component;
                    if (component != null)
                        roots.Add(new KeyValuePair<string, Transform>(AstronautTabs[i], component.transform));
                }

                if (roots.Count == 0)
                    throw new InvalidOperationException("no AstronautComplex UIList roots resolved");
                return true;
            }
            catch (Exception ex)
            {
                if (!astronautListsWarned)
                {
                    astronautListsWarned = true;
                    ParsekLog.Warn(Tag,
                        $"StockUiOverlay: AstronautComplex list-field reflection failed - applicant overlays disabled this session ({ex.Message})");
                }

                return false;
            }
        }

        private static HashSet<string> CollectRosterNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
                return names;

            AddRosterNames(names, roster.Crew);
            AddRosterNames(names, roster.Applicants);
            AddRosterNames(names, roster.Tourist);
            return names;
        }

        private static HashSet<string> CollectActiveCrewOrTouristNames()
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

        private static string SafeGetCrewListItemName(CrewListItem item)
        {
            if (item == null)
                return null;

            try
            {
                return item.GetName();
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "crew-list-get-name-failed",
                    $"StockUiOverlay: CrewListItem.GetName() failed - overlay skipped ({ex.Message})");
                return null;
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

        private static Color ColorForKerbalKind(StockUiDecorationKind kind)
        {
            switch (kind)
            {
                case StockUiDecorationKind.KerbalHire:
                    return new Color(0.33f, 0.86f, 0.48f, 0.95f);
                case StockUiDecorationKind.KerbalRetire:
                    return new Color(0.62f, 0.62f, 0.62f, 0.95f);
                case StockUiDecorationKind.KerbalOnFlight:
                case StockUiDecorationKind.KerbalLost:
                    return new Color(1.0f, 0.70f, 0.28f, 0.95f);
                case StockUiDecorationKind.KerbalRetiredStandIn:
                    return new Color(0.78f, 0.48f, 0.95f, 0.95f);
                default:
                    return Color.white;
            }
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
