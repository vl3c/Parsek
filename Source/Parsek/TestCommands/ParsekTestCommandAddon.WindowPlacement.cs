using System;
using System.Collections;
using System.Diagnostics;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Boot-time window placement for parallel harness runs (<see cref="WindowPlacementRun"/>):
    /// started once from <see cref="Awake"/> when the seam is armed, stepped once per frame
    /// (frames run during LOADING too, before the pump's own gates), and finished with one
    /// Info line. Never throws out of the coroutine and never fails anything.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private IEnumerator RunWindowPlacement()
        {
            WindowPlacementConfig config = WindowPlacementRun.ParseConfig(
                Environment.GetEnvironmentVariable(WindowPlacementRun.DesktopIdEnvVar),
                Environment.GetEnvironmentVariable(WindowPlacementRun.ReturnForegroundEnvVar));
            var clock = Stopwatch.StartNew();
            var run = new WindowPlacementRun(config, 0.0);
            IWindowPlacementPlatform platform = new AutomationWindowPlacementNative();
            while (true)
            {
                bool finished;
                try
                {
                    finished = run.Step(clock.Elapsed.TotalSeconds, platform);
                }
                catch (Exception ex)
                {
                    run.FinishOnException(clock.Elapsed.TotalSeconds, ex);
                    finished = true;
                }
                if (finished) break;
                yield return null;
            }
            ParsekLog.Info(Tag, run.Line);
        }
    }
}
