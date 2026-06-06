using System;
using System.Collections;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI regression guard for the Logistics window bottom tooltip echo box.
    /// The original QW6 echo block emitted a DIFFERENT number of GUILayout controls
    /// between the Layout pass (GUI.tooltip empty: 1 control) and the Repaint pass
    /// (GUI.tooltip populated: 2 controls). GUI.tooltip is empty during Layout and
    /// only becomes populated during Repaint, so the count diverged and the trailing
    /// Close button overran its layout group, throwing a continuous
    /// "Getting control N's position in a group with only N controls when doing
    /// repaint" exception on every hover.
    ///
    /// This cannot be caught by a headless unit test (it needs a real IMGUI
    /// Layout+Repaint cycle), and the InGameTestRunner coroutine itself does not
    /// reliably execute during OnGUI. So the test installs a tiny probe
    /// MonoBehaviour whose OWN OnGUI runs on Unity's real event loop, feeds the real
    /// <see cref="LogisticsWindowUI.DrawTooltipEchoBox"/> an empty tooltip during
    /// Layout and a populated one during Repaint (the exact field condition), draws
    /// a trailing button after it (mirroring the Close button), and captures any
    /// exception. With the fix the echo box emits an invariant control count, so no
    /// exception fires.
    /// </summary>
    public sealed class LogisticsTooltipEchoImguiTest
    {
        [InGameTest(Category = "Logistics",
            Description = "Logistics tooltip echo box emits an invariant IMGUI control count across Layout/Repaint, so a populated GUI.tooltip during Repaint does not overrun the trailing button's layout group")]
        public IEnumerator TooltipEchoBox_StableControlCount_NoImguiException()
        {
            var go = new GameObject("ParsekLogisticsTooltipEchoProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TooltipEchoImguiProbe probe = go.AddComponent<TooltipEchoImguiProbe>();

            try
            {
                // Give the probe several frames to run multiple full Layout/Repaint
                // cycles. Two clean repaint passes is enough to prove the count is
                // stable; the probe sets Completed once it reaches that (or faults).
                int guardFrames = 0;
                while (!probe.Completed && guardFrames < 240)
                {
                    guardFrames++;
                    yield return null;
                }

                if (probe.RepaintPasses == 0)
                {
                    InGameAssert.Skip(
                        $"probe never observed an IMGUI Repaint pass (frames={guardFrames}); cannot validate the echo box in this context");
                    yield break;
                }

                InGameAssert.IsFalse(probe.Faulted,
                    "Logistics tooltip echo box threw an IMGUI exception during Repaint with a populated GUI.tooltip: "
                        + probe.FaultMessage);

                ParsekLog.Info("TestRunner",
                    $"LogisticsTooltipEcho_InGame: PASS layoutPasses={probe.LayoutPasses} repaintPasses={probe.RepaintPasses} faulted={probe.Faulted}");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Probe MonoBehaviour: each OnGUI draws a 1x1 layout area containing the real
        /// <see cref="LogisticsWindowUI.DrawTooltipEchoBox"/> followed by a trailing
        /// button. It feeds the echo box an empty tooltip on the Layout event and a
        /// non-empty one on the Repaint event, reproducing the divergence that throws
        /// "Getting control N's position in a group with only N controls when doing
        /// repaint" if the echo box control count is not invariant. The trailing
        /// button is the control that overruns the group.
        /// </summary>
        private sealed class TooltipEchoImguiProbe : MonoBehaviour
        {
            internal bool Faulted;
            internal string FaultMessage = string.Empty;
            internal int RepaintPasses;
            internal int LayoutPasses;
            internal bool Completed;

            private void OnGUI()
            {
                if (Completed)
                    return;

                EventType evt = Event.current.type;
                // Only Layout builds and Repaint reads the layout group; input
                // events do not exercise the control-count path.
                if (evt != EventType.Layout && evt != EventType.Repaint)
                    return;

                if (evt == EventType.Layout)
                    LayoutPasses++;

                // GUI.tooltip is empty during Layout and populated during Repaint in
                // the real window (it reflects the hovered control, whose tooltip
                // only resolves at Repaint). Reproduce that exact divergence.
                string tooltip = evt == EventType.Repaint ? "parsek probe tooltip" : string.Empty;

                GUILayout.BeginArea(new Rect(0f, 0f, 1f, 1f));
                try
                {
                    LogisticsWindowUI.DrawTooltipEchoBox(tooltip);
                    // Trailing controls mirror the real Close button block.
                    GUILayout.Space(3f);
                    GUILayout.Button("probe-close");
                }
                catch (Exception ex)
                {
                    if (!Faulted)
                    {
                        Faulted = true;
                        FaultMessage = ex.GetType().Name + ": " + ex.Message;
                        ParsekLog.Warn("TestRunner",
                            "LogisticsTooltipEcho_InGame probe caught IMGUI exception: " + FaultMessage);
                    }
                }
                finally
                {
                    GUILayout.EndArea();
                }

                if (evt == EventType.Repaint)
                {
                    RepaintPasses++;
                    if (RepaintPasses >= 2 || Faulted)
                        Completed = true;
                }
            }
        }
    }
}
