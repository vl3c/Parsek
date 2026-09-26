using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live check of <see cref="Patches.FlightStateGhostBudgetPatch"/> around a REAL stock
    /// <c>new FlightState()</c> (the build <c>Game.Updated</c> runs on every save), in the
    /// Tracking Station, where orbital committed recordings put ghost map ProtoVessels in
    /// <c>FlightGlobals.Vessels</c>. It sets the stock vessel budget to exactly the number of
    /// real (non-ghost) vessels, so without the patch every ghost would push one real Debris
    /// vessel out of the build; with it none may be dropped. It also proves the patch reached
    /// the real constructor (its one log line, with the exact counts) and that the global
    /// budget is back to the pre-build value afterwards.
    ///
    /// <para>
    /// Side effects are held to the build itself: <c>DECLUTTER_KSC</c> is turned off for the
    /// build so stock cannot flag a live landed debris vessel for autoclean, and both settings
    /// plus the log observer are restored in <c>finally</c>. The built state is discarded.
    /// </para>
    /// </summary>
    public class FlightStateGhostBudgetInGameTest
    {
        [InGameTest(Category = "VesselBudget", Scene = GameScenes.TRACKSTATION,
            Description = "A real FlightState() build excludes ghost map vessels from "
                + "MAX_VESSELS_BUDGET: at a budget equal to the real-vessel count no real vessel "
                + "is dropped, the patch logs the exact adjustment, and the budget is restored")]
        public void FlightStateBuild_GhostMapVessels_DoNotCountAgainstVesselBudget()
        {
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null)
            {
                InGameAssert.Skip("FlightGlobals.Vessels unavailable");
                return;
            }

            int ghosts = 0;
            int real = 0;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null || v.state == Vessel.State.DEAD)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    ghosts++;
                else
                    real++;
            }

            if (ghosts == 0)
            {
                InGameAssert.Skip("needs at least one ghost map vessel in the Tracking Station "
                    + "(a committed recording with an orbital ghost); found none");
                return;
            }

            int savedBudget = GameSettings.MAX_VESSELS_BUDGET;
            bool savedDeclutter = GameSettings.DECLUTTER_KSC;
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;
            var lines = new List<string>();
            FlightState built;
            int budgetAfterBuild;
            try
            {
                GameSettings.DECLUTTER_KSC = false;
                GameSettings.MAX_VESSELS_BUDGET = real;
                ParsekLog.TestObserverForTesting = line =>
                {
                    lines.Add(line);
                    prevObserver?.Invoke(line);
                };

                built = new FlightState();
                budgetAfterBuild = GameSettings.MAX_VESSELS_BUDGET;
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                GameSettings.MAX_VESSELS_BUDGET = savedBudget;
                GameSettings.DECLUTTER_KSC = savedDeclutter;
            }

            int savedGhosts = 0;
            int savedReal = 0;
            for (int i = 0; i < built.protoVessels.Count; i++)
            {
                ProtoVessel pv = built.protoVessels[i];
                if (pv == null)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                    savedGhosts++;
                else
                    savedReal++;
            }

            string counts = string.Format(CultureInfo.InvariantCulture,
                "(live real={0} ghosts={1}; built real={2} ghosts={3})",
                real, ghosts, savedReal, savedGhosts);
            ParsekLog.Info("TestRunner", "FlightStateGhostBudget: " + counts);

            InGameAssert.AreEqual(real, budgetAfterBuild,
                "the finalizer must restore MAX_VESSELS_BUDGET to its pre-build value " + counts);
            InGameAssert.AreEqual(real, savedReal,
                "no real vessel may be dropped when the budget equals the real-vessel count " + counts);
            InGameAssert.AreEqual(ghosts, savedGhosts,
                "every ghost map vessel is still in the built state (OnSave strips them) " + counts);

            string expectedFragment = string.Format(CultureInfo.InvariantCulture,
                "excluded {0} ghost map vessel(s)", ghosts);
            string expectedBudget = string.Format(CultureInfo.InvariantCulture,
                "MAX_VESSELS_BUDGET {0} -> {1}", real, real + ghosts);
            bool logged = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains("[GhostMap]")
                    && lines[i].Contains(expectedFragment)
                    && lines[i].Contains(expectedBudget))
                {
                    logged = true;
                    break;
                }
            }
            InGameAssert.IsTrue(logged,
                "the Harmony patch must reach the real FlightState() and log '"
                + expectedFragment + "' with '" + expectedBudget + "' " + counts);
        }
    }
}
