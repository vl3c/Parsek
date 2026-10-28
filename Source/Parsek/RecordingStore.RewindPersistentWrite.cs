using System;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// Test seam: when non-null, replaces the persistent.sfs write of the parsed rewind game
        /// (<see cref="WriteRewindGameAsPersistent"/>) and returns whether it "wrote".
        /// </summary>
        internal static Func<Game, bool> RewindWritePersistentForTesting;

        /// <summary>
        /// Test seam: when non-null, replaces the persistent.sfs write after the rewind's deferred
        /// resource adjustment (<see cref="WritePersistentAfterRewindAdjustment"/>).
        /// </summary>
        internal static Func<bool> PostRewindPersistentWriteForTesting;

        /// <summary>
        /// Saves persistent.sfs once the rewind's OnLoad and its deferred resource adjustment have
        /// run. The file the rewind wrote before the Space Center load pairs the rewind save's
        /// world and stock pools with the last live Parsek node (the latest pool reference, no
        /// vessel holds); a crash before the next save would cold-load that mix, and the pool
        /// reference would read the rewind's pool drop as a leak. This write puts the patched
        /// pools, the holds and the stripped world on disk together. Returns whether it wrote.
        /// </summary>
        internal static bool WritePersistentAfterRewindAdjustment()
        {
            var seam = PostRewindPersistentWriteForTesting;
            if (seam != null)
            {
                bool ok = seam();
                ParsekLog.Info("Rewind",
                    $"Post-rewind persistent.sfs write ok={(ok ? "true" : "false")} (test seam)");
                return ok;
            }
            try
            {
                if (HighLogic.CurrentGame == null)
                {
                    ParsekLog.Warn("Rewind",
                        "Post-rewind persistent.sfs write skipped: HighLogic.CurrentGame is null");
                    return false;
                }
                string result = GamePersistence.SaveGame(
                    HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("Rewind", "Post-rewind persistent.sfs write returned empty");
                    return false;
                }
                ParsekLog.Info("Rewind",
                    $"Post-rewind persistent.sfs written saveFolder='{HighLogic.SaveFolder ?? "<none>"}' " +
                    $"holds={RewindVesselHold.Holds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Rewind",
                    $"Post-rewind persistent.sfs write threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes the parsed (pre-processed) rewind game to persistent.sfs before the Space Center
        /// load. <c>SpaceCenterMain.Start</c> reloads persistent.sfs rather than reading
        /// <see cref="HighLogic.CurrentGame"/> (the decompile fact behind the Discard Re-fly
        /// write), so without this the rewind lands in whatever world the last persistent write
        /// held, every surviving vessel in its latest form (todo
        /// REWIND-TO-LAUNCH-LANDS-IN-THE-LATEST-PERSISTENT-WORLD). The game's Parsek node is
        /// swapped for the last live-saved one already in persistent.sfs, the node the rewind's
        /// OnLoad read before this write, so Parsek's own state is unchanged and a crash before
        /// the next save cannot cold-load the rewind save's older Parsek node; the rewind saves
        /// again once its OnLoad and pool patch are done
        /// (<see cref="WritePersistentAfterRewindAdjustment"/>). Returns false (and
        /// logs) when nothing was written; the rewind goes on and lands on the old world.
        /// </summary>
        internal static bool WriteRewindGameAsPersistent(Game game, string messageLabel)
        {
            var seam = RewindWritePersistentForTesting;
            if (seam != null)
            {
                bool ok = seam(game);
                if (ok)
                    RewindContext.MarkRewindGameWrittenToPersistent();
                ParsekLog.Info("Rewind",
                    $"{messageLabel}: rewind game written to persistent.sfs ok={(ok ? "true" : "false")} (test seam)");
                return ok;
            }
            if (game == null)
            {
                ParsekLog.Warn("Rewind",
                    $"{messageLabel}: no rewind game to write to persistent.sfs - the Space Center will reload the last persistent.sfs");
                return false;
            }

            try
            {
                ConfigNode liveParsekNode = ParsekScenario.TryLoadCarryForwardSourceParsekNode(out string persistentPath);
                ConfigNode targetParsekNode = RevertInterceptor.FindParsekScenarioProtoNode(game);
                string spliceRefusal = RevertInterceptor.SpliceLiveParsekScenarioNode(targetParsekNode, liveParsekNode);
                if (spliceRefusal != null)
                {
                    ParsekLog.Warn("Rewind",
                        $"{messageLabel}: kept the old persistent.sfs ({spliceRefusal}, path='{persistentPath ?? "<none>"}') " +
                        "- the Space Center will reload it, not the rewind save's world");
                    return false;
                }
                // The carried node's ledger held-through UT is the session's; the file holds the
                // rewind save's world, so it carries the floor this rewind sets
                // (LedgerHeldWindow.ResolveAtRewind), and a crash before the next save cold-loads
                // the lead window's rows as reached.
                LedgerHeldWindow.WriteValue(targetParsekNode, LedgerHeldWindow.ResolveAtRewind(
                    RewindContext.RewindAdjustedUT, RewindContext.RewindSaveOwnClockUT,
                    RewindContext.RewindSaveLedgerHeldThroughUT));
                string result = GamePersistence.SaveGame(
                    game, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("Rewind",
                        $"{messageLabel}: writing the rewind game to persistent.sfs returned empty " +
                        "- the Space Center will reload the old persistent.sfs");
                    return false;
                }
                RewindContext.MarkRewindGameWrittenToPersistent();
                int vesselCount = game.flightState?.protoVessels?.Count ?? -1;
                ParsekLog.Info("Rewind",
                    $"{messageLabel}: rewind game written to persistent.sfs " +
                    $"saveFolder='{HighLogic.SaveFolder ?? "<none>"}' vessels={vesselCount} " +
                    $"UT={(game.flightState != null ? game.flightState.universalTime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "?")}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Rewind",
                    $"{messageLabel}: writing the rewind game to persistent.sfs threw {ex.GetType().Name}: {ex.Message} " +
                    "- the Space Center will reload the old persistent.sfs");
                return false;
            }
        }
    }
}
