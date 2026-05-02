using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Contracts;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    internal enum ApplicantOverlayKind
    {
        FutureHired,
        FutureRetired,
        ReservedActive,
        ReservedRetired
    }

    internal struct TechNodeOverlayMark
    {
        public string TechId;
        public double UT;
        public string RecordingId;
        public int AdditionalCommittedCount;
        public string Tooltip;
    }

    internal struct ContractOverlayMark
    {
        public string ContractKey;
        public double UT;
        public string RecordingId;
        public int AdditionalCommittedCount;
        public string Tooltip;
    }

    internal struct ApplicantOverlayMark
    {
        public string KerbalName;
        public ApplicantOverlayKind Kind;
        public double UT;
        public string RecordingId;
        public int AdditionalCommittedCount;
        public string Tooltip;
    }

    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public sealed class StockUiOverlayController : MonoBehaviour
    {
        private const string Tag = "StockUiOverlay";
        private const string TechOverlayName = "Parsek_TechOverlay";
        private const string KerbalOverlayName = "Parsek_KerbalOverlay";
        private const string ContractOverlayName = "Parsek_ContractOverlay";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
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
            string screens = DescribeOpenScreens();
            if (string.IsNullOrEmpty(screens))
            {
                ParsekLog.Verbose(Tag,
                    "StockUiOverlay: timeline changed but no tracked screen open — RebuildAllVisible no-op");
                return;
            }

            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: timeline changed — scheduling RebuildAllVisible for {screens}");
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
            var screens = new List<string>();
            if (rdOpen) screens.Add("R&D");
            if (astronautOpen) screens.Add("Astronaut");
            if (missionOpen) screens.Add("MissionControl");
            return string.Join(", ", screens.ToArray());
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
            ParsekLog.Verbose(Tag, $"StockUiOverlay: R&D despawn — stripped overlayCount={stripped}");
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
            ParsekLog.Verbose(Tag, $"StockUiOverlay: Astronaut despawn — stripped overlayCount={stripped}");
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
            ParsekLog.Verbose(Tag, $"StockUiOverlay: MissionControl despawn — stripped overlayCount={stripped}");
            missionOpen = false;
            currentMissionControl = null;
        }

        private void DecorateRnD(RDController controller)
        {
            if (controller == null)
                return;

            StripOverlays(controller.transform, TechOverlayName);
            if (!ShouldApplyOverlays())
            {
                ParsekLog.Info(Tag, "StockUiOverlay: feature disabled by ParsekSettings — no decorations applied");
                return;
            }

            var marks = BuildTechMarks();
            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: R&D spawn — building tech marks committedTechCount={marks.Count}");

            List<RDNode> nodes;
            if (!TryGetRdNodes(controller, out nodes))
                return;

            int decorated = 0;
            int total = nodes != null ? nodes.Count : 0;
            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    RDNode node = nodes[i];
                    if (node == null || node.tech == null || string.IsNullOrEmpty(node.tech.techID))
                        continue;

                    TechNodeOverlayMark mark;
                    if (!marks.TryGetValue(node.tech.techID, out mark))
                        continue;

                    AttachBadge(node.transform, TechOverlayName, "R&D", mark.TechId, mark.Tooltip,
                        new Color(1.0f, 0.84f, 0.22f, 0.95f));
                    decorated++;
                }
            }

            ParsekLog.Info(Tag,
                $"StockUiOverlay: R&D decorated nodeCount={decorated} of total={total}");
        }

        private void DecorateAstronaut(AstronautComplex complex)
        {
            if (complex == null)
                return;

            StripOverlays(complex.transform, KerbalOverlayName);
            if (!ShouldApplyOverlays())
            {
                ParsekLog.Info(Tag, "StockUiOverlay: feature disabled by ParsekSettings — no decorations applied");
                return;
            }

            List<Transform> listRoots;
            if (!TryGetAstronautListRoots(complex, out listRoots))
                return;

            var rosterNames = CollectRosterNames();
            var rowItems = CollectCrewListItems(listRoots);
            var candidateNames = new HashSet<string>(rosterNames, StringComparer.Ordinal);
            for (int i = 0; i < rowItems.Count; i++)
            {
                string rowName = SafeGetCrewListItemName(rowItems[i]);
                if (!string.IsNullOrEmpty(rowName))
                    candidateNames.Add(rowName);
            }

            var marks = BuildApplicantMarks(candidateNames, ResolveReservationKind, ResolveReservationSlotOwner);
            marks = SuppressFutureHiredApplicantsAlreadyInLiveRoster(marks, CollectActiveCrewOrTouristNames());
            int reservedActive = CountApplicantMarks(marks, ApplicantOverlayKind.ReservedActive);
            int reservedRetired = CountApplicantMarks(marks, ApplicantOverlayKind.ReservedRetired);
            int futureHired = CountApplicantMarks(marks, ApplicantOverlayKind.FutureHired);
            int futureRetired = CountApplicantMarks(marks, ApplicantOverlayKind.FutureRetired);

            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: Astronaut spawn — building applicant marks reservedActive={reservedActive} reservedRetired={reservedRetired} futureHired={futureHired} futureRetired={futureRetired}");

            int decorated = 0;
            for (int i = 0; i < rowItems.Count; i++)
            {
                CrewListItem item = rowItems[i];
                string name = SafeGetCrewListItemName(item);
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!rosterNames.Contains(name))
                {
                    ParsekLog.VerboseRateLimited(Tag, "applicant-row-name-" + name,
                        $"StockUiOverlay: applicant row name '{name}' not found in CrewRoster — overlay skipped");
                    continue;
                }

                ApplicantOverlayMark mark;
                if (!marks.TryGetValue(name, out mark))
                    continue;

                AttachBadge(item.transform, KerbalOverlayName, "Astronaut", mark.KerbalName, mark.Tooltip,
                    ColorForApplicantKind(mark.Kind));
                decorated++;

                string ut = mark.UT >= 0.0
                    ? mark.UT.ToString("F0", IC)
                    : "n/a";
                ParsekLog.VerboseRateLimited(Tag, "applicant-decorated-" + name,
                    $"StockUiOverlay: applicant decorated name={name} kind={mark.Kind} ut={ut}");
            }

            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: Astronaut decorated applicantCount={decorated}");
        }

        private void DecorateMissionControl(MissionControl missionControl)
        {
            if (missionControl == null)
                return;

            StripOverlays(missionControl.transform, ContractOverlayName);
            if (!ShouldApplyOverlays())
            {
                ParsekLog.Info(Tag, "StockUiOverlay: feature disabled by ParsekSettings — no decorations applied");
                return;
            }

            var marks = BuildContractMarks();
            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: MissionControl spawn — building contract marks futureAcceptedCount={marks.Count}");

            MCListItem[] rows = missionControl.GetComponentsInChildren<MCListItem>(true);
            var visibleKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Length; i++)
            {
                MCListItem row = rows[i];
                if (row == null)
                    continue;

                Contract contract;
                if (!TryGetMissionControlRowContract(row, out contract))
                    continue;

                string key = contract.ContractGuid.ToString();
                visibleKeys.Add(key);
            }

            var visibleMarks = FilterContractMarksToVisibleContracts(marks, visibleKeys);
            visibleMarks = SuppressAlreadyActiveContractMarks(visibleMarks, CollectActiveContractKeys());

            int decorated = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                MCListItem row = rows[i];
                if (row == null)
                    continue;

                Contract contract;
                if (!TryGetMissionControlRowContract(row, out contract))
                    continue;

                string key = contract.ContractGuid.ToString();
                ContractOverlayMark mark;
                if (!visibleMarks.TryGetValue(key, out mark))
                    continue;

                AttachBadge(row.transform, ContractOverlayName, "MissionControl", mark.ContractKey, mark.Tooltip,
                    new Color(0.35f, 0.74f, 1.0f, 0.95f));
                decorated++;
            }

            ParsekLog.Verbose(Tag,
                $"StockUiOverlay: MissionControl decorated contractCount={decorated}");
        }

        internal static bool ShouldApplyOverlays()
        {
            return ParsekSettings.Current?.showCommittedFutureOverlays ?? true;
        }

        internal static Dictionary<string, TechNodeOverlayMark> BuildTechMarks()
        {
            return BuildTechMarks(MilestoneStore.GetCommittedTechIds());
        }

        // Test-only shim for overlay/click-block predicate parity.
        internal static Dictionary<string, TechNodeOverlayMark> BuildTechMarks_Candidates()
        {
            return BuildTechMarks();
        }

        internal static Dictionary<string, TechNodeOverlayMark> BuildTechMarks(HashSet<string> committedTechIds)
        {
            var marks = BuildEventMarks(committedTechIds, GameStateEventType.TechResearched);
            var result = new Dictionary<string, TechNodeOverlayMark>(StringComparer.Ordinal);
            foreach (var kvp in marks)
            {
                result[kvp.Key] = new TechNodeOverlayMark
                {
                    TechId = kvp.Key,
                    UT = kvp.Value.UT,
                    RecordingId = kvp.Value.RecordingId,
                    AdditionalCommittedCount = kvp.Value.AdditionalCommittedCount,
                    Tooltip = BuildCommittedTooltip("Committed at UT", kvp.Value)
                };
            }

            return result;
        }

        internal static Dictionary<string, ContractOverlayMark> BuildContractMarks()
        {
            return BuildContractMarks(MilestoneStore.GetCommittedContractAcceptIds());
        }

        // Test-only shim for overlay/click-block predicate parity.
        internal static Dictionary<string, ContractOverlayMark> BuildContractMarks_Candidates()
        {
            return BuildContractMarks();
        }

        internal static Dictionary<string, ContractOverlayMark> BuildContractMarks(HashSet<string> committedContractKeys)
        {
            var marks = BuildEventMarks(committedContractKeys, GameStateEventType.ContractAccepted);
            var result = new Dictionary<string, ContractOverlayMark>(StringComparer.Ordinal);
            foreach (var kvp in marks)
            {
                result[kvp.Key] = new ContractOverlayMark
                {
                    ContractKey = kvp.Key,
                    UT = kvp.Value.UT,
                    RecordingId = kvp.Value.RecordingId,
                    AdditionalCommittedCount = kvp.Value.AdditionalCommittedCount,
                    Tooltip = BuildCommittedTooltip("Will be accepted at UT", kvp.Value)
                };
            }

            return result;
        }

        internal static Dictionary<string, ApplicantOverlayMark> BuildApplicantMarks(
            IEnumerable<string> kerbalNames,
            Func<string, KerbalReservationKind> reservationKindResolver)
        {
            return BuildApplicantMarks(kerbalNames, reservationKindResolver, null);
        }

        internal static Dictionary<string, ApplicantOverlayMark> BuildApplicantMarks(
            IEnumerable<string> kerbalNames,
            Func<string, KerbalReservationKind> reservationKindResolver,
            Func<string, string> reservationSlotOwnerResolver)
        {
            var result = new Dictionary<string, ApplicantOverlayMark>(StringComparer.Ordinal);
            if (kerbalNames == null)
                return result;

            var futureHires = BuildEventMarks(
                MilestoneStore.GetCommittedKerbalHireNames(),
                GameStateEventType.CrewHired);
            var futureRetires = BuildEventMarks(
                MilestoneStore.GetCommittedKerbalRetireNames(),
                GameStateEventType.CrewRemoved);

            foreach (string rawName in kerbalNames)
            {
                string name = rawName ?? "";
                if (string.IsNullOrEmpty(name) || result.ContainsKey(name))
                    continue;

                EventOverlayMark eventMark;
                if (futureHires.TryGetValue(name, out eventMark))
                {
                    result[name] = ToApplicantMark(name, ApplicantOverlayKind.FutureHired, eventMark, "Will be hired at UT");
                    continue;
                }

                if (futureRetires.TryGetValue(name, out eventMark))
                {
                    result[name] = ToApplicantMark(name, ApplicantOverlayKind.FutureRetired, eventMark, "Will be retired at UT");
                    continue;
                }

                var reservationKind = reservationKindResolver != null
                    ? reservationKindResolver(name)
                    : KerbalReservationKind.NotManaged;
                if (reservationKind == KerbalReservationKind.ReservedActive)
                {
                    result[name] = new ApplicantOverlayMark
                    {
                        KerbalName = name,
                        Kind = ApplicantOverlayKind.ReservedActive,
                        UT = -1.0,
                        RecordingId = null,
                        Tooltip = BuildReservedActiveTooltip(name, reservationSlotOwnerResolver)
                    };
                    continue;
                }

                if (reservationKind == KerbalReservationKind.ReservedRetired)
                {
                    result[name] = new ApplicantOverlayMark
                    {
                        KerbalName = name,
                        Kind = ApplicantOverlayKind.ReservedRetired,
                        UT = -1.0,
                        RecordingId = null,
                        Tooltip = "Retired stand-in (managed by Parsek)"
                    };
                }
            }

            return result;
        }

        // Test-only shim for overlay/click-block predicate parity.
        internal static Dictionary<string, ApplicantOverlayMark> BuildApplicantMarks_Candidates(
            IEnumerable<string> kerbalNames,
            Func<string, KerbalReservationKind> reservationKindResolver)
        {
            return BuildApplicantMarks(kerbalNames, reservationKindResolver);
        }

        // Test-only shim for overlay/click-block predicate parity.
        internal static Dictionary<string, ApplicantOverlayMark> BuildApplicantMarks_Candidates(
            IEnumerable<string> kerbalNames,
            Func<string, KerbalReservationKind> reservationKindResolver,
            Func<string, string> reservationSlotOwnerResolver)
        {
            return BuildApplicantMarks(kerbalNames, reservationKindResolver, reservationSlotOwnerResolver);
        }

        internal static Dictionary<string, ApplicantOverlayMark> SuppressFutureHiredApplicantsAlreadyInLiveRoster(
            IDictionary<string, ApplicantOverlayMark> marks,
            ISet<string> activeCrewOrTouristNames)
        {
            var result = new Dictionary<string, ApplicantOverlayMark>(StringComparer.Ordinal);
            if (marks == null)
                return result;

            int suppressed = 0;
            foreach (var kvp in marks)
            {
                if (kvp.Value.Kind == ApplicantOverlayKind.FutureHired
                    && activeCrewOrTouristNames != null
                    && activeCrewOrTouristNames.Contains(kvp.Key))
                {
                    suppressed++;
                    ParsekLog.Verbose(Tag,
                        $"BuildApplicantMarks: suppressed already-live future hire name={kvp.Key}");
                    continue;
                }

                result[kvp.Key] = kvp.Value;
            }

            if (suppressed > 0)
                ParsekLog.Verbose(Tag,
                    $"BuildApplicantMarks: suppressed already-live future hire count={suppressed}");

            return result;
        }

        internal static Dictionary<string, ContractOverlayMark> SuppressAlreadyActiveContractMarks(
            IDictionary<string, ContractOverlayMark> marks,
            ISet<string> alreadyActiveContractKeys)
        {
            var result = new Dictionary<string, ContractOverlayMark>(StringComparer.Ordinal);
            if (marks == null)
                return result;

            int suppressed = 0;
            foreach (var kvp in marks)
            {
                if (alreadyActiveContractKeys != null && alreadyActiveContractKeys.Contains(kvp.Key))
                {
                    suppressed++;
                    continue;
                }

                result[kvp.Key] = kvp.Value;
            }

            if (suppressed > 0)
                ParsekLog.Verbose(Tag,
                    $"BuildContractMarks: suppressed already-active committed contract count={suppressed}");

            return result;
        }

        internal static Dictionary<string, ContractOverlayMark> FilterContractMarksToVisibleContracts(
            IDictionary<string, ContractOverlayMark> marks,
            ISet<string> visibleContractKeys)
        {
            var result = new Dictionary<string, ContractOverlayMark>(StringComparer.Ordinal);
            if (marks == null)
                return result;

            int missing = 0;
            foreach (var kvp in marks)
            {
                if (visibleContractKeys != null && visibleContractKeys.Contains(kvp.Key))
                {
                    result[kvp.Key] = kvp.Value;
                    continue;
                }

                missing++;
            }

            if (missing > 0)
                ParsekLog.Verbose(Tag,
                    $"BuildContractMarks: suppressed missing offered contract count={missing}");

            return result;
        }

        private static ApplicantOverlayMark ToApplicantMark(
            string name,
            ApplicantOverlayKind kind,
            EventOverlayMark eventMark,
            string prefix)
        {
            return new ApplicantOverlayMark
            {
                KerbalName = name,
                Kind = kind,
                UT = eventMark.UT,
                RecordingId = eventMark.RecordingId,
                AdditionalCommittedCount = eventMark.AdditionalCommittedCount,
                Tooltip = BuildCommittedTooltip(prefix, eventMark)
            };
        }

        private static string BuildReservedActiveTooltip(
            string name,
            Func<string, string> reservationSlotOwnerResolver)
        {
            string slotOwner = reservationSlotOwnerResolver != null
                ? reservationSlotOwnerResolver(name)
                : null;
            return string.IsNullOrEmpty(slotOwner)
                ? "Reserved by Parsek for a committed crew slot"
                : "Reserved by Parsek for slot '" + slotOwner + "'";
        }

        private static Dictionary<string, EventOverlayMark> BuildEventMarks(
            HashSet<string> committedKeys,
            GameStateEventType eventType)
        {
            var result = new Dictionary<string, EventOverlayMark>(StringComparer.Ordinal);
            if (committedKeys == null || committedKeys.Count == 0)
                return result;

            var milestones = MilestoneStore.Milestones;
            for (int i = 0; i < milestones.Count; i++)
            {
                var milestone = milestones[i];
                if (milestone == null || !milestone.Committed || milestone.Events == null)
                    continue;

                for (int j = milestone.LastReplayedEventIndex + 1; j < milestone.Events.Count; j++)
                {
                    GameStateEvent ev = milestone.Events[j];
                    if (ev.eventType != eventType || string.IsNullOrEmpty(ev.key))
                        continue;
                    if (!committedKeys.Contains(ev.key))
                        continue;
                    if (!GameStateStore.IsEventVisibleToCurrentTimeline(ev))
                        continue;

                    EventOverlayMark existing;
                    string recordingId = !string.IsNullOrEmpty(ev.recordingId)
                        ? ev.recordingId
                        : milestone.RecordingId;
                    if (!result.TryGetValue(ev.key, out existing))
                    {
                        result[ev.key] = new EventOverlayMark
                        {
                            Key = ev.key,
                            UT = ev.ut,
                            RecordingId = recordingId,
                            AdditionalCommittedCount = 0
                        };
                    }
                    else if (ev.ut < existing.UT)
                    {
                        // EventOverlayMark is a struct; carry the duplicate count into the replacement entry.
                        existing.AdditionalCommittedCount++;
                        result[ev.key] = new EventOverlayMark
                        {
                            Key = ev.key,
                            UT = ev.ut,
                            RecordingId = recordingId,
                            AdditionalCommittedCount = existing.AdditionalCommittedCount
                        };
                    }
                    else
                    {
                        // EventOverlayMark is a struct; write the incremented copy back into the dictionary.
                        existing.AdditionalCommittedCount++;
                        result[ev.key] = existing;
                    }
                }
            }

            return result;
        }

        private static string BuildCommittedTooltip(string prefix, EventOverlayMark mark)
        {
            string text = prefix + " " + mark.UT.ToString("F0", IC);
            if (!string.IsNullOrEmpty(mark.RecordingId))
            {
                Recording rec = LedgerOrchestrator.FindRecordingById(mark.RecordingId);
                if (rec != null && !string.IsNullOrEmpty(rec.VesselName))
                    text += " — recording '" + rec.VesselName + "'";
                else
                    ParsekLog.Verbose(Tag,
                        $"StockUiOverlay: recording '{mark.RecordingId}' not found for overlay tooltip — using UT-only text");
            }

            if (mark.AdditionalCommittedCount > 0)
                text += " (+" + mark.AdditionalCommittedCount.ToString(IC) + " more committed)";
            return text;
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
                        $"StockUiOverlay: RDController.nodes reflection failed — tech overlays disabled this session ({ex.Message})");
                }

                return false;
            }
        }

        private static bool TryGetAstronautListRoots(AstronautComplex complex, out List<Transform> roots)
        {
            roots = new List<Transform>();
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
                        roots.Add(component.transform);
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
                        $"StockUiOverlay: AstronautComplex list-field reflection failed — applicant overlays disabled this session ({ex.Message})");
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

        private static List<CrewListItem> CollectCrewListItems(List<Transform> roots)
        {
            var result = new List<CrewListItem>();
            var seen = new HashSet<CrewListItem>();
            for (int i = 0; i < roots.Count; i++)
            {
                Transform root = roots[i];
                if (root == null)
                    continue;

                CrewListItem[] items = root.GetComponentsInChildren<CrewListItem>(true);
                for (int j = 0; j < items.Length; j++)
                {
                    if (items[j] != null && seen.Add(items[j]))
                        result.Add(items[j]);
                }
            }

            return result;
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
                    $"StockUiOverlay: CrewListItem.GetName() failed — overlay skipped ({ex.Message})");
                return null;
            }
        }

        private static KerbalReservationKind ResolveReservationKind(string name)
        {
            return LedgerOrchestrator.Kerbals?.GetReservationKind(name)
                ?? KerbalReservationKind.NotManaged;
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

        private static int CountApplicantMarks(
            Dictionary<string, ApplicantOverlayMark> marks,
            ApplicantOverlayKind kind)
        {
            int count = 0;
            foreach (var kvp in marks)
            {
                if (kvp.Value.Kind == kind)
                    count++;
            }

            return count;
        }

        private static bool TryGetMissionControlRowContract(MCListItem row, out Contract contract)
        {
            contract = null;
            if (row == null)
                return false;

            try
            {
                contract = row.container != null ? row.container.Data as Contract : null;
                if (contract != null)
                    return true;

                if (!missionRowsWarned)
                {
                    missionRowsWarned = true;
                    ParsekLog.Warn(Tag,
                        "StockUiOverlay: MissionControl row contract lookup failed — contract overlays disabled for rows without UIListItem.Data Contract");
                }
            }
            catch (Exception ex)
            {
                if (!missionRowsWarned)
                {
                    missionRowsWarned = true;
                    ParsekLog.Warn(Tag,
                        $"StockUiOverlay: MissionControl row contract lookup failed — contract overlays disabled for rows without UIListItem.Data Contract ({ex.Message})");
                }
            }

            return false;
        }

        private static HashSet<string> CollectActiveContractKeys()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var system = ContractSystem.Instance;
            if (system == null || system.Contracts == null)
                return result;

            var contracts = system.Contracts;
            for (int i = 0; i < contracts.Count; i++)
            {
                Contract contract = contracts[i];
                if (contract != null && contract.ContractState == Contract.State.Active)
                    result.Add(contract.ContractGuid.ToString());
            }

            return result;
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

        private static Color ColorForApplicantKind(ApplicantOverlayKind kind)
        {
            switch (kind)
            {
                case ApplicantOverlayKind.FutureHired:
                    return new Color(0.33f, 0.86f, 0.48f, 0.95f);
                case ApplicantOverlayKind.FutureRetired:
                    return new Color(0.62f, 0.62f, 0.62f, 0.95f);
                case ApplicantOverlayKind.ReservedActive:
                    return new Color(1.0f, 0.70f, 0.28f, 0.95f);
                case ApplicantOverlayKind.ReservedRetired:
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

        private struct EventOverlayMark
        {
            public string Key;
            public double UT;
            public string RecordingId;
            public int AdditionalCommittedCount;
        }
    }
}
