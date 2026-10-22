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
        /// Writes the parsed (pre-processed) rewind game to persistent.sfs before the Space Center
        /// load. <c>SpaceCenterMain.Start</c> reloads persistent.sfs rather than reading
        /// <see cref="HighLogic.CurrentGame"/> (the decompile fact behind the Discard Re-fly
        /// write), so without this the rewind lands in whatever world the last persistent write
        /// held, every surviving vessel in its latest form (todo
        /// REWIND-TO-LAUNCH-LANDS-IN-THE-LATEST-PERSISTENT-WORLD). The game's Parsek node is
        /// swapped for the last live-saved one already in persistent.sfs, the node the rewind's
        /// OnLoad read before this write, so Parsek's own state is unchanged and a crash before
        /// the next save cannot cold-load the rewind save's older Parsek node. Returns false (and
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
