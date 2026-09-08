using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI guard that a control drawn under <c>GUI.enabled = false</c> still gets
    /// its "why is this greyed out?" reason into <see cref="GUI.tooltip"/> - the channel
    /// the window's <see cref="TooltipEchoBox"/> strip echoes.
    ///
    /// <para><b>Why this cannot be a unit test.</b> Whether a tooltip is published at all
    /// is decided inside <c>UnityEngine.GUI</c> during a real Repaint against a real
    /// pointer. Nothing headless can observe it.</para>
    ///
    /// <para><b>Why it is worth a live cell.</b> The shipped
    /// UnityEngine.IMGUIModule has exactly two MANAGED tooltip-publish sites,
    /// <c>GUI.DoLabel</c> and <c>GUI.DoButtonGrid</c>, and neither reads
    /// <c>GUI.enabled</c>. Every other control - <c>GUI.Button</c> included - publishes
    /// from the NATIVE <c>GUIStyle.Internal_Draw2</c>, whose behaviour while disabled
    /// cannot be read out of the assembly. <see cref="DisabledHoverEcho"/> therefore does
    /// not depend on the native path: it routes the reason through a zero-size
    /// <c>GUI.Label</c>, the proven one. This cell is what turns that reasoning into a
    /// measurement on the machine the mod actually runs on.</para>
    ///
    /// <para>It also RECORDS, without asserting, what the native disabled-button path
    /// does. That is Unity's behaviour rather than Parsek's, and the feature is
    /// deliberately built not to care - but it is exactly the fact a future reader will
    /// want, and there is no other way to learn it.</para>
    /// </summary>
    public sealed class DisabledHoverEchoImguiTest
    {
        // Its OWN category, deliberately not the sibling strip guards' `Settings`.
        // H46-settings.toml pins `BATCH_COMPLETE v1 total=5 passed=4 ... skipped=1` for
        // `Settings` from a real 2026-08-28 flight, and this cell SKIPS whenever the
        // pointer is not over the probe rect - which an unattended batch cannot
        // guarantee. Declaring it there would move `total` and force a guess at the
        // passed/skipped split, and only a live flight can measure that. A category no
        // spec drives keeps the tally honest until someone flies this one.
        [InGameTest(Category = "DisabledHoverEcho",
            Description = "A greyed-out button still publishes its why-disabled reason to GUI.tooltip through the zero-size Label carrier, so the window help strip can echo it")]
        public IEnumerator DisabledControl_PublishesItsReasonToTheTooltipChannel()
        {
            var go = new GameObject("ParsekDisabledHoverEchoProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            DisabledHoverProbe probe = go.AddComponent<DisabledHoverProbe>();
            // Unattended batches leave the OS pointer wherever the launcher left it, which
            // is almost never inside the game window; the probe then reads "not measured"
            // and skips. Park the pointer over the probe's own button for the measurement
            // and put it back afterwards. Windows only; anywhere else the placement reports
            // itself unavailable and the cell keeps its original skip.
            CursorPlacement placement = CursorPlacement.TryPlaceOverProbeButton();
            ParsekLog.Info("TestRunner", "DisabledHoverEcho_InGame: cursor placement " + placement.Describe());
            try
            {
                int guardFrames = 0;
                while (!probe.Completed && guardFrames < 240)
                {
                    guardFrames++;
                    yield return null;
                }

                if (probe.RepaintPasses == 0)
                {
                    InGameAssert.Skip(
                        $"probe never observed an IMGUI Repaint pass (frames={guardFrames}); cannot measure tooltip publication in this context");
                    yield break;
                }

                InGameAssert.IsFalse(probe.Faulted,
                    "Disabled-hover carrier threw an IMGUI exception while drawing: "
                        + probe.FaultMessage);

                if (!probe.PointerWasOverTheButton)
                {
                    // Nothing is wrong - the pointer simply was not over the probe area
                    // (mouse outside the game window during an unattended batch). Asserting
                    // would turn "not measured" into "broken".
                    InGameAssert.Skip(
                        "pointer was never inside the probe button rect (mouse outside the "
                        + "game window?); tooltip publication cannot be measured without a hover");
                    yield break;
                }

                if (!probe.EnabledButtonPublished)
                {
                    // The control case failed, so the probe itself cannot see tooltips here
                    // and any reading about the DISABLED case would be meaningless.
                    InGameAssert.Skip(
                        "an ENABLED button did not publish its tooltip in this context, so the "
                        + "probe cannot measure tooltip publication at all; nothing to conclude "
                        + "about disabled controls");
                    yield break;
                }

                // THE GATE: our carrier must deliver the reason for a disabled control.
                InGameAssert.IsTrue(probe.CarrierPublished,
                    "A disabled button with a DisabledHoverEcho carrier did not publish its "
                        + "reason to GUI.tooltip, so the window help strip would show nothing "
                        + "while the player hovers a greyed-out control. Observed tooltip: \""
                        + (probe.CarrierObservedTooltip ?? "<null>") + "\", expected \""
                        + DisabledHoverProbe.CarrierReason + "\"");

                InGameAssert.AreEqual(DisabledHoverProbe.CarrierReason,
                    probe.CarrierObservedTooltip,
                    "The carrier published a tooltip, but not the reason it was given");

                // Observation only - see the class doc. Never an assertion: the feature is
                // built so that either answer is fine, and pinning Unity's native behaviour
                // here would make a future Unity/KSP bump red for something Parsek does not
                // rely on.
                ParsekLog.Info("TestRunner",
                    "DisabledHoverEcho_InGame: PASS carrierPublished=true "
                    + $"nativeDisabledButtonPublished={probe.NativeDisabledButtonPublished} "
                    + $"(observation only, not asserted) "
                    + $"enabledControlPublished={probe.EnabledButtonPublished} "
                    + $"layoutPasses={probe.LayoutPasses} repaintPasses={probe.RepaintPasses}");
            }
            finally
            {
                placement.Restore();
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Parks the OS pointer inside the probe button's rect (the top 120 px of the game
        /// window's client area, full width) for the duration of the measurement, and
        /// restores the previous pointer position afterwards. Unity samples the pointer
        /// from the OS, so nothing inside the engine can fake a hover; moving the real
        /// pointer is the only way an unattended batch can measure tooltip publication.
        /// Every failure mode (not Windows, no user32, no game window found) degrades to
        /// "not placed", which leaves the cell on its original skip path.
        /// </summary>
        internal readonly struct CursorPlacement
        {
            private const int ProbeButtonHeightPx = 120;

            internal readonly bool Placed;
            internal readonly string Reason;
            private readonly int restoreX;
            private readonly int restoreY;

            private CursorPlacement(bool placed, string reason, int restoreX, int restoreY)
            {
                Placed = placed;
                Reason = reason;
                this.restoreX = restoreX;
                this.restoreY = restoreY;
            }

            internal string Describe()
            {
                return (Placed ? "placed" : "not placed") + " (" + Reason + ")";
            }

            internal static CursorPlacement TryPlaceOverProbeButton()
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                    return new CursorPlacement(false, "not Windows: " + Environment.OSVersion.Platform, 0, 0);
                try
                {
                    // This process's own main window first: a title lookup would find
                    // whichever KSP instance was created first (the dev instance sits outside
                    // the harness machine lock and may be open alongside a nightly), and the
                    // foreground window may be anything at all.
                    // On a harness-launched instance MainWindowHandle reads zero (measured on
                    // LT-1 run 2026-09-08_1114, which placed through the title lookup), so the
                    // title fallback is the path that carries the nightly; the process handle
                    // is what protects an operator with a second KSP open.
                    IntPtr window = IntPtr.Zero;
                    string source = "process";
                    try
                    {
                        window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                    }
                    catch (Exception)
                    {
                        window = IntPtr.Zero;
                    }
                    if (window == IntPtr.Zero)
                    {
                        window = NativeMethods.FindWindowW(null, "Kerbal Space Program");
                        source = "title";
                    }
                    if (window == IntPtr.Zero)
                    {
                        window = NativeMethods.GetForegroundWindow();
                        source = "foreground";
                    }
                    if (window == IntPtr.Zero)
                        return new CursorPlacement(false, "no game window handle", 0, 0);
                    if (!NativeMethods.GetClientRect(window, out NativeMethods.RECT client))
                        return new CursorPlacement(false, "GetClientRect failed", 0, 0);
                    int width = client.Right - client.Left;
                    int height = client.Bottom - client.Top;
                    if (width <= 0 || height < ProbeButtonHeightPx)
                        return new CursorPlacement(false, "client area " + width + "x" + height + " too small", 0, 0);
                    var target = new NativeMethods.POINT { X = width / 2, Y = ProbeButtonHeightPx / 2 };
                    if (!NativeMethods.ClientToScreen(window, ref target))
                        return new CursorPlacement(false, "ClientToScreen failed", 0, 0);
                    if (!NativeMethods.GetCursorPos(out NativeMethods.POINT previous))
                        return new CursorPlacement(false, "GetCursorPos failed", 0, 0);
                    if (!NativeMethods.SetCursorPos(target.X, target.Y))
                        return new CursorPlacement(false, "SetCursorPos refused", 0, 0);
                    return new CursorPlacement(true,
                        "window=" + source + " client=" + width + "x" + height
                        + " screen=(" + target.X + "," + target.Y + ") restore=(" + previous.X + "," + previous.Y + ")",
                        previous.X, previous.Y);
                }
                catch (Exception ex)
                {
                    // DllNotFoundException / EntryPointNotFoundException / SecurityException:
                    // the platform has no usable user32, so the cell measures nothing.
                    return new CursorPlacement(false, ex.GetType().Name + ": " + ex.Message, 0, 0);
                }
            }

            internal void Restore()
            {
                if (!Placed)
                    return;
                try
                {
                    NativeMethods.SetCursorPos(restoreX, restoreY);
                }
                catch (Exception)
                {
                    // Best effort: the pointer stays parked over the probe area.
                }
            }
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct POINT
            {
                public int X;
                public int Y;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr FindWindowW(string className, string windowName);

            [DllImport("user32.dll")]
            internal static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetCursorPos(out POINT point);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetCursorPos(int x, int y);
        }

        /// <summary>
        /// Probe MonoBehaviour. Draws one full-screen-width button per frame so the
        /// pointer is inside its rect wherever it sits in the game window, and cycles
        /// three states across frames (whole frames are one state, so the Repaint that
        /// reads <see cref="GUI.tooltip"/> matches the Layout that reserved the control):
        ///
        /// <list type="number">
        /// <item>ENABLED + tooltip - the control case. If this does not publish, the probe
        /// cannot measure anything here and the test skips.</item>
        /// <item>DISABLED + tooltip, NO carrier - measures Unity's native behaviour.
        /// Recorded, never asserted.</item>
        /// <item>DISABLED + NO tooltip + carrier - the mechanism under test.</item>
        /// </list>
        ///
        /// <para>State 3 deliberately gives the button ITSELF no tooltip, so a publish can
        /// only have come from the carrier. Were the button to keep its own tooltip, a
        /// native publish would mask a completely broken carrier.</para>
        /// </summary>
        private sealed class DisabledHoverProbe : MonoBehaviour
        {
            internal const string CarrierReason = "Parsek probe: stop recording before rewinding";
            private const string NativeTooltip = "Parsek probe: native disabled button tooltip";
            private const string EnabledTooltip = "Parsek probe: enabled button tooltip";

            internal bool Faulted;
            internal string FaultMessage = string.Empty;
            internal int LayoutPasses;
            internal int RepaintPasses;
            internal bool PointerWasOverTheButton;
            internal bool EnabledButtonPublished;
            internal bool NativeDisabledButtonPublished;
            internal bool CarrierPublished;
            internal string CarrierObservedTooltip = string.Empty;
            internal bool Completed;

            // 0 = enabled control, 1 = native disabled, 2 = carrier. Held for several
            // frames each so a dropped Repaint cannot skip a state.
            private int state;

            private void OnGUI()
            {
                if (Completed)
                    return;

                EventType evt = Event.current.type;
                if (evt != EventType.Layout && evt != EventType.Repaint)
                    return;

                if (evt == EventType.Layout)
                {
                    LayoutPasses++;
                    state = LayoutPasses <= 3 ? 0 : (LayoutPasses <= 6 ? 1 : 2);
                }

                GUILayout.BeginArea(new Rect(0f, 0f, Screen.width, Screen.height));
                try
                {
                    bool prevEnabled = GUI.enabled;
                    GUI.enabled = state == 0;

                    string buttonTooltip =
                        state == 0 ? EnabledTooltip : (state == 1 ? NativeTooltip : string.Empty);

                    GUILayout.Button(
                        new GUIContent("parsek-probe-button", buttonTooltip),
                        GUILayout.ExpandWidth(true), GUILayout.Height(120f));

                    if (state == 2)
                    {
                        // The mechanism under test, called exactly as the windows call it.
                        DisabledHoverEcho.CarryLastControl(false, CarrierReason);
                    }

                    if (evt == EventType.Repaint)
                    {
                        RepaintPasses++;
                        Rect buttonRect = GUILayoutUtility.GetLastRect();
                        bool over = DisabledHoverEcho.PointerInside(
                            buttonRect, Event.current.mousePosition);
                        if (over)
                            PointerWasOverTheButton = true;

                        string live = GUI.tooltip;
                        if (over)
                        {
                            if (state == 0)
                                EnabledButtonPublished |=
                                    string.Equals(live, EnabledTooltip, StringComparison.Ordinal);
                            else if (state == 1)
                                NativeDisabledButtonPublished |=
                                    string.Equals(live, NativeTooltip, StringComparison.Ordinal);
                            else if (string.Equals(live, CarrierReason, StringComparison.Ordinal))
                            {
                                CarrierPublished = true;
                                CarrierObservedTooltip = live;
                            }
                            else if (!CarrierPublished)
                            {
                                CarrierObservedTooltip = live;
                            }
                        }
                    }

                    GUI.enabled = prevEnabled;

                    // Trailing control mirrors a real window's Close row: a carrier that
                    // wrongly consumed a layout slot would desync the control count here
                    // and throw on Repaint.
                    GUILayout.Button("probe-close");
                }
                catch (Exception ex)
                {
                    Faulted = true;
                    FaultMessage = ex.Message;
                }
                finally
                {
                    GUILayout.EndArea();
                }

                if (LayoutPasses > 9)
                    Completed = true;
            }
        }
    }
}
