using System;
using System.Collections;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI regression guard for the bottom tooltip echo strip shared by the
    /// Logistics, Settings, Recordings, Real Spawn Control and test-runner windows
    /// (<see cref="TooltipEchoBox"/>).
    ///
    /// <para>It pins TWO properties that no headless unit test can reach, because both
    /// need a real Layout+Repaint cycle on Unity's own event loop:</para>
    /// <list type="number">
    ///   <item><b>Invariant control count.</b> The original QW6 echo block emitted a
    ///   DIFFERENT number of GUILayout controls between the Layout pass (GUI.tooltip
    ///   empty: 1 control) and the Repaint pass (GUI.tooltip populated: 2 controls),
    ///   so the trailing Close button overran its layout group with a continuous
    ///   "Getting control N's position in a group with only N controls when doing
    ///   repaint" exception on every hover. The probe draws a trailing button after
    ///   the strip and captures any exception.</item>
    ///   <item><b>Sizing cannot diverge from painting.</b> IMGUI sizes a control during
    ///   Layout and reuses that cached rect during Repaint, while GUI.tooltip only
    ///   becomes populated during Repaint - so a strip that read GUI.tooltip live
    ///   sized itself for the OLD text and painted the NEW one on every hover-start
    ///   frame (the one-frame dark sliver). The helper renders both passes from a
    ///   Repaint-captured cache; the probe reads that cache at the top of each pass
    ///   and asserts the Layout and Repaint halves of the same frame saw the same
    ///   string.</item>
    /// </list>
    ///
    /// <para>The InGameTestRunner coroutine does not reliably execute during OnGUI, so
    /// the test installs a tiny probe MonoBehaviour whose OWN OnGUI runs on the real
    /// event loop and drives a live <see cref="TooltipEchoBox"/> instance.</para>
    /// </summary>
    public sealed class LogisticsTooltipEchoImguiTest
    {
        [InGameTest(Category = "Logistics",
            Description = "Tooltip echo strip emits an invariant IMGUI control count across Layout/Repaint AND renders both passes of a frame from the same cached text, so a hover can neither overrun the trailing button's layout group nor size the strip for a different string than it paints")]
        public IEnumerator TooltipEchoBox_StableControlCount_NoImguiException()
        {
            var go = new GameObject("ParsekLogisticsTooltipEchoProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TooltipEchoImguiProbe probe = go.AddComponent<TooltipEchoImguiProbe>();

            try
            {
                // Give the probe several frames to run multiple full Layout/Repaint
                // cycles. Three clean repaint passes is enough: the hover text is fed
                // from frame 1, so frame 2 onward is the steady-hover state where the
                // cache is populated and the strip is at its wrapped height.
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
                    "Tooltip echo strip threw an IMGUI exception during Repaint with a populated hover text: "
                        + probe.FaultMessage);

                InGameAssert.AreEqual(0, probe.DivergedFrames,
                    "Tooltip echo strip rendered its Layout and Repaint passes from DIFFERENT text on "
                        + probe.DivergedFrames + " frame(s); sizing and painting can therefore disagree");

                InGameAssert.IsTrue(probe.ObservedPopulatedRepaint,
                    "probe never reached the steady-hover state (cache stayed empty), so the populated-hover path was not exercised");

                ParsekLog.Info("TestRunner",
                    $"LogisticsTooltipEcho_InGame: PASS layoutPasses={probe.LayoutPasses} repaintPasses={probe.RepaintPasses} " +
                    $"divergedFrames={probe.DivergedFrames} populatedRepaint={probe.ObservedPopulatedRepaint} faulted={probe.Faulted}");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Probe MonoBehaviour: each OnGUI draws a 1x1 layout area containing a live
        /// <see cref="TooltipEchoBox"/> followed by a trailing button (the control that
        /// overruns the group when the strip's count is not invariant). A non-empty
        /// manual override is handed in from the first pass, which is the helper's
        /// stand-in for a hovered control: the Repaint capture stores it, so the NEXT
        /// frame is the steady-hover state. The probe records the strip's cached text
        /// at the top of the Layout pass and again at the top of the Repaint pass of
        /// the same frame and counts any frame where the two differ.
        /// </summary>
        private sealed class TooltipEchoImguiProbe : MonoBehaviour
        {
            internal bool Faulted;
            internal string FaultMessage = string.Empty;
            internal int RepaintPasses;
            internal int LayoutPasses;
            internal int DivergedFrames;
            internal bool ObservedPopulatedRepaint;
            internal bool Completed;

            private const string HoverText = "parsek probe tooltip";

            private readonly TooltipEchoBox echo = new TooltipEchoBox();
            private string textSeenAtLayout;
            private bool haveLayoutText;

            private void OnGUI()
            {
                if (Completed)
                    return;

                EventType evt = Event.current.type;
                // Only Layout builds and Repaint reads the layout group; input
                // events do not exercise the control-count path.
                if (evt != EventType.Layout && evt != EventType.Repaint)
                    return;

                string textAtPassStart = echo.CachedText;
                if (evt == EventType.Layout)
                {
                    LayoutPasses++;
                    textSeenAtLayout = textAtPassStart;
                    haveLayoutText = true;
                }
                else if (haveLayoutText && !string.Equals(textSeenAtLayout, textAtPassStart, StringComparison.Ordinal))
                {
                    DivergedFrames++;
                    ParsekLog.Warn("TestRunner",
                        "LogisticsTooltipEcho_InGame probe saw a Layout/Repaint text divergence: layout='"
                            + textSeenAtLayout + "' repaint='" + textAtPassStart + "'");
                }

                if (evt == EventType.Repaint && textAtPassStart.Length > 0)
                    ObservedPopulatedRepaint = true;

                GUILayout.BeginArea(new Rect(0f, 0f, 1f, 1f));
                try
                {
                    // The manual override stands in for a hovered control: the helper
                    // captures it during Repaint, so from the next frame on the strip
                    // is in the steady-hover state the real windows reach.
                    echo.Draw(HoverText);
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
                    if (RepaintPasses >= 3 || Faulted)
                        Completed = true;
                }
            }
        }
    }
}
