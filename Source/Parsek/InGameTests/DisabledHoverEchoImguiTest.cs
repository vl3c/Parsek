using System;
using System.Collections;
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
                UnityEngine.Object.Destroy(go);
            }
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
