using System;
using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Kerbals roster window — shows reserved crew, active stand-ins, and retired
    /// stand-ins in one place. Retired list previously lived in the Timeline footer
    /// (#385).
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

        private GUIStyle grayStyle;
        private GUIStyle sectionHeaderStyle;
        private GUIStyle groupHeaderStyle;
        private GUIStyle deadStyle;
        private GUIStyle recoveredStyle;
        private GUIStyle aboardStyle;

        internal struct KerbalsViewModel
        {
            public List<ReservedEntry> Reserved;
            public List<ActiveStandInEntry> Active;
            public List<RetiredEntry> Retired;
            public List<CrewEndStateEntry> EndStates;
        }

        internal struct ReservedEntry
        {
            public string Owner;
            public string Trait;
            public double UntilUT;
            public bool IsPermanent;
        }

        internal struct ActiveStandInEntry
        {
            public string StandIn;
            public string Owner;
            public string Trait;
        }

        internal struct RetiredEntry
        {
            public string StandIn;
            public string FormerOwner;  // empty for orphans not linked to any slot
            public string Trait;        // inherited from FormerOwner's slot
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
                "Parsek \u2014 Kerbals",
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
            if (grayStyle != null) return;
            grayStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
            };
            sectionHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                stretchWidth = true
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

            if (vm.Reserved.Count == 0 && vm.Active.Count == 0
                && vm.Retired.Count == 0 && vm.EndStates.Count == 0)
            {
                GUILayout.Label("No reserved crew, stand-ins, retired kerbals, or committed crew history.", grayStyle);
            }
            else
            {
                DrawReservedSection(vm.Reserved);
                DrawActiveSection(vm.Active);
                DrawRetiredSection(vm.Retired);
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

        private void DrawReservedSection(List<ReservedEntry> reserved)
        {
            if (reserved.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Reserved Crew ({reserved.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < reserved.Count; i++)
            {
                var e = reserved[i];
                GUILayout.Label(FormatReservedRow(e), grayStyle);
            }
            GUILayout.EndVertical();
        }

        private void DrawActiveSection(List<ActiveStandInEntry> active)
        {
            if (active.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Active Stand-ins ({active.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < active.Count; i++)
            {
                var e = active[i];
                GUILayout.Label(
                    $"{e.StandIn} standing in for {e.Owner} [{e.Trait}]",
                    grayStyle);
            }
            GUILayout.EndVertical();
        }

        private void DrawRetiredSection(List<RetiredEntry> retired)
        {
            if (retired.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Retired Stand-ins ({retired.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < retired.Count; i++)
            {
                GUILayout.Label(FormatRetiredRow(retired[i]), grayStyle);
            }
            GUILayout.EndVertical();
        }

        internal static string FormatRetiredRow(RetiredEntry e)
        {
            if (string.IsNullOrEmpty(e.FormerOwner))
                return e.StandIn;
            return string.IsNullOrEmpty(e.Trait)
                ? $"{e.StandIn} \u2014 stood in for {e.FormerOwner}"
                : $"{e.StandIn} [{e.Trait}] \u2014 stood in for {e.FormerOwner}";
        }

        private void DrawEndStatesSection(List<CrewEndStateEntry> endStates)
        {
            if (endStates.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Per-Recording Fates ({endStates.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);
            string lastKerbal = null;
            for (int i = 0; i < endStates.Count; i++)
            {
                var e = endStates[i];
                if (e.KerbalName != lastKerbal)
                {
                    if (lastKerbal != null) GUILayout.Space(3);
                    GUILayout.Label(e.KerbalName, groupHeaderStyle);
                    lastKerbal = e.KerbalName;
                }
                GUILayout.Label("  " + FormatEndStateRow(e), StyleForEndState(e.EndState));
            }
            GUILayout.EndVertical();
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
            return $"{rec} \u2014 {FormatEndState(e.EndState)} at UT {e.EndUT.ToString("F0", ic)}";
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

        internal static string FormatReservedRow(ReservedEntry e)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string header = $"{e.Owner} [{e.Trait}]";
            if (double.IsPositiveInfinity(e.UntilUT))
                return header;
            return header + $" \u2014 until UT {e.UntilUT.ToString("F0", ic)}";
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
                Reserved = new List<ReservedEntry>(),
                Active = new List<ActiveStandInEntry>(),
                Retired = new List<RetiredEntry>(),
                EndStates = new List<CrewEndStateEntry>()
            };

            // Map each stand-in name to the owner slot it belongs to, so retired rows can
            // display the former-owner context (trait + owner name) instead of just a bare
            // name. In the pathological case where the same name shows up in multiple slot
            // chains, the first (lexicographically smallest owner) wins — keeps output
            // deterministic without a hard guarantee from the data source.
            var standInOwner = new Dictionary<string, KeyValuePair<string, string>>(
                StringComparer.Ordinal);

            if (slots != null && reservations != null && activeChainIndexOf != null)
            {
                var ownerNames = new List<string>(slots.Keys);
                ownerNames.Sort(StringComparer.Ordinal);

                for (int i = 0; i < ownerNames.Count; i++)
                {
                    var owner = ownerNames[i];
                    var slot = slots[owner];
                    if (slot == null) continue;

                    // Reserved: non-permanent, non-dead owner with a live reservation.
                    if (!slot.OwnerPermanentlyGone
                        && reservations.TryGetValue(owner, out var res)
                        && res != null
                        && !res.IsPermanent)
                    {
                        vm.Reserved.Add(new ReservedEntry
                        {
                            Owner = owner,
                            Trait = slot.OwnerTrait,
                            UntilUT = res.ReservedUntilUT,
                            IsPermanent = false
                        });
                    }

                    // Active stand-in: canonical occupancy walk via injected delegate.
                    int idx = activeChainIndexOf(slot);
                    if (idx >= 0 && slot.Chain != null && idx < slot.Chain.Count)
                    {
                        string standIn = slot.Chain[idx];
                        if (!string.IsNullOrEmpty(standIn))
                        {
                            vm.Active.Add(new ActiveStandInEntry
                            {
                                StandIn = standIn,
                                Owner = owner,
                                Trait = slot.OwnerTrait
                            });
                        }
                    }

                    // Index every chain entry for retired-row enrichment below.
                    if (slot.Chain != null)
                    {
                        for (int c = 0; c < slot.Chain.Count; c++)
                        {
                            string name = slot.Chain[c];
                            if (string.IsNullOrEmpty(name)) continue;
                            if (!standInOwner.ContainsKey(name))
                                standInOwner[name] = new KeyValuePair<string, string>(
                                    owner, slot.OwnerTrait);
                        }
                    }
                }
            }

            // Retired comes from a HashSet snapshot in KerbalsModule — iteration order is
            // implementation-defined and reshuffles between recalculations. Sort ordinally
            // so the rendered list is stable save-over-save.
            if (retired != null)
            {
                var retiredSorted = new List<string>(retired);
                retiredSorted.Sort(StringComparer.Ordinal);
                for (int i = 0; i < retiredSorted.Count; i++)
                {
                    string name = retiredSorted[i];
                    string formerOwner = "";
                    string trait = "";
                    if (standInOwner.TryGetValue(name, out var info))
                    {
                        formerOwner = info.Key;
                        trait = info.Value;
                    }
                    vm.Retired.Add(new RetiredEntry
                    {
                        StandIn = name,
                        FormerOwner = formerOwner,
                        Trait = trait
                    });
                }
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
                $"KerbalsWindow: built VM \u2014 reserved={vm.Reserved.Count} " +
                $"active={vm.Active.Count} retired={vm.Retired.Count} " +
                $"endStates={vm.EndStates.Count}");

            return vm;
        }
    }
}
