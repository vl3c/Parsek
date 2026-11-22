using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests.RealChuteStandIn
{
    // The shape of RealChute's live types (github.com/StupidChris/RealChute, Parachute.cs and
    // RealChuteModule.cs): public fields only, the ones Parsek reads plus a few it must ignore.
    public enum DeploymentStates { NONE, STOWED, PREDEPLOYED, LOWDEPLOYED, DEPLOYED, CUT }

    public class Parachute
    {
        public string material = "Nylon";
        public float preDeployedDiameter = 1f, deployedDiameter = 25f;
        public float deploymentAlt = 700f, cutAlt = -1f;
        public string depState = "STOWED";
        public double currentArea;
        internal DeploymentStates state = DeploymentStates.NONE;
    }

    public class RealChuteModule
    {
        public bool armed;
        public List<Parachute> parachutes = new List<Parachute>();
    }

    public class RealChuteModuleSubclass : RealChuteModule
    {
    }

    public class NotRealChuteModule
    {
        public List<Parachute> parachutes = new List<Parachute>();
    }

    public class RealChuteModuleWithoutList
    {
    }
}

namespace Parsek.Tests
{
    using Parsek.Tests.RealChuteStandIn;

    /// <summary>
    /// ABANDONED-STAGE-REALCHUTE-LOADED: a LOADED RealChute stage is read as under chutes
    /// (reflection over RealChute's public fields into the node shape RealChute persists), and
    /// a RealChute stage's full-canopy estimate scales by its open canopy area.
    /// </summary>
    [Collection("Sequential")]
    public class RealChuteLiveReadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RealChuteLiveReadTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static Parachute Canopy(string depState, float alt, float pre, float dep)
        {
            return new Parachute
            {
                depState = depState,
                deploymentAlt = alt,
                preDeployedDiameter = pre,
                deployedDiameter = dep,
            };
        }

        private static RealChuteModule Module(params Parachute[] canopies)
        {
            var m = new RealChuteModule();
            m.parachutes.AddRange(canopies);
            return m;
        }

