using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>One view's re-photographed start pair, held until every view is in.</summary>
    internal sealed class PartParityStartPair
    {
        internal string View;
        internal byte[] RealTile;
        internal byte[] GhostTile;
        internal PartParityViewRecord Record;
    }

    /// <summary>The start-route reload pass of one engine case (see
    /// <see cref="PartParityRampHold"/>).</summary>
    internal static partial class PartParityFxDriver
    {
        /// <summary>
        /// After a case's body views: when a load would move a glow ramp of the real part, its
        /// ramps are put where the load puts them, the game runs a frame, and each view's start
        /// pair (real, start-route ghost) is photographed again into the <c>real-start</c> and
        /// <c>ghost-start</c> rows, which the view's <c>start</c> block is measured from. The event
        /// route, the stowed reference and every FX layer stay as they were. The ramps are put back,
        /// and a frame runs, before this returns, so nothing captured after sees them.
        /// </summary>
        private static IEnumerator CaptureStartReload(StepContext ctx, WaveCase wc)
        {
            if (!StartReloadApplies(wc)) yield break;
            List<PartParityRampHold> ramps = null;
            PartParitySweepRunner.Guard(ctx, wc, "fx-start-reload", () =>
            {
                ramps = PartParityRampHold.Find(wc.Real.rootPart, Planetarium.GetUniversalTime(), out bool unreadable);
                if (unreadable)
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: start reload: no readable ModuleColorChanger.{PartParityRampHold.FieldRamp}; " +
                        "the start route stays compared with the burning part");
            });
            if (ramps == null || ramps.Count == 0)
            {
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: start reload: no ramp a load would move");
                yield break;
            }
            string detail = string.Join("; ", ramps.ConvertAll(r => r.Describe()).ToArray());
            var pairs = new List<PartParityStartPair>();
            bool lost = false, loaded = false;
            try
            {
                PartParitySweepRunner.Guard(ctx, wc, "fx-start-reload", () =>
                {
                    foreach (PartParityRampHold r in ramps) r.Load();
                    loaded = true;
                });
                foreach (string view in loaded ? wc.CapturedViews.ToArray() : new string[0])
                {
                    yield return new WaitForEndOfFrame();
                    if (PartParitySweepRunner.CaseVesselLost(wc)) { lost = true; break; }
                    PartParitySweepRunner.Guard(ctx, wc, "fx-nozzle-glow", () => AdvanceNozzleGlows(wc));
                    PartParitySweepRunner.Guard(ctx, wc, "capture-start-" + view, () =>
                    {
                        PartParityStartPair p = PartParityStateDriver.CaptureStartPair(ctx, wc, view);
                        if (p != null) pairs.Add(p);
                    });
                }
            }
            finally
            {
                PartParitySweepRunner.Guard(ctx, wc, "fx-start-reload", () => RestoreRamps(wc, ramps));
            }
            if (lost) { LoseVessel(wc); yield break; }
            // All views or none: a row with a hole would pair a view's start block with the wrong side.
            if (pairs.Count == wc.CapturedViews.Count)
            {
                PartParityStateDriver.CommitStartPairs(wc, pairs);
                wc.Flags.Add(PartParityRampHold.Flag(ramps.Count));
                ParsekLog.Info(Tag, $"case {wc.CaseId}: start reload: {ramps.Count.ToString(Ic)} ramp(s) [{detail}] start pairs={pairs.Count.ToString(Ic)}");
            }
            else
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: start reload dropped: pairs={pairs.Count.ToString(Ic)} views={wc.CapturedViews.Count.ToString(Ic)} [{detail}]");
            // Two ends of frame, so one whole frame of the game's own Update runs on the live ramp
            // before any light or FX read.
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
        }

        /// <summary>An engine part case with a start-route ghost: a control, an rcs, heat or plate
        /// case has no throttle ramp to load.</summary>
        internal static bool StartReloadApplies(WaveCase wc)
            => wc?.Fx != null && wc.Status == null && wc.Fx.Control == null && wc.Fx.Option != null
               && wc.Fx.Option.Family == PartParityFxRules.FamilyPlume && wc.State != null && wc.State.FourRows
               && wc.State.StartGhost != null && wc.Real != null && wc.Real.rootPart != null && wc.CapturedViews.Count > 0;

        private static void RestoreRamps(WaveCase wc, List<PartParityRampHold> ramps)
        {
            double ut = Planetarium.GetUniversalTime();
            var at = new List<string>();
            foreach (PartParityRampHold r in ramps)
                if (r.Alive) at.Add(r.Restore(ut).ToString("0.000", Ic));
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: start reload: ramps restored to [{string.Join(",", at.ToArray())}]");
        }
    }
}
