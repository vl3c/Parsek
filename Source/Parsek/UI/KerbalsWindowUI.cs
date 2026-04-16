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

        internal struct KerbalsViewModel
        {
            public List<ReservedEntry> Reserved;
            public List<ActiveStandInEntry> Active;
            public List<RetiredEntry> Retired;
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
        }

        private void DrawKerbalsWindow(int windowID)
        {
            EnsureStyles();

            if (cachedVM == null)
            {
                var kerbals = LedgerOrchestrator.Kerbals;
                if (kerbals == null)
                {
                    cachedVM = new KerbalsViewModel
                    {
                        Reserved = new List<ReservedEntry>(),
                        Active = new List<ActiveStandInEntry>(),
                        Retired = new List<RetiredEntry>()
                    };
                }
                else
                {
                    cachedVM = Build(
                        kerbals.Slots,
                        kerbals.Reservations,
                        kerbals.GetRetiredKerbals(),
                        slot => kerbals.GetActiveChainIndex(slot));
                }
            }

            var vm = cachedVM.Value;

            kerbalsScrollPos = GUILayout.BeginScrollView(kerbalsScrollPos, GUILayout.ExpandHeight(true));

            if (vm.Reserved.Count == 0 && vm.Active.Count == 0 && vm.Retired.Count == 0)
            {
                GUILayout.Label("No reserved crew, stand-ins, or retired kerbals.", grayStyle);
            }
            else
            {
                DrawReservedSection(vm.Reserved);
                DrawActiveSection(vm.Active);
                DrawRetiredSection(vm.Retired);
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
            ActiveChainIndexFunc activeChainIndexOf)
        {
            var vm = new KerbalsViewModel
            {
                Reserved = new List<ReservedEntry>(),
                Active = new List<ActiveStandInEntry>(),
                Retired = new List<RetiredEntry>()
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

            ParsekLog.Verbose("UI",
                $"KerbalsWindow: built VM \u2014 reserved={vm.Reserved.Count} " +
                $"active={vm.Active.Count} retired={vm.Retired.Count}");

            return vm;
        }
    }
}
