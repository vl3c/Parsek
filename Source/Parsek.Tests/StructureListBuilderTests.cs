using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StructureListBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StructureListBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStructureBuilder.SuppressLogging = false;
            MissionStructureListBuilder.SuppressLogging = false;
            RouteStructureListBuilder.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = false;
            MissionStructureListBuilder.SuppressLogging = false;
            RouteStructureListBuilder.SuppressLogging = false;
        }

        // --- Helpers ---

        private static Recording Rec(string id, double start, double end, string vessel = "V",
            string eva = null, TerminalState? terminal = null, string body = "Kerbin",
            string launchSite = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                EvaCrewName = eva,
                TerminalStateValue = terminal,
                StartBodyName = body,
                TerminalOrbitBody = body,
                LaunchSiteName = launchSite
            };
        }

        private static BranchPoint BP(string id, BranchPointType type, string[] parents,
            string[] children, double ut, string splitCause = null, uint decouplerPid = 0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
                SplitCause = splitCause,
                DecouplerPartId = decouplerPid
            };
        }

        private static RecordingTree Tree(Recording[] recs, BranchPoint[] bps = null, string id = "tree-1")
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static List<StructureStep> BuildMission(RecordingTree tree)
        {
            var structure = MissionStructureBuilder.Build(tree);
            return MissionStructureListBuilder.Build(tree, structure);
        }

        // --- Mission builder tests ---

        [Fact]
        public void Mission_SingleLaunchToLanded_EmitsLaunchAndTerminal()
        {
            var r1 = Rec("r1", 0, 300, vessel: "Probe", terminal: TerminalState.Landed,
                body: "Kerbin", launchSite: "LaunchPad");
            var steps = BuildMission(Tree(new[] { r1 }));

            Assert.Equal(2, steps.Count);
            Assert.Equal(StructureStepKind.Launch, steps[0].Kind);
            Assert.Equal(0, steps[0].UT);
            // Location is "body, biome/site" order; the launch site fills the biome slot.
            Assert.Equal("Kerbin, LaunchPad", steps[0].Location);
            Assert.Equal(StructureStepKind.Terminal, steps[1].Kind);
            // Terminal: Event = "End", Status carries the situation.
            Assert.Equal("End", steps[1].Label);
            Assert.Equal("Landed", steps[1].Status);
            Assert.Equal(300, steps[1].UT);
        }

        [Fact]
        public void Mission_ControlledDecouple_DebrisStaging_DedupsDecoupledByDecouplerPid()
        {
            var r1 = Rec("r1", 0, 500, vessel: "Stack", terminal: TerminalState.Landed);
            var r2 = Rec("r2", 100, 400, vessel: "Lander", terminal: TerminalState.Landed);
            // Controlled decouple branch point (pid 42) + a mirroring Decoupled part event
            // (pid 42, should be deduped), plus a debris Decoupled (pid 99, passes through),
            // plus a fairing jettison (passes through).
            r1.PartEvents.Add(new PartEvent { ut = 100, eventType = PartEventType.Decoupled, partPersistentId = 42, partName = "decoupler" });
            r1.PartEvents.Add(new PartEvent { ut = 150, eventType = PartEventType.Decoupled, partPersistentId = 99, partName = "booster" });
            r1.PartEvents.Add(new PartEvent { ut = 120, eventType = PartEventType.FairingJettisoned, partPersistentId = 7, partName = "fairing" });

            var bp = BP("bp1", BranchPointType.JointBreak, new[] { "r1" }, new[] { "r2" }, 100,
                splitCause: "DECOUPLE", decouplerPid: 42);
            var steps = BuildMission(Tree(new[] { r1, r2 }, new[] { bp }));

            // The controlled decouple is a Separation step; its mirroring pid-42 part event is gone.
            Assert.Contains(steps, s => s.Kind == StructureStepKind.Separation && s.UT == 100 && s.Label == "Decoupled");
            Assert.DoesNotContain(steps, s => s.Kind == StructureStepKind.Staging && s.SortPid == 42);
            // Debris staging (pid 99) and fairing pass through.
            Assert.Contains(steps, s => s.Kind == StructureStepKind.Staging && s.SortPid == 99);
            Assert.Contains(steps, s => s.Kind == StructureStepKind.Staging && s.Label == "Fairing jettisoned");
            // Both legs' terminals present.
            Assert.Equal(2, steps.Count(s => s.Kind == StructureStepKind.Terminal));
        }

        [Fact]
        public void Mission_SamePhysicalStaging_RecordedOnTwoRecordings_CollapsesToOneStep()
        {
            // The same booster (pid 99) decouples once but is recorded on both the parent
            // and a continuation recording at slightly different sampled UTs. The (pid,
            // eventType) dedup must collapse them to a single Staging step.
            var r1 = Rec("r1", 0, 200, vessel: "Stage1", terminal: TerminalState.Destroyed);
            var r2 = Rec("r2", 100, 500, vessel: "Stage2", terminal: TerminalState.Landed);
            r1.PartEvents.Add(new PartEvent { ut = 150.04, eventType = PartEventType.Decoupled, partPersistentId = 99, partName = "booster" });
            r2.PartEvents.Add(new PartEvent { ut = 150.06, eventType = PartEventType.Decoupled, partPersistentId = 99, partName = "booster" });

            var steps = BuildMission(Tree(new[] { r1, r2 }));

            Assert.Equal(1, steps.Count(s => s.Kind == StructureStepKind.Staging && s.SortPid == 99));
        }

        [Fact]
        public void Mission_DockThenUndock_AreOrderedByUT()
        {
            var r1 = Rec("r1", 0, 300, vessel: "Tug", terminal: TerminalState.Recovered);
            // External children (not in the tree) keep r1 a root while still tagging the events.
            var dock = BP("dock", BranchPointType.Dock, new[] { "r1" }, new[] { "ext" }, 100);
            var undock = BP("undock", BranchPointType.Undock, new[] { "r1" }, new[] { "ext2" }, 200);
            var steps = BuildMission(Tree(new[] { r1 }, new[] { dock, undock }));

            int dockIdx = steps.FindIndex(s => s.Kind == StructureStepKind.Dock);
            int undockIdx = steps.FindIndex(s => s.Kind == StructureStepKind.Undock);
            Assert.True(dockIdx >= 0 && undockIdx >= 0);
            Assert.True(dockIdx < undockIdx);
            Assert.Equal(100, steps[dockIdx].UT);
            Assert.Equal(200, steps[undockIdx].UT);
        }

        [Fact]
        public void Mission_Eva_EmitsEvaStep()
        {
            var r1 = Rec("r1", 0, 300, vessel: "Capsule", terminal: TerminalState.Recovered);
            var rEva = Rec("rEva", 50, 120, vessel: "Bob Kerman", eva: "Bob Kerman", terminal: TerminalState.Recovered);
            var eva = BP("eva", BranchPointType.EVA, new[] { "r1" }, new[] { "rEva" }, 50);
            var steps = BuildMission(Tree(new[] { r1, rEva }, new[] { eva }));

            Assert.Contains(steps, s => s.Kind == StructureStepKind.Eva && s.UT == 50 && s.Label == "EVA");
        }

        [Fact]
        public void Mission_Build_IsDeterministic()
        {
            var r1 = Rec("r1", 0, 500, vessel: "Stack", terminal: TerminalState.Landed);
            var r2 = Rec("r2", 100, 400, vessel: "Lander", terminal: TerminalState.Landed);
            r1.PartEvents.Add(new PartEvent { ut = 150, eventType = PartEventType.Decoupled, partPersistentId = 99, partName = "booster" });
            var bp = BP("bp1", BranchPointType.JointBreak, new[] { "r1" }, new[] { "r2" }, 100, "DECOUPLE", 42);

            var a = BuildMission(Tree(new[] { r1, r2 }, new[] { bp }));
            var b = BuildMission(Tree(new[] { r1, r2 }, new[] { bp }));

            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].UT, b[i].UT);
                Assert.Equal(a[i].Kind, b[i].Kind);
                Assert.Equal(a[i].Label, b[i].Label);
                Assert.Equal(a[i].VesselName, b[i].VesselName);
            }
        }

        [Fact]
        public void Mission_EmitsVerboseSummaryLine()
        {
            var r1 = Rec("r1", 0, 300, terminal: TerminalState.Landed);
            BuildMission(Tree(new[] { r1 }));
            Assert.Contains(logLines, l => l.Contains("[Mission]") && l.Contains("BuildStructureList:") && l.Contains("steps="));
        }

        // --- Route builder tests ---

        [Fact]
        public void Route_KscOrigin_OneStop_EmitsOrderedSteps()
        {
            var dockRec = new Recording
            {
                RecordingId = "rDock",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        DockUT = 100,
                        UndockUT = 200,
                        EndpointAtDock = new RouteEndpoint { BodyName = "Mun", IsSurface = false }
                    }
                }
            };
            var route = new RouteFixtureBuilder()
                .WithKscOrigin(true)
                .WithOrigin(new RouteEndpoint { BodyName = "Kerbin" })
                .WithDockBinding(150, "rDock")
                .WithStop(new RouteStop
                {
                    Endpoint = new RouteEndpoint { BodyName = "Mun", IsSurface = false },
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 50 } }
                })
                .Build();

            var steps = RouteStructureListBuilder.Build(route, id => id == "rDock" ? dockRec : null);

            Assert.Equal(4, steps.Count);
            Assert.Equal(StructureStepKind.Origin, steps[0].Kind);
            Assert.Equal("Origin: KSC", steps[0].Label);
            Assert.Equal("Kerbin, KSC", steps[0].Location);   // body, biome-slot (KSC)
            Assert.Equal("Prelaunch", steps[0].Status);
            Assert.Equal(StructureStepKind.Dock, steps[1].Kind);
            Assert.Equal("Orbiting", steps[1].Status);        // orbital endpoint
            Assert.Equal(100, steps[1].UT);
            Assert.Equal(StructureStepKind.Delivery, steps[2].Kind);
            Assert.Equal(150, steps[2].UT);
            Assert.Contains("50 LiquidFuel", steps[2].Label);
            Assert.Equal(StructureStepKind.Undock, steps[3].Kind);
            Assert.Equal(200, steps[3].UT);
        }

        [Fact]
        public void Route_VesselOrigin_LabelsDepot()
        {
            var route = new RouteFixtureBuilder()
                .WithKscOrigin(false)
                .WithOrigin(new RouteEndpoint { BodyName = "Minmus", IsSurface = true, Latitude = 10, Longitude = 20 })
                .WithStop(new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Mun" } })
                .Build();

            var steps = RouteStructureListBuilder.Build(route, id => null);

            Assert.Equal("Origin: depot", steps[0].Label);
            Assert.Contains("Minmus", steps[0].Location);   // body first, with surface coords
            Assert.Equal("Landed", steps[0].Status);         // surface endpoint
        }

        [Fact]
        public void Route_MissingSourceRecording_NoWindowSteps_NoThrow()
        {
            var route = new RouteFixtureBuilder()
                .WithKscOrigin(true)
                .WithOrigin(new RouteEndpoint { BodyName = "Kerbin" })
                .WithDockBinding(150, "missing")
                .WithStop(new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Mun" } })
                .Build();

            var steps = RouteStructureListBuilder.Build(route, id => null);

            Assert.DoesNotContain(steps, s => s.Kind == StructureStepKind.Dock);
            Assert.DoesNotContain(steps, s => s.Kind == StructureStepKind.Undock);
            Assert.Contains(steps, s => s.Kind == StructureStepKind.Origin);
            // Delivery still emitted at the recorded dock UT even without the window.
            Assert.Contains(steps, s => s.Kind == StructureStepKind.Delivery && s.UT == 150);
        }

        [Fact]
        public void Route_NullRoute_ReturnsEmpty()
        {
            var steps = RouteStructureListBuilder.Build(null, id => null);
            Assert.Empty(steps);
        }

        // --- Location formatter tests ---

        [Fact]
        public void LocationFormatter_BodyBiome_IsAlwaysBodyThenBiome()
        {
            Assert.Equal("Kerbin, Shores", StructureLocationFormatter.BodyBiome("Kerbin", "Shores"));
            Assert.Equal("Mun", StructureLocationFormatter.BodyBiome("Mun", null));
            Assert.Equal("Shores", StructureLocationFormatter.BodyBiome(null, "Shores"));
            Assert.Equal("-", StructureLocationFormatter.BodyBiome(null, null));
        }

        [Fact]
        public void LocationFormatter_Endpoint_KscSurfaceOrbit()
        {
            // KSC: body first, "KSC" in the biome slot; status Prelaunch.
            Assert.Equal("Kerbin, KSC",
                StructureLocationFormatter.EndpointLocation(new RouteEndpoint { BodyName = "Kerbin" }, true));
            Assert.Equal("Prelaunch",
                StructureLocationFormatter.EndpointStatus(new RouteEndpoint { BodyName = "Kerbin" }, true));

            // Surface: body first + coords; status Landed.
            RouteEndpoint surf = new RouteEndpoint { BodyName = "Mun", IsSurface = true, Latitude = 1, Longitude = 2 };
            Assert.StartsWith("Mun", StructureLocationFormatter.EndpointLocation(surf, false));
            Assert.Equal("Landed", StructureLocationFormatter.EndpointStatus(surf, false));

            // Orbit: body only; status Orbiting.
            RouteEndpoint orb = new RouteEndpoint { BodyName = "Duna", IsSurface = false };
            Assert.Equal("Duna", StructureLocationFormatter.EndpointLocation(orb, false));
            Assert.Equal("Orbiting", StructureLocationFormatter.EndpointStatus(orb, false));
        }
    }
}
