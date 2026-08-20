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
    ///   <item><b>Constant reserved size.</b> This is the mechanism that replaced the
    ///   old Repaint-cached text: IMGUI sizes a control during Layout and reuses that
    ///   rect during Repaint, while GUI.tooltip is only populated during Repaint - so
    ///   any strip whose SIZE depends on the tooltip could size itself for one string
    ///   and paint another, and the window's height would move under the player as the
    ///   pointer crossed a control. The probe measures the rect the strip actually
    ///   reserved on a frame with NO text and again on a frame with a long multi-line
    ///   tooltip, and asserts the two are the same height.</item>
    /// </list>
    ///
    /// <para>The InGameTestRunner coroutine does not reliably execute during OnGUI, so
    /// the test installs a tiny probe MonoBehaviour whose OWN OnGUI runs on the real
    /// event loop and drives a live <see cref="TooltipEchoBox"/> instance.</para>
    /// </summary>
    public sealed class LogisticsTooltipEchoImguiTest
    {
        [InGameTest(Category = "Logistics",
            Description = "Tooltip echo strip emits an invariant IMGUI control count across Layout/Repaint AND reserves the same fixed height whether it is empty or showing a long tooltip, so a hover can neither overrun the trailing button's layout group nor change the window's height")]
        public IEnumerator TooltipEchoBox_StableControlCount_NoImguiException()
        {
            var go = new GameObject("ParsekLogisticsTooltipEchoProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TooltipEchoImguiProbe probe = go.AddComponent<TooltipEchoImguiProbe>();

            try
            {
                // Give the probe several frames to run multiple full Layout/Repaint
                // cycles: it needs at least one measured empty frame and one measured
                // populated frame.
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

                var ic = System.Globalization.CultureInfo.InvariantCulture;

                if (probe.EmptyHeight <= 0f || probe.PopulatedHeight <= 0f)
                {
                    InGameAssert.Skip(
                        $"probe could not measure both strip states (empty={probe.EmptyHeight.ToString("F2", ic)} "
                        + $"populated={probe.PopulatedHeight.ToString("F2", ic)}); nothing to compare");
                    yield break;
                }

                // The whole stability contract in one number: the rect the strip
                // reserves does not depend on the text it echoes.
                InGameAssert.ApproxEqual(probe.EmptyHeight, probe.PopulatedHeight, 0.01f,
                    "Tooltip echo strip reserved "
                        + probe.PopulatedHeight.ToString("F2", ic)
                        + "px while showing a " + probe.TooltipLength
                        + "-character tooltip against "
                        + probe.EmptyHeight.ToString("F2", ic)
                        + "px while empty; the strip's height still depends on its text, so the window "
                        + "changes height when the pointer crosses a control");

                ParsekLog.Info("TestRunner",
                    $"LogisticsTooltipEcho_InGame: PASS layoutPasses={probe.LayoutPasses} repaintPasses={probe.RepaintPasses} " +
                    $"emptyHeight={probe.EmptyHeight.ToString("F2", ic)} populatedHeight={probe.PopulatedHeight.ToString("F2", ic)} " +
                    $"faulted={probe.Faulted}");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Probe MonoBehaviour: each OnGUI draws a 1x1 layout area containing a live
        /// <see cref="TooltipEchoBox"/> followed by a trailing button (the control that
        /// overruns the group when the strip's count is not invariant). The hover text
        /// is switched at the START of a Layout pass, so a whole frame is either the
        /// empty state or the populated state, and the height measured on Repaint is
        /// the height that frame's Layout actually reserved.
        /// </summary>
        private sealed class TooltipEchoImguiProbe : MonoBehaviour
        {
            internal bool Faulted;
            internal string FaultMessage = string.Empty;
            internal int RepaintPasses;
            internal int LayoutPasses;
            internal float EmptyHeight;
            internal float PopulatedHeight;
            internal int TooltipLength;
            internal bool Completed;

            // Long enough to wrap past the strip's two lines in a 1px-wide area, which
            // is exactly the case that must NOT grow the reserved rect.
            private const string HoverText =
                "parsek probe tooltip: this hover text is deliberately far longer than the two " +
                "lines the help strip reserves, so a strip that still sized itself from its text " +
                "would reserve a visibly taller rect on this frame than on an empty one";

            private readonly TooltipEchoBox echo = new TooltipEchoBox();
            // Frames 1-2 empty, frames 3+ populated (one settling frame each way).
            private bool populatedFrame;

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
                {
                    LayoutPasses++;
                    populatedFrame = LayoutPasses > 2;
                }

                GUILayout.BeginArea(new Rect(0f, 0f, 1f, 1f));
                try
                {
                    // The manual override stands in for a hovered control: the helper
                    // reads it live, so this pass renders the state under test.
                    echo.Draw(populatedFrame ? HoverText : null);
                    if (evt == EventType.Repaint)
                    {
                        float height = GUILayoutUtility.GetLastRect().height;
                        if (populatedFrame)
                        {
                            PopulatedHeight = height;
                            TooltipLength = HoverText.Length;
                        }
                        else
                        {
                            EmptyHeight = height;
                        }
                    }
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
                    // Four full frames: two empty, two populated.
                    if (RepaintPasses >= 4 || Faulted)
                        Completed = true;
                }
            }
        }
    }
}
