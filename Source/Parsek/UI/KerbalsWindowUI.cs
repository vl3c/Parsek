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
        private const string KerbalsInputLockId = "Parsek_KerbalsWindow";
        private Rect lastKerbalsWindowRect;

        private KerbalsViewModel? cachedVM;

        private GUIStyle grayStyle;
        private GUIStyle sectionHeaderStyle;

        internal struct KerbalsViewModel
        {
            public List<ReservedEntry> Reserved;
            public List<ActiveStandInEntry> Active;
            public List<string> Retired;
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
                    320, 10);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Kerbals window initial position: x={kerbalsWindowRect.x.ToString("F0", ic)} y={kerbalsWindowRect.y.ToString("F0", ic)}");
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            kerbalsWindowRect.height = 10;
            kerbalsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekKerbals".GetHashCode(),
                kerbalsWindowRect,
                DrawKerbalsWindow,
                "Parsek \u2014 Kerbals",
                opaqueWindowStyle,
                GUILayout.Width(320)
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
                        Retired = new List<string>()
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

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Close"))
            {
                showKerbalsWindow = false;
                ParsekLog.Verbose("UI", "Kerbals window closed via button");
            }

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

        private void DrawRetiredSection(List<string> retired)
        {
            if (retired.Count == 0) return;
            GUILayout.Space(5);
            GUILayout.Label($"Retired Stand-ins ({retired.Count})", sectionHeaderStyle);
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < retired.Count; i++)
            {
                GUILayout.Label(retired[i], grayStyle);
            }
            GUILayout.EndVertical();
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
                Retired = retired != null ? new List<string>(retired) : new List<string>()
            };

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
                }
            }

            ParsekLog.Verbose("UI",
                $"KerbalsWindow: built VM \u2014 reserved={vm.Reserved.Count} " +
                $"active={vm.Active.Count} retired={vm.Retired.Count}");

            return vm;
        }
    }
}
