using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The persisted-module chute read behind the unloaded-vessel chute descent: an EVA
    /// kerbal's ModuleEvaChute (stock's one ModuleParachute subclass) and RealChute's
    /// RealChuteModule are read as well as the stock ModuleParachute.
    /// </summary>
    public class ParachuteProtoModuleReadTests
    {
        private static ConfigNode StockModule(string persistentState, string deployAltitude)
        {
            var node = new ConfigNode("MODULE");
            if (persistentState != null) node.AddValue("persistentState", persistentState);
            if (deployAltitude != null) node.AddValue("deployAltitude", deployAltitude);
            return node;
        }

        private static ConfigNode RealChuteModule(params (string depState, string alt)[] canopies)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("armed", "False");
            foreach (var (depState, alt) in canopies)
            {
                ConfigNode c = node.AddNode("PARACHUTE");
                c.AddValue("depState", depState);
                if (alt != null) c.AddValue("deploymentAlt", alt);
            }
            return node;
        }

        // catches: an unloaded EVA kerbal under his personal chute matched by the exact name
        // "ModuleParachute" only, so he was predicted to crash once he unloaded.
        [Fact]
        public void EvaChute_IsReadLikeTheStockChute()
        {
            Assert.True(ParachuteProtoModuleRead.IsChuteModuleName("ModuleEvaChute"));
            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "ModuleEvaChute", StockModule("DEPLOYED", "450"), out string state, out double alt));
            Assert.Equal("DEPLOYED", state);
            Assert.Equal(450.0, alt);
        }

        [Fact]
        public void StockChute_SemiDeployed_KeepsItsFullDeployAltitude()
        {
            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "ModuleParachute", StockModule("SEMIDEPLOYED", "1000"), out string state, out double alt));
            Assert.Equal("SEMIDEPLOYED", state);
            Assert.Equal(1000.0, alt);
        }

        [Theory]
        [InlineData("ModuleCommand")]
        [InlineData("moduleparachute")]
        [InlineData(null)]
        public void OtherModules_AreNotChutes(string moduleName)
        {
            Assert.False(ParachuteProtoModuleRead.IsChuteModuleName(moduleName));
            Assert.False(ParachuteProtoModuleRead.TryReadCanopy(
                moduleName, StockModule("DEPLOYED", "500"), out string state, out _));
            Assert.Null(state);
        }

        [Fact]
        public void NoValues_IsNotRead()
        {
            Assert.False(ParachuteProtoModuleRead.TryReadCanopy(
                "ModuleParachute", null, out _, out double alt));
            Assert.True(double.IsNaN(alt));
        }

        // catches: a RealChute canopy that is open read as no chute (a RealChute craft left
        // under canopy predicted to crash), or its partly open states not counting.
        [Theory]
        [InlineData("DEPLOYED", "DEPLOYED")]
        [InlineData("PREDEPLOYED", "SEMIDEPLOYED")]
        [InlineData("LOWDEPLOYED", "SEMIDEPLOYED")]
        [InlineData("STOWED", "STOWED")]
        [InlineData("CUT", "CUT")]
        [InlineData("NONE", "NONE")]
        public void RealChute_MapsDepStateToTheStockState(string depState, string expected)
        {
            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "RealChuteModule", RealChuteModule((depState, "700")), out string state, out double alt));
            Assert.Equal(expected, state);
            Assert.Equal(700.0, alt);
        }

        // catches: a two-canopy RealChute part read from its first canopy only (a drogue cut
        // and the main open read as cut).
        [Fact]
        public void RealChute_TheMostOpenCanopyDecides()
        {
            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "RealChuteModule",
                RealChuteModule(("CUT", "2500"), ("PREDEPLOYED", "800"), ("STOWED", "300")),
                out string state, out double alt));
            Assert.Equal("SEMIDEPLOYED", state);
            Assert.Equal(800.0, alt);
        }

        [Fact]
        public void RealChute_NoCanopyNodes_ReadsNoState()
        {
            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "RealChuteModule", RealChuteModule(), out string state, out double alt));
            Assert.Null(state);
            Assert.True(double.IsNaN(alt));
        }

        [Fact]
        public void UnreadableAltitude_IsNaN()
        {
            Assert.True(ParachuteProtoModuleRead.TryReadCanopy(
                "ModuleParachute", StockModule("DEPLOYED", "high"), out _, out double alt));
            Assert.True(double.IsNaN(alt));
            Assert.Null(ParachuteProtoModuleRead.MapRealChuteState("SPINNING"));
        }

        // ------------------------------------------------ the unloaded-vessel loop's classifier

        // catches: the unloaded-vessel chute count (ParachuteDescentLiveReader) matching the
        // exact name "ModuleParachute", so an unloaded EVA kerbal under ModuleEvaChute and an
        // unloaded RealChute craft read as having no open chute and were predicted to crash.
        [Theory]
        [InlineData("ModuleParachute", "DEPLOYED", "Deployed")]
        [InlineData("ModuleParachute", "SEMIDEPLOYED", "SemiDeployed")]
        [InlineData("ModuleEvaChute", "DEPLOYED", "Deployed")]
        [InlineData("ModuleEvaChute", "SEMIDEPLOYED", "SemiDeployed")]
        public void LiveReader_UnloadedStockAndEvaChutes_AreOpen(
            string moduleName, string persistentState, string expected)
        {
            Assert.Equal(expected, ParachuteDescentLiveReader.ClassifyProtoModule(
                moduleName, StockModule(persistentState, "450"), out double alt).ToString());
            Assert.Equal(450.0, alt);
        }

        [Theory]
        [InlineData("DEPLOYED", "Deployed")]
        [InlineData("LOWDEPLOYED", "SemiDeployed")]
        [InlineData("PREDEPLOYED", "SemiDeployed")]
        public void LiveReader_UnloadedRealChute_IsOpen(string depState, string expected)
        {
            Assert.Equal(expected, ParachuteDescentLiveReader.ClassifyProtoModule(
                "RealChuteModule", RealChuteModule((depState, "700")), out double alt).ToString());
            Assert.Equal(700.0, alt);
        }

        // catches (owner ruling Q10b): a staged chute still armed (stock ACTIVE, a RealChute
        // canopy still STOWED while armed), stowed, cut or not a chute counted as open.
        [Theory]
        [InlineData("ModuleParachute", "ACTIVE")]
        [InlineData("ModuleParachute", "STOWED")]
        [InlineData("ModuleParachute", "CUT")]
        [InlineData("ModuleEvaChute", "ACTIVE")]
        [InlineData("ModuleEvaChute", "STOWED")]
        [InlineData("ModuleCommand", "DEPLOYED")]
        public void LiveReader_UnloadedChuteNotOpen_DoesNotCount(string moduleName, string persistentState)
        {
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyProtoModule(
                moduleName, StockModule(persistentState, "450"), out _));
        }

        [Theory]
        [InlineData("STOWED")]
        [InlineData("CUT")]
        [InlineData("NONE")]
        public void LiveReader_UnloadedRealChuteNotOpen_DoesNotCount(string depState)
        {
            ConfigNode module = RealChuteModule((depState, "700"));
            module.SetValue("armed", "True");
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyProtoModule(
                "RealChuteModule", module, out _));
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyProtoModule(
                "RealChuteModule", null, out double alt));
            Assert.True(double.IsNaN(alt));
        }
    }
}
