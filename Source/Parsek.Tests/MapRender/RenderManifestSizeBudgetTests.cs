using System;
using System.Globalization;
using System.IO;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// M-A7 Phase 1 SIZE BUDGET (design risk #1: "manifest volume on 20+ routes"). The per-pid caps
    /// are the guard, but the budget must be MEASURED, not assumed. This cell forges a 20-unit,
    /// two-cycle accumulation through the pure core - the scale the module targets - and pins the
    /// serialized size under 512 KB.
    ///
    /// <para>If this reds, the fix is a tighter cap or a coarser record, never a bigger budget: the
    /// manifest is a diagnostic artifact copied into every run's shot folder.</para>
    /// </summary>
    public class RenderManifestSizeBudgetTests
    {
        private const int BudgetBytes = 512 * 1024;
        private const int Units = 20;
        private const int Cycles = 2;

        [Fact]
        public void TwentyUnitsTwoCycles_SerializeUnderTheBudget()
        {
            RenderCompositionManifest m = BuildSyntheticScene();
            string text = Serialize(m);

            Assert.True(text.Length < BudgetBytes,
                string.Format(CultureInfo.InvariantCulture,
                    "Render manifest for {0} units x {1} cycles serialized to {2} bytes, over the "
                    + "{3}-byte budget. Tighten a cap or coarsen a record - do not raise the budget.",
                    Units, Cycles, text.Length, BudgetBytes));

            // Anti-vacuity: the budget must be measured against a manifest that actually carries the
            // scene, not an empty one.
            Assert.True(text.Length > 20000,
                "The synthetic scene serialized to only " + text.Length
                + " bytes - the budget cell is measuring nothing.");
            Assert.Equal(Units, m.PlanUnitCount);
            Assert.True(m.ClosedDwellCount >= Units * Cycles);
        }

        private static RenderCompositionManifest BuildSyntheticScene()
        {
            var m = new RenderCompositionManifest();
            for (int u = 0; u < Units; u++)
            {
                var unit = new RenderCompositionManifest.PlanUnitRecord
                {
                    Host = "TrackingStation",
                    SignatureHash = RenderCompositionManifest.StableHash("sig-" + u),
                    OwnerIndex = u * 10,
                    SpanStartUT = 1000.0 + u,
                    SpanEndUT = 9000.0 + u,
                    CadenceSeconds = 8000.0,
                    OverlapCadenceSeconds = 8000.0,
                    PhaseAnchorUT = 1000.0 + u,
                    IsReaim = (u % 3) == 0,
                    ArrivalHoldSeconds = 900.0,
                    ArrivalHoldAtUT = 6000.0,
                    ArrivalAlignPeriodSeconds = 21549.425,
                    LaunchBodyRotationPeriodSeconds = 21549.425,
                    RecordedSoiExitUT = 2500.0,
                    RecordedDeorbitUT = 7200.0,
                    DescentEndUT = 8100.0,
                    DestinationBodyRotationPeriodSeconds = 88642.6,
                    LoiterPeriodSeconds = 1800.0,
                    CaptureShiftSeconds = -450.5,
                    ParkingConicEndUT = 6749.5,
                    TransferMemberIndex = u * 10 + 1,
                    FirstDeorbitLegStartUT = 7100.0,
                    TransferMemberRecordingId = "rec-" + u + "-transfer",
                };
                for (int mem = 0; mem < 4; mem++)
                {
                    unit.Members.Add(new RenderCompositionManifest.PlanMemberRecord
                    {
                        Index = u * 10 + mem,
                        RecId = "rec-" + u + "-" + mem,
                        StartUT = 1000.0 + mem * 500.0,
                        EndUT = 1500.0 + mem * 500.0,
                    });
                }
                unit.LoiterCuts.Add(new RenderCompositionManifest.PlanCutRecord
                { StartUT = 3000.0, LengthSeconds = 3600.0 });
                m.AppendPlanUnit(unit);
            }

            for (int u = 0; u < Units; u++)
            {
                uint pid = (uint)(100000 + u * 1111);
                string recId = "rec-" + u + "-0";
                string sig = "rec-" + u + "-0|1000|9000|4|4200|w1";

                var chain = new RenderCompositionManifest.ChainBuildRecord
                {
                    Pid = pid,
                    RecId = recId,
                    CommittedIndex = u * 10,
                    UT = 1000.0,
                    Signature = sig,
                    WindowIndex = 1,
                    Provenance = "spine",
                    HasReaimedSegments = (u % 3) == 0,
                };
                for (int p = 0; p < 5; p++)
                {
                    chain.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord
                    {
                        Kind = "heliocentric-transfer",
                        Provenance = "recorded",
                        Body = "Sun",
                        StartUT = 1000.0 + p * 400.0,
                        EndUT = 1400.0 + p * 400.0,
                    });
                    if (p > 0)
                    {
                        chain.Seams.Add(new RenderCompositionManifest.ChainSeamRecord
                        { BoundaryIndex = p, Kind = (p % 2) == 0 ? "rigid" : "flexible-soi" });
                    }
                }
                m.AppendChainBuild(chain);

                // Two cycles, each walking five phases with several frames per phase.
                double ut = 1000.0;
                for (int cycle = 0; cycle < Cycles; cycle++)
                {
                    m.AppendClockEventIfChanged(
                        RenderCompositionManifest.ClockCycleRollover, u * 10, cycle, ut, 0.0, ut, 0.0, null);
                    for (int seg = 0; seg < 5; seg++)
                    {
                        for (int f = 0; f < 6; f++)
                        {
                            var s = default(RenderCompositionManifest.DwellSample);
                            s.Pid = pid;
                            s.RecId = recId;
                            s.CommittedIndex = u * 10;
                            s.ChainSignature = sig;
                            s.SegmentIndex = seg;
                            s.PhaseKind = "heliocentric-transfer";
                            s.Treatment = (seg % 2) == 0 ? "StockConic" : "TracedPath";
                            s.Visible = true;
                            s.Coverage = "InSegment";
                            s.FrameBody = "Sun";
                            s.CurrentUT = ut;
                            s.HeadUT = ut;
                            s.WarpRate = 1000.0;
                            s.MarkerDecision = true;
                            s.HasTruth = true;
                            s.TruthBody = "Sun";
                            s.TruthX = 1.0e10 + ut;
                            s.TruthY = -2.0e10 + ut;
                            s.TruthZ = 3.0e9 + ut;
                            m.ObserveDwellFrame(in s);
                            ut += 100.0;
                        }
                    }
                    m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
                    {
                        Pid = pid,
                        RecId = recId,
                        UT = ut,
                        Reason = (cycle % 2) == 0 ? "visible-body-frame" : "past-body-frame-end",
                        LineActive = (cycle % 2) == 0,
                        DrawIcons = 2,
                        IconSuppressed = false,
                        Coverage = (cycle % 2) == 0 ? "Inside" : "Outside",
                    });
                    m.AppendOwnershipChange(recId, ut, appeared: (cycle % 2) == 0);
                    m.AppendSeamTangent(new RenderCompositionManifest.SeamTangentRecord
                    {
                        Pid = pid,
                        RecId = recId,
                        LegIndex = cycle,
                        UT = ut,
                        Continuous = true,
                        AngleRadians = 0.0123456789,
                        ToleranceRadians = 0.1,
                    });
                }
                m.CloseOpenDwell(pid, ut);
            }

            return m;
        }

        private static string Serialize(RenderCompositionManifest m)
            => RenderManifestSampleFixtureTests.SerializeConfigNode(
                m.BuildFileNode(new RenderCompositionManifest.ManifestHeader(
                    999999.0, "verb", "TRACKSTATION", "budget", true, false, true)),
                "parsek-render-manifest-budget-");
    }
}
