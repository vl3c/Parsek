using System;
using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Kerbals roster window — renders each career-kerbal slot as a collapsible
    /// per-owner chain (reserved/active/retired/displaced stand-ins indented as
    /// tree children), plus an Unlinked Retired tail section for stand-ins that
    /// no longer belong to any slot, plus the Per-Recording Fates section with
    /// per-kerbal fold/unfold (#415-1).
    /// </summary>
    internal class KerbalsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showKerbalsWindow;
        private Rect kerbalsWindowRect;
        private bool kerbalsWindowHasInputLock;
        private bool isResizingKerbalsWindow;
        private Vector2 kerbalsScrollPos;
        private const string KerbalsInputLockId = "Parsek_KerbalsWindow";
        private const float MinWindowWidth = 280f;
        private const float MinWindowHeight = 150f;
        private const float DefaultWindowWidth = 320f;
        private const float DefaultWindowHeight = 400f;
        private Rect lastKerbalsWindowRect;

        private KerbalsViewModel? cachedVM;

        // Fold-toggle arrow glyphs; match the chain-block pattern in RecordingsTableUI.
        private const string FoldedArrow = "\u25b6";
        private const string UnfoldedArrow = "\u25bc";

        // Transient fold state for Per-Recording Fates groups. Default-unfolded means we
        // only store names that are currently folded, so HashSet fits the access pattern.
        // InvalidateCache does NOT clear this — fold is UI preference, not data.
        internal readonly HashSet<string> foldedKerbals = new HashSet<string>(StringComparer.Ordinal);

        // Transient expand state for per-owner slot topology rows. Default-collapsed —
        // the set only contains OwnerNames currently expanded, so the initial window
        // view is a contiguous single-line list of owners. Orthogonal to data, so not
        // cleared by InvalidateCache.
        private readonly HashSet<string> expandedSlots = new HashSet<string>(StringComparer.Ordinal);

        private GUIStyle grayStyle;
        private GUIStyle sectionHeaderStyle;
        private GUIStyle groupHeaderStyle;
        private GUIStyle deadStyle;
        private GUIStyle recoveredStyle;
        private GUIStyle aboardStyle;
        private GUIStyle activeChainStyle;
        private GUIStyle displacedStyle;

        internal struct KerbalsViewModel
        {
            public List<SlotTopologyEntry> Topology;
            public List<string> OrphanRetired;
            public List<CrewEndStateEntry> EndStates;
        }

        internal enum ChainMemberStatus
        {
            Active,
            Retired,
            Displaced,
            Unknown
        }

        internal struct SlotTopologyEntry
        {
            public string OwnerName;
            public string OwnerTrait;
            public bool OwnerPermanentlyGone;
            public bool OwnerReserved;
            public double OwnerReservedUntilUT;
            public List<ChainMember> Chain;
        }

        internal struct ChainMember
        {
            public string Name;
            public int ChainIndex;
            public ChainMemberStatus Status;
        }

        internal struct CrewEndStateEntry
        {
            public string KerbalName;
            public string RecordingName;
            public string RecordingId;
            public double EndUT;
            public KerbalEndState EndState;
        }

        internal delegate int ActiveChainIndexFunc(KerbalsModule.KerbalSlot slot);

        public bool IsOpen
        {
            get { return showKerbalsWindow; }
            set { showKerbalsWindow = value; }
        }

        internal KerbalsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void InvalidateCache()
        {
            cachedVM = null;
            ParsekLog.Verbose("UI", "KerbalsWindow: cache invalidated");
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showKerbalsWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (kerbalsWindowRect.width < 1f)
            {
                kerbalsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    DefaultWindowWidth,
                    DefaultWindowHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Kerbals window initial position: x={kerbalsWindowRect.x.ToString("F0", ic)} y={kerbalsWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref kerbalsWindowRect, ref isResizingKerbalsWindow,
                MinWindowWidth, MinWindowHeight, "Kerbals window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            kerbalsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekKerbals".GetHashCode(),
                kerbalsWindowRect,
                DrawKerbalsWindow,
                "Parsek - Kerbals",
                opaqueWindowStyle,
                GUILayout.Width(kerbalsWindowRect.width),
                GUILayout.Height(kerbalsWindowRect.height)
            );
            parentUI.LogWindowPosition("Kerbals", ref lastKerbalsWindowRect, kerbalsWindowRect);

            if (kerbalsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!kerbalsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, KerbalsInputLockId);
                    kerbalsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        internal void ReleaseInputLock()
        {
            if (!kerbalsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(KerbalsInputLockId);
            kerbalsWindowHasInputLock = false;
        }

        private void EnsureStyles()
        {
            // Section header style is shared across the mod via ParsekUI; reassign
            // every draw so any ParsekUI-level updates flow through.
            sectionHeaderStyle = parentUI.GetSectionHeaderStyle();
            if (grayStyle != null) return;
            grayStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
            };
            groupHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
            deadStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.95f, 0.45f, 0.45f) }
            };
            recoveredStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.55f, 0.85f, 0.55f) }
            };
            aboardStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.6f, 0.8f, 0.95f) }
            };
            activeChainStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.6f, 0.8f, 0.95f) }
            };
            displacedStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
            };
        }

        private void DrawKerbalsWindow(int windowID)
        {
            EnsureStyles();

            if (cachedVM == null)
            {
                var kerbals = LedgerOrchestrator.Kerbals;
                var recordings = RecordingStore.CommittedRecordings;
                if (kerbals == null)
                {
                    cachedVM = Build(
                        null,
                        null,
                        null,
                        recordings,
                        null);
                }
                else
                {
                    cachedVM = Build(
                        kerbals.Slots,
                        kerbals.Reservations,
                        kerbals.GetRetiredKerbals(),
                        recordings,
                        slot => kerbals.GetActiveChainIndex(slot));
                }
            }

            var vm = cachedVM.Value;

            kerbalsScrollPos = GUILayout.BeginScrollView(kerbalsScrollPos, GUILayout.ExpandHeight(true));

            if (vm.Topology.Count == 0 && vm.OrphanRetired.Count == 0 && vm.EndStates.Count == 0)
            {
                GUILayout.Label("No reserved crew, stand-ins, retired kerbals, or committed crew history.", grayStyle);
            }
            else
            {
                DrawTopologySection(vm.Topology);
                DrawOrphanRetiredSection(vm.OrphanRetired);
                DrawEndStatesSection(vm.EndStates);
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("Close"))
            {
                showKerbalsWindow = false;
                ParsekLog.Verbose("UI", "Kerbals window closed via button");
            }

            ParsekUI.DrawResizeHandle(kerbalsWindowRect, ref isResizingKerbalsWindow,
                "Kerbals window");

            GUI.DragWindow();
        }

        private void DrawTopologySection(List<SlotTopologyEntry> topology)
        {
            if (topology.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Kerbal Slots ({topology.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);

            bool first = true;
            for (int i = 0; i < topology.Count; i++)
            {
                var entry = topology[i];
                int chainCount = CountExpandableChainEntries(entry.Chain);
                bool expandable = chainCount > 0;
                bool expanded = expandedSlots.Contains(entry.OwnerName);

                if (!first) GUILayout.Space(3);
                first = false;

                DrawOwnerHeader(entry, expandable, expanded, chainCount);

                if (expandable && expanded)
                {
                    int lastIdx = -1;
                    if (entry.Chain != null)
                    {
                        for (int c = entry.Chain.Count - 1; c >= 0; c--)
                        {
                            if (!string.IsNullOrEmpty(entry.Chain[c].Name))
                            {
                                lastIdx = c;
                                break;
                            }
                        }
                    }
                    for (int c = 0; c < entry.Chain.Count; c++)
                    {
                        var member = entry.Chain[c];
                        if (string.IsNullOrEmpty(member.Name)) continue;
                        string prefix = (c == lastIdx) ? "    \u2514\u2500 " : "    \u251c\u2500 ";
                        GUILayout.Label(prefix + FormatChainMember(member), StyleForChainMember(member.Status));
                    }
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawOwnerHeader(SlotTopologyEntry entry, bool expandable, bool expanded, int chainCount)
        {
            string body = FormatOwnerHeader(entry);
            string countSuffix = expandable ? $"  ({chainCount})" : "";
            GUIStyle style = entry.OwnerPermanentlyGone ? deadStyle : groupHeaderStyle;

            if (!expandable)
            {
                // Indent by the width of the arrow + space so the leaf body lines
                // up with bodies on arrow-prefixed (expandable) rows.
                GUILayout.Label("  " + body + countSuffix, style);
                return;
            }

            string arrow = expanded ? UnfoldedArrow : FoldedArrow;
            if (GUILayout.Button($"{arrow} {body}{countSuffix}", GUI.skin.label, GUILayout.ExpandWidth(true)))
            {
                if (expanded) expandedSlots.Remove(entry.OwnerName);
                else expandedSlots.Add(entry.OwnerName);
                ParsekLog.Verbose("UI",
                    $"Kerbal slot '{entry.OwnerName}' {(expanded ? "collapsed" : "expanded")} ({chainCount} chain members)");
            }
        }

        private GUIStyle StyleForChainMember(ChainMemberStatus s)
        {
            switch (s)
            {
                case ChainMemberStatus.Active: return activeChainStyle;
                case ChainMemberStatus.Retired: return grayStyle;
                case ChainMemberStatus.Displaced: return displacedStyle;
                default: return grayStyle;
            }
        }

        private void DrawOrphanRetiredSection(List<string> orphans)
        {
            if (orphans.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Unlinked Retired ({orphans.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < orphans.Count; i++)
            {
                GUILayout.Label(orphans[i], grayStyle);
            }
            GUILayout.EndVertical();
        }

        internal static int CountExpandableChainEntries(List<ChainMember> chain)
        {
            if (chain == null) return 0;
            int n = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                if (!string.IsNullOrEmpty(chain[i].Name)) n++;
            }
            return n;
        }

        internal static string FormatOwnerHeader(SlotTopologyEntry entry)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string status;
            if (entry.OwnerPermanentlyGone)
            {
                status = "deceased";
            }
            else if (entry.OwnerReserved)
            {
                status = double.IsPositiveInfinity(entry.OwnerReservedUntilUT)
                    ? "reserved"
                    : $"reserved until UT {entry.OwnerReservedUntilUT.ToString("F0", ic)}";
            }
            else
            {
                status = "active";
            }
            return $"{entry.OwnerName} [{entry.OwnerTrait}] - {status}";
        }

        internal static string FormatChainMember(ChainMember m)
        {
            string tag;
            switch (m.Status)
            {
                case ChainMemberStatus.Active: tag = "active"; break;
                case ChainMemberStatus.Retired: tag = "retired"; break;
                case ChainMemberStatus.Displaced: tag = "displaced"; break;
                default: tag = "?"; break;
            }
            return $"{m.Name} ({tag})";
        }

        private void DrawEndStatesSection(List<CrewEndStateEntry> endStates)
        {
            if (endStates.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Per-Recording Fates ({endStates.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);

            int i = 0;
            bool first = true;
            while (i < endStates.Count)
            {
                string name = endStates[i].KerbalName;
                int j = i;
                while (j < endStates.Count && endStates[j].KerbalName == name) j++;

                if (!first) GUILayout.Space(3);
                first = false;

                bool folded = foldedKerbals.Contains(name);
                string arrow = folded ? FoldedArrow : UnfoldedArrow;
                string headerText = folded
                    ? FormatKerbalSummary(name, endStates, i, j)
                    : name;

                // Use groupHeaderStyle (bold) as the button style so unfolded output stays
                // visually identical to the pre-fold-toggle design — only the arrow prefix
                // is new. RecordingsTableUI uses GUI.skin.label for its chain blocks because
                // those are body rows; Per-Recording Fates headers are group headers.
                if (GUILayout.Button($"{arrow} {headerText}", groupHeaderStyle, GUILayout.ExpandWidth(true)))
                {
                    ToggleFold(foldedKerbals, name, j - i);
                }

                if (!folded)
                {
                    for (int k = i; k < j; k++)
                    {
                        var e = endStates[k];
                        if (GUILayout.Button("  " + FormatEndStateRow(e), StyleForEndState(e.EndState)))
                        {
                            // Mirrors the Timeline.GoTo → RecordingsTableUI.ScrollToRecording
                            // cross-link pattern (TimelineWindowUI.cs:665). GetTimelineUI()
                            // can return null during cold-start scene transitions — the
                            // helper tolerates a null callback and still emits the
                            // diagnostic log (E14).
                            var timelineUI = parentUI != null ? parentUI.GetTimelineUI() : null;
                            Action<string> scrollCallback = timelineUI != null
                                ? timelineUI.ScrollToRecording
                                : (Action<string>)null;
                            OnFatesRowClicked(scrollCallback, e.RecordingId);
                        }
                    }
                }
                i = j;
            }

            GUILayout.EndVertical();
        }

        // Pure mutation + log helper so the toggle contract is unit-testable outside IMGUI.
        // Returns the new folded state (true = now folded, false = now unfolded).
        internal static bool ToggleFold(
            HashSet<string> foldedKerbals, string kerbalName, int missionCount)
        {
            bool wasFolded = foldedKerbals.Contains(kerbalName);
            if (wasFolded) foldedKerbals.Remove(kerbalName);
            else foldedKerbals.Add(kerbalName);
            ParsekLog.Verbose("UI",
                $"Kerbals fold toggled: '{kerbalName}' -> {(wasFolded ? "unfolded" : "folded")} ({missionCount} missions)");
            return !wasFolded;
        }

        // Pure helper for the Fates → Timeline cross-link. Production passes
        // `parentUI.GetTimelineUI().ScrollToRecording` as the callback; tests pass a
        // lambda spy. Tolerates a null callback (E14 — GetTimelineUI() can be null
        // during cold-start scene transitions) so the click never NREs; the log
        // still fires so stale-id clicks leave a diagnostic trail.
        internal static void OnFatesRowClicked(
            Action<string> scrollCallback, string recordingId)
        {
            ParsekLog.Verbose("UI",
                $"Kerbals Fates \u2192 Timeline scroll: recordingId={recordingId}");
            if (scrollCallback != null) scrollCallback(recordingId);
        }

        internal static string FormatKerbalSummary(
            string kerbalName,
            IReadOnlyList<CrewEndStateEntry> entries,
            int start,
            int end)
        {
            int dead = 0, recovered = 0, aboard = 0, unknown = 0;
            for (int k = start; k < end; k++)
            {
                switch (entries[k].EndState)
                {
                    case KerbalEndState.Dead: dead++; break;
                    case KerbalEndState.Recovered: recovered++; break;
                    case KerbalEndState.Aboard: aboard++; break;
                    default: unknown++; break;
                }
            }
            int total = end - start;
            var parts = new List<string>(4);
            if (dead > 0) parts.Add($"{dead} Dead");
            if (recovered > 0) parts.Add($"{recovered} Recovered");
            if (aboard > 0) parts.Add($"{aboard} Aboard");
            if (unknown > 0) parts.Add($"{unknown} Unknown");
            string missionLabel = total == 1 ? "1 mission" : $"{total} missions";
            return $"{kerbalName} ({missionLabel} - {string.Join(", ", parts)})";
        }

        private GUIStyle StyleForEndState(KerbalEndState s)
        {
            switch (s)
            {
                case KerbalEndState.Dead: return deadStyle;
                case KerbalEndState.Recovered: return recoveredStyle;
                case KerbalEndState.Aboard: return aboardStyle;
                default: return grayStyle;
            }
        }

        internal static string FormatEndStateRow(CrewEndStateEntry e)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string rec = string.IsNullOrEmpty(e.RecordingName) ? "(unnamed)" : e.RecordingName;
            return $"{rec} - {FormatEndState(e.EndState)} at UT {e.EndUT.ToString("F0", ic)}";
        }

        internal static string FormatEndState(KerbalEndState s)
        {
            switch (s)
            {
                case KerbalEndState.Dead: return "Dead";
                case KerbalEndState.Recovered: return "Recovered";
                case KerbalEndState.Aboard: return "Aboard";
                default: return "Unknown";
            }
        }

        internal static KerbalsViewModel Build(
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots,
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations,
            IReadOnlyList<string> retired,
            IReadOnlyList<Recording> committedRecordings,
            ActiveChainIndexFunc activeChainIndexOf)
        {
            var vm = new KerbalsViewModel
            {
                Topology = new List<SlotTopologyEntry>(),
                OrphanRetired = new List<string>(),
                EndStates = new List<CrewEndStateEntry>()
            };

            // Retired snapshot is a HashSet in KerbalsModule — iteration order is
            // implementation-defined. Copy to an ordered lookup so classification is
            // deterministic and orphan detection is O(1).
            var retiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (retired != null)
            {
                for (int i = 0; i < retired.Count; i++)
                {
                    string n = retired[i];
                    if (!string.IsNullOrEmpty(n)) retiredSet.Add(n);
                }
            }

            // Names that appear in some slot's Chain — the complement within retiredSet
            // is the orphan set. If the same stand-in name somehow appears in two
            // different slots' chains (not expected by construction, but not enforced
            // by the data source), it shows up under both owner rows and never lands
            // in OrphanRetired — deliberate: duplicate visibility beats losing the
            // link entirely.
            var seenInChain = new HashSet<string>(StringComparer.Ordinal);

            if (slots != null && reservations != null && activeChainIndexOf != null)
            {
                var ownerNames = new List<string>(slots.Keys);
                ownerNames.Sort(StringComparer.Ordinal);

                for (int i = 0; i < ownerNames.Count; i++)
                {
                    string owner = ownerNames[i];
                    var slot = slots[owner];
                    if (slot == null) continue;

                    bool ownerReserved = false;
                    double ownerReservedUntilUT = 0.0;
                    if (!slot.OwnerPermanentlyGone
                        && reservations.TryGetValue(owner, out var res)
                        && res != null
                        && !res.IsPermanent)
                    {
                        ownerReserved = true;
                        ownerReservedUntilUT = res.ReservedUntilUT;
                    }

                    int activeIdx = activeChainIndexOf(slot);
                    var chainMembers = new List<ChainMember>();
                    if (slot.Chain != null)
                    {
                        for (int c = 0; c < slot.Chain.Count; c++)
                        {
                            string name = slot.Chain[c];
                            if (string.IsNullOrEmpty(name)) continue;

                            // Retired wins before active: ComputeRetiredSet only marks
                            // !isReserved names, so the two are mutually exclusive by
                            // construction — ordering here is defensive.
                            ChainMemberStatus status;
                            if (retiredSet.Contains(name))
                                status = ChainMemberStatus.Retired;
                            else if (activeIdx >= 0 && activeIdx < slot.Chain.Count && c == activeIdx)
                                status = ChainMemberStatus.Active;
                            else
                                status = ChainMemberStatus.Displaced;

                            chainMembers.Add(new ChainMember
                            {
                                Name = name,
                                ChainIndex = c,
                                Status = status
                            });
                            seenInChain.Add(name);
                        }
                    }

                    vm.Topology.Add(new SlotTopologyEntry
                    {
                        OwnerName = owner,
                        OwnerTrait = slot.OwnerTrait,
                        OwnerPermanentlyGone = slot.OwnerPermanentlyGone,
                        OwnerReserved = ownerReserved,
                        OwnerReservedUntilUT = ownerReservedUntilUT,
                        Chain = chainMembers
                    });
                }
            }

            if (retiredSet.Count > 0)
            {
                var orphans = new List<string>();
                foreach (var name in retiredSet)
                {
                    if (!seenInChain.Contains(name)) orphans.Add(name);
                }
                orphans.Sort(StringComparer.Ordinal);
                vm.OrphanRetired = orphans;
            }

            // Per-recording crew end-states. Skip recordings where
            // CrewEndStatesResolved==false (end-states still pending) or where the dict is
            // null (nothing committed yet). Group ordinally by kerbal name, then
            // chronologically by EndUT within a group so each kerbal's mission history reads
            // forward in time.
            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    var rec = committedRecordings[i];
                    if (rec == null) continue;
                    if (!rec.CrewEndStatesResolved) continue;
                    if (rec.CrewEndStates == null) continue;
                    foreach (var kvp in rec.CrewEndStates)
                    {
                        if (string.IsNullOrEmpty(kvp.Key)) continue;
                        vm.EndStates.Add(new CrewEndStateEntry
                        {
                            KerbalName = kvp.Key,
                            RecordingName = rec.VesselName ?? "",
                            RecordingId = rec.RecordingId ?? "",
                            EndUT = rec.EndUT,
                            EndState = kvp.Value
                        });
                    }
                }

                vm.EndStates.Sort((a, b) =>
                {
                    int n = StringComparer.Ordinal.Compare(a.KerbalName, b.KerbalName);
                    if (n != 0) return n;
                    return a.EndUT.CompareTo(b.EndUT);
                });
            }

            ParsekLog.Verbose("UI",
                $"KerbalsWindow: built VM \u2014 topology={vm.Topology.Count} " +
                $"orphans={vm.OrphanRetired.Count} " +
                $"endStates={vm.EndStates.Count}");

            return vm;
        }
    }
}