        private static ConfigNode PersistedModule(params (string depState, string alt, string pre, string dep)[] canopies)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", "RealChuteModule");
            foreach (var (depState, alt, pre, dep) in canopies)
            {
                ConfigNode c = node.AddNode("PARACHUTE");
                c.AddValue("depState", depState);
                if (alt != null) c.AddValue("deploymentAlt", alt);
                if (pre != null) c.AddValue("preDeployedDiameter", pre);
                if (dep != null) c.AddValue("deployedDiameter", dep);
            }
            return node;
        }

        private static double Area(double d) => Math.PI * d * d / 4.0;

        // ------------------------------------------------------------- type match

        [Fact]
        public void ModuleType_IsMatchedByNameOnItselfOrABase()
        {
            Assert.True(RealChuteLiveRead.IsRealChuteModuleType(typeof(RealChuteModule)));
            Assert.True(RealChuteLiveRead.IsRealChuteModuleType(typeof(RealChuteModuleSubclass)));
            Assert.False(RealChuteLiveRead.IsRealChuteModuleType(typeof(NotRealChuteModule)));
            Assert.False(RealChuteLiveRead.IsRealChuteModuleType(typeof(object)));
            Assert.False(RealChuteLiveRead.IsRealChuteModuleType(null));
        }

        // ------------------------------------------------------------- reflection read

        // catches: a loaded RealChute stage under an open canopy read as having no chute (the
        // loaded half read only ModuleParachute), so it was predicted to fall and crash.
        [Fact]
        public void LoadedModule_ReadsLikeThePersistedNode()
        {
            RealChuteModule live = Module(
                Canopy("CUT", 2500f, 1f, 4f),
                Canopy("PREDEPLOYED", 800f, 1.5f, 25f),
                Canopy("STOWED", 300f, 1f, 25f));
            Assert.True(RealChuteLiveRead.TryBuildModuleNode(
                live, out ConfigNode node, out int unread, out string failure));
            Assert.Null(failure);
            Assert.Equal(0, unread);
            Assert.Equal(3, node.GetNodes("PARACHUTE").Length);

            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "RealChuteModule", node, out string state, out double alt));
            Assert.Equal("SEMIDEPLOYED", state);
            Assert.Equal(800.0, alt);

            ConfigNode persisted = PersistedModule(
                ("CUT", "2500", "1", "4"), ("PREDEPLOYED", "800", "1.5", "25"), ("STOWED", "300", "1", "25"));
            var fromLive = new OpenChuteTally();
            fromLive.NoteRealChuteModule(node, 77u);
            var fromSave = new OpenChuteTally();
            fromSave.NoteRealChuteModule(persisted, 77u);
            Assert.Equal(fromSave.Canopy, fromLive.Canopy);
            Assert.Equal(fromSave.FullDeployAltitudeAgl, fromLive.FullDeployAltitudeAgl);
            Assert.Equal(fromSave.RealChuteAreaNow, fromLive.RealChuteAreaNow, 9);
            Assert.Equal(fromSave.RealChuteAreaFull, fromLive.RealChuteAreaFull, 9);
            Assert.Equal(ParachuteCanopyState.SemiDeployed, fromLive.Canopy);
            Assert.Equal(77u, fromLive.FirstChutePid);
            Assert.Equal(1, fromLive.OpenCount);
        }

        [Fact]
        public void LoadedModule_FullyDeployed_IsDeployed()
        {
            Assert.True(RealChuteLiveRead.TryBuildModuleNode(
                Module(Canopy("DEPLOYED", 700f, 1f, 25f)), out ConfigNode node, out _, out _));
            Assert.Equal(ParachuteCanopyState.Deployed, ParachuteDescentLiveReader.ClassifyProtoModule(
                "RealChuteModule", node, out _));
        }

        // catches (owner ruling Q10b): an armed RealChute canopy (still STOWED) or a cut one
        // read live as open.
        [Theory]
        [InlineData("STOWED")]
        [InlineData("CUT")]
        [InlineData("NONE")]
        public void LoadedModule_NotOpen_DoesNotCount(string depState)
        {
            RealChuteModule live = Module(Canopy(depState, 700f, 1f, 25f));
            live.armed = true;
            Assert.True(RealChuteLiveRead.TryBuildModuleNode(live, out ConfigNode node, out _, out _));
            var tally = new OpenChuteTally();
            tally.NoteRealChuteModule(node, 5u);
            Assert.Equal(ParachuteCanopyState.None, tally.Canopy);
            Assert.Equal(0, tally.OpenCount);
            Assert.Equal(1, tally.RealChuteModules);
            Assert.Equal(0, tally.RealChuteOpenModules);
        }

        [Fact]
        public void LoadedModule_UnreadableShapes_FailWithAReason()
        {
            Assert.False(RealChuteLiveRead.TryBuildModuleNode(null, out _, out _, out string f0));
            Assert.Equal("module-null", f0);
            Assert.False(RealChuteLiveRead.TryBuildModuleNode(
                new RealChuteModuleWithoutList(), out _, out _, out string f1));
            Assert.Equal("canopy-list-field-missing", f1);
            Assert.False(RealChuteLiveRead.TryBuildModuleNode(
                new RealChuteModule { parachutes = null }, out _, out _, out string f2));
            Assert.Equal("canopy-list-null", f2);
        }

        [Fact]
        public void LoadedModule_ACanopyWithNoState_IsSkippedAndCounted()
        {
            RealChuteModule live = Module(Canopy("DEPLOYED", 700f, 1f, 25f));
            live.parachutes.Add(null);
            live.parachutes.Add(new Parachute { depState = null });
            Assert.True(RealChuteLiveRead.TryBuildModuleNode(live, out ConfigNode node, out int unread, out _));
            Assert.Equal(2, unread);
            Assert.Single(node.GetNodes("PARACHUTE"));
        }

        // ------------------------------------------------------------- canopy area

        [Fact]
        public void CanopyArea_PredeployedUsesItsPreDiameter_OthersTheDeployedOne()
        {
            ConfigNode node = PersistedModule(
                ("PREDEPLOYED", "800", "2", "20"),
                ("LOWDEPLOYED", "800", "1", "10"),
                ("DEPLOYED", "800", "1", "5"),
                ("STOWED", "800", "1", "30"),
                ("CUT", "800", "1", "30"));
            Assert.True(ParachuteProtoModuleRead.TryReadRealChuteCanopyArea(
                node, out double now, out double full, out int open));
            Assert.Equal(3, open);
            Assert.Equal(Area(2) + Area(10) + Area(5), now, 9);
            Assert.Equal(Area(20) + Area(10) + Area(5), full, 9);
        }

        // An open canopy with no diameters poisons the sums, so the planner falls back
        // instead of scaling from a partial sum (as for an unread stock chute).
        [Fact]
        public void CanopyArea_OpenCanopyWithoutDiameters_IsNaN()
        {
            ConfigNode node = PersistedModule(("DEPLOYED", "800", "1", "25"), ("PREDEPLOYED", "800", null, "25"));
            Assert.True(ParachuteProtoModuleRead.TryReadRealChuteCanopyArea(
                node, out double now, out double full, out int open));
            Assert.Equal(2, open);
            Assert.True(double.IsNaN(now));
            Assert.True(double.IsNaN(full));
            Assert.False(ParachuteProtoModuleRead.TryReadRealChuteCanopyArea(null, out _, out _, out _));
        }

        // ------------------------------------------------------------- combine

        // The stock sums are what the reader produced before RealChute was read.
        [Fact]
        public void CombineDrag_StockOnly_IsTheStockSums()
        {
            var tally = new OpenChuteTally();
            tally.NoteStockChute(ParachuteCanopyState.SemiDeployed, 2500.0, 11u, 4.0, 170.0);
            tally.NoteStockChute(ParachuteCanopyState.Deployed, double.NaN, 12u, 1.0, 500.0);
            tally.NoteStockChute(ParachuteCanopyState.None, double.NaN, 13u, 1.0, 500.0);
            Assert.Equal("stock", tally.CombineDrag(out double now, out double full));
            Assert.Equal(504.0, now, 9);
            Assert.Equal(670.0, full, 9);
            Assert.Equal(ParachuteCanopyState.Deployed, tally.Canopy);
            Assert.Equal(2500.0, tally.FullDeployAltitudeAgl);
            Assert.Equal(11u, tally.FirstChutePid);
            Assert.Equal(2, tally.OpenCount);

            var none = new OpenChuteTally();
            Assert.Equal("-", none.CombineDrag(out double n0, out double f0));
            Assert.Equal(0.0, n0);
            Assert.Equal(0.0, f0);
        }

        // Stock drag and RealChute area are different units: a vessel with both kinds open
        // reads no drag rather than a ratio of mixed units.
        [Fact]
        public void CombineDrag_RealChuteOnly_IsArea_Mixed_IsNaN()
        {
            var rc = new OpenChuteTally();
            rc.NoteRealChuteModule(PersistedModule(("PREDEPLOYED", "800", "2", "20")), 21u);
            Assert.Equal("realchute-area", rc.CombineDrag(out double now, out double full));
            Assert.Equal(Area(2), now, 9);
            Assert.Equal(Area(20), full, 9);

            rc.NoteStockChute(ParachuteCanopyState.Deployed, double.NaN, 22u, 1.0, 500.0);
            Assert.Equal("mixed", rc.CombineDrag(out double mNow, out double mFull));
            Assert.True(double.IsNaN(mNow));
            Assert.True(double.IsNaN(mFull));

            // A stowed RealChute part beside an open stock chute does not mix.
            var stowed = new OpenChuteTally();
            stowed.NoteRealChuteModule(PersistedModule(("STOWED", "800", "2", "20")), 23u);
            stowed.NoteStockChute(ParachuteCanopyState.Deployed, double.NaN, 24u, 1.0, 500.0);
            Assert.Equal("stock", stowed.CombineDrag(out _, out _));
        }

        // ------------------------------------------------------------- planner end to end

        // catches: a RealChute stage under a drogue-sized partly open canopy scaled by no drag
        // and given the 7 m/s default (it always landed); its area ratio now scales the rate
        // (100 m/s * sqrt((4/20)^2) = 20 m/s, over the 12 m/s limit: it crashes).
        [Fact]
        public void Planner_RealChutePartlyOpen_ScalesByCanopyArea()
        {
            var tally = new OpenChuteTally();
            Assert.True(RealChuteLiveRead.TryBuildModuleNode(
                Module(Canopy("PREDEPLOYED", 500f, 4f, 20f)), out ConfigNode node, out _, out _));
            tally.NoteRealChuteModule(node, 31u);
            string source = tally.CombineDrag(out double now, out double full);
            var input = new ParachuteDescentInput
            {
                BodyName = "Kerbin",
                StartUT = 1000.0,
                Latitude = -0.1,
                Longitude = -74.5,
                Altitude = 3070.0,
                VerticalSpeed = -100.0,
                Canopy = tally.Canopy,
                FullDeployAltitudeAgl = tally.FullDeployAltitudeAgl,
                TerrainAltitude = 70.0,
                BodyHasOcean = true,
                InAtmosphere = true,
                RootHeightAboveLowestPoint = double.NaN,
                OpenChuteDragNow = now,
                OpenChuteDragFull = full,
                OpenChuteDragSource = source,
                ChutePartPersistentId = tally.FirstChutePid,
                OpenChuteCount = tally.OpenCount,
            };
            Assert.True(ParachuteDescentPlanner.TryPlan(input, out ParachuteDescentPlan plan, out _));
            Assert.Equal("semi-estimated", plan.LandingRateSource);
            Assert.Equal(20.0, plan.LandingRate, 6);
            Assert.False(plan.SurvivesTouchdown);

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                "rec-realchute", 9u, input, out IncompleteBallisticFinalizationResult result));
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Contains(logLines, l => l.Contains("Chute descent predicted: rec=rec-realchute")
                && l.Contains("chuteDragSource=realchute-area"));
        }

        // ------------------------------------------------------------- logging

        [Fact]
        public void Log_SummarizesTheRealChuteRead()
        {
            var tally = new OpenChuteTally();
            tally.NoteRealChuteModule(PersistedModule(("DEPLOYED", "700", "1", "2")), 41u);
            tally.RealChuteUnreadCanopies = 1;
            ParachuteDescentLiveReader.LogRealChuteRead(tally, loaded: true, vesselPid: 4040u);
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("RealChute read: pid=4040 source=loaded-reflection modules=1 openModules=1 " +
                              "openCanopies=1 unreadCanopies=1 readFailures=0 areaNow=3.14 areaFull=3.14"));
            Assert.DoesNotContain(logLines, l => l.Contains("RealChute module not readable"));
        }

        [Fact]
        public void Log_WarnsOnAnUnreadableModule()
        {
            var tally = new OpenChuteTally();
            tally.NoteRealChuteReadFailure("canopy-list-field-missing");
            ParachuteDescentLiveReader.LogRealChuteRead(tally, loaded: true, vesselPid: 5050u);
            Assert.Contains(logLines, l => l.Contains("RealChute read: pid=5050")
                && l.Contains("readFailures=1 areaNow=- areaFull=-"));
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("RealChute module not readable: pid=5050 failures=1 reason=canopy-list-field-missing"));
        }

        // A stock install carries no RealChute module: no RealChute line at all.
        [Fact]
        public void Log_StockOnlyVessel_IsSilent()
        {
            var tally = new OpenChuteTally();
            tally.NoteStockChute(ParachuteCanopyState.Deployed, double.NaN, 1u, 1.0, 500.0);
            ParachuteDescentLiveReader.LogRealChuteRead(tally, loaded: true, vesselPid: 6060u);
            ParachuteDescentLiveReader.LogRealChuteRead(null, loaded: false, vesselPid: 6060u);
            Assert.DoesNotContain(logLines, l => l.Contains("RealChute"));
        }
    }
}
