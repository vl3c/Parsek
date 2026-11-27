using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo SAVE-ACTIVE-INDEX-AFTER-GHOST-STRIP. Stock computes <c>FlightState.activeVesselIdx</c>
    /// before <c>ParsekScenario.OnSave</c> strips the ghost map ProtoVessels, so the strip must move
    /// the index down by the ghosts that sat before the active vessel, or F9 focuses a vessel that
    /// many slots later (an EVA kerbal's flag instead of the kerbal). The far Switch-To half strips
    /// the live ghosts in <c>onVesselSwitchingToUnloaded</c>, before stock's live <c>IndexOf</c>.
    /// </summary>
    [Collection("Sequential")]
    public class SaveActiveVesselIndexTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SaveActiveVesselIndexTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ProtoVessel MakeProtoVessel(uint pid)
        {
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.persistentId = pid;
            return pv;
        }

        private static FlightState MakeFlightState(int activeIdx, params uint[] pids)
        {
            var fs = (FlightState)FormatterServices.GetUninitializedObject(typeof(FlightState));
            fs.protoVessels = new List<ProtoVessel>();
            for (int i = 0; i < pids.Length; i++)
                fs.protoVessels.Add(MakeProtoVessel(pids[i]));
            fs.activeVesselIdx = activeIdx;
            return fs;
        }

        private static void RegisterGhosts(params uint[] pids)
        {
            for (int i = 0; i < pids.Length; i++)
                GhostMapPresence.RegisterGhostMapVesselPidForTesting(pids[i]);
        }

        private static Vessel MakeVessel()
        {
            var v = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            // Unity's == reads an object with no native pointer as null.
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cachedPtr);
            cachedPtr.SetValue(v, new IntPtr(1));
            return v;
        }

        private static uint ActivePid(FlightState fs) => fs.protoVessels[fs.activeVesselIdx].persistentId;

        // ------------------------------------------------------------------ pure decision

        [Theory]
        // [A, G, K, F], active K: one ghost before it.
        [InlineData(new uint[] { 1, 900, 2, 3 }, new uint[] { 900 }, 2, 1, 1, (int)GhostMapPresence.ActiveVesselIdxOutcome.Shifted)]
        // [G1, G2, A], active A: two ghosts before it.
        [InlineData(new uint[] { 900, 901, 1 }, new uint[] { 900, 901 }, 2, 0, 2, (int)GhostMapPresence.ActiveVesselIdxOutcome.Shifted)]
        // [A, K, G], active K: the ghost comes after it.
        [InlineData(new uint[] { 1, 2, 900 }, new uint[] { 900 }, 1, 1, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.Unchanged)]
        // [G, A], the index names the ghost: stock's off-flight default.
        [InlineData(new uint[] { 900, 1 }, new uint[] { 900 }, 0, 0, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.ActiveWasGhost)]
        // [A, G, B], the index names the ghost mid-list.
        [InlineData(new uint[] { 1, 900, 2 }, new uint[] { 900 }, 1, 0, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.ActiveWasGhost)]
        // stock wrote -1 (active ProtoVessel not in the list).
        [InlineData(new uint[] { 900, 1 }, new uint[] { 900 }, -1, -1, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.OutOfRange)]
        // past the end (never written by stock, but must not throw or move).
        [InlineData(new uint[] { 900, 1 }, new uint[] { 900 }, 5, 5, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.OutOfRange)]
        // no ghosts at all.
        [InlineData(new uint[] { 1, 2, 3 }, new uint[0], 2, 2, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.Unchanged)]
        public void Resolve_MovesTheIndexByTheGhostsBeforeIt(
            uint[] pids, uint[] ghosts, int oldIdx, int expectedIdx, int expectedGhostsBefore,
            int expectedOutcome)
        {
            int newIdx = GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(
                pids, new HashSet<uint>(ghosts), oldIdx, out int ghostsBefore, out var outcome);

            Assert.Equal(expectedIdx, newIdx);
            Assert.Equal(expectedGhostsBefore, ghostsBefore);
            Assert.Equal((GhostMapPresence.ActiveVesselIdxOutcome)expectedOutcome, outcome);
        }

        [Fact]
        public void Resolve_NullInputs_AreOutOfRangeOrUnchanged()
        {
            Assert.Equal(3, GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(
                null, new HashSet<uint> { 1 }, 3, out _, out var o1));
            Assert.Equal(GhostMapPresence.ActiveVesselIdxOutcome.OutOfRange, o1);

            Assert.Equal(1, GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(
                new uint[] { 5, 6 }, null, 1, out _, out var o2));
            Assert.Equal(GhostMapPresence.ActiveVesselIdxOutcome.Unchanged, o2);
        }

        // ------------------------------------------------------------------ the strip itself

        // Fails if: the strip leaves activeVesselIdx as stock computed it (counting the ghosts), so
        // the EVA + flag case saves an index that names the flag instead of the kerbal.
        [Fact]
        public void Strip_EvaThenFlag_SavedIndexStillNamesTheKerbal()
        {
            // [ship, ghost, kerbal, flag], kerbal active (index 2 counted with the ghost).
            RegisterGhosts(900);
            FlightState fs = MakeFlightState(2, 10, 900, 20, 30);

            int stripped = GhostMapPresence.StripFromSave(fs);

            Assert.Equal(1, stripped);
            Assert.Equal(3, fs.protoVessels.Count);
            Assert.Equal(1, fs.activeVesselIdx);
            Assert.Equal(20u, ActivePid(fs));
            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("Save activeVesselIdx 2->1 ghostsBefore=1 activePid=20"));
        }

        // Fails if: the EVA-only case (active vessel last) is left past the end of the stripped list,
        // which only stock's clamp in FlightDriver.Start rescues (and the harness LoadGame refuses).
        [Fact]
        public void Strip_ActiveIsLast_IndexStaysInRange()
        {
            RegisterGhosts(900, 901);
            FlightState fs = MakeFlightState(3, 900, 10, 901, 20);

            GhostMapPresence.StripFromSave(fs);

            Assert.Equal(2, fs.protoVessels.Count);
            Assert.Equal(1, fs.activeVesselIdx);
            Assert.Equal(20u, ActivePid(fs));
        }

        [Fact]
        public void Strip_GhostAfterTheActiveVessel_IndexUnchangedAndNotLogged()
        {
            RegisterGhosts(900);
            FlightState fs = MakeFlightState(1, 10, 20, 900);

            GhostMapPresence.StripFromSave(fs);

            Assert.Equal(1, fs.activeVesselIdx);
            Assert.Equal(20u, ActivePid(fs));
            Assert.DoesNotContain(logLines, l => l.Contains("Save activeVesselIdx"));
        }

        [Fact]
        public void Strip_IndexNamedAGhost_FallsBackToZero()
        {
            RegisterGhosts(900);
            FlightState fs = MakeFlightState(1, 10, 900, 20);

            GhostMapPresence.StripFromSave(fs);

            Assert.Equal(0, fs.activeVesselIdx);
            // Outside FLIGHT (the test host) the fallback is a placeholder, logged Verbose.
            Assert.Contains(logLines, l => l.Contains("[VERBOSE]")
                && l.Contains("Save activeVesselIdx 1->0: the index named ghost map vessel pid=900"));
        }

        [Fact]
        public void ComputeForStrip_IndexNamedAGhostInFlight_Warns()
        {
            RegisterGhosts(900);
            FlightState fs = MakeFlightState(1, 10, 900, 20);

            int newIdx = GhostMapPresence.ComputeActiveVesselIdxForStrip(
                fs.protoVessels, fs.activeVesselIdx, flightScene: true,
                0u, Guid.Empty, Guid.Empty, out _);

            Assert.Equal(0, newIdx);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Save activeVesselIdx 1->0: the index named ghost map vessel pid=900"));
        }

        [Fact]
        public void Strip_StockMinusOne_LeftUnchanged()
        {
            RegisterGhosts(900);
            FlightState fs = MakeFlightState(-1, 900, 10);

            GhostMapPresence.StripFromSave(fs);

            Assert.Equal(-1, fs.activeVesselIdx);
            Assert.Single(fs.protoVessels);
        }

        [Fact]
        public void Strip_NoGhosts_NothingTouched()
        {
            FlightState fs = MakeFlightState(2, 10, 20, 30);

            Assert.Equal(0, GhostMapPresence.StripFromSave(fs));
            Assert.Equal(2, fs.activeVesselIdx);
            Assert.DoesNotContain(logLines, l => l.Contains("Save activeVesselIdx"));
        }

        // Fails if: the OnSave wrapper reaches the raw strip some other way that skips the index move.
        [Fact]
        public void FailClosedWrapper_AlsoMovesTheIndex()
        {
            RegisterGhosts(900, 901);
            FlightState fs = MakeFlightState(3, 10, 900, 901, 20, 30);

            int stripped = ParsekScenario.StripGhostMapVesselsFailClosed(
                fs, "OnSave", "save-stamp", phasesCompleted: true);

            Assert.Equal(2, stripped);
            Assert.Equal(1, fs.activeVesselIdx);
            Assert.Equal(20u, ActivePid(fs));
        }

        // ------------------------------------------------------------------ far Switch-To

        [Fact]
        public void FarSwitch_NullTarget_DoesNothing()
        {
            RegisterGhosts(900);
            GhostMapPresence.RemoveAllGhostVesselsBeforeFarSwitch(null);

            Assert.True(GhostMapPresence.IsGhostMapVessel(900));
            Assert.Contains(logLines, l => l.Contains("Far Switch-To pre-stock ghost strip: no target"));
        }

        [Fact]
        public void FarSwitch_NoGhosts_LogsAndReturns()
        {
            Vessel target = MakeVessel();
            target.persistentId = 10;

            GhostMapPresence.RemoveAllGhostVesselsBeforeFarSwitch(target);

            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("Far Switch-To pre-stock ghost strip: no ghost map vessels targetPid=10"));
        }

        [Fact]
        public void FarSwitch_GhostTarget_IsLeftAlone()
        {
            RegisterGhosts(900);
            Vessel target = MakeVessel();
            target.persistentId = 900;

            GhostMapPresence.RemoveAllGhostVesselsBeforeFarSwitch(target);

            Assert.True(GhostMapPresence.IsGhostMapVessel(900));
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Far Switch-To pre-stock ghost strip skipped: target pid=900"));
        }

        // Fails if: the far Switch-To stops stripping the live ghosts before stock's IndexOf (the
        // handler is unsubscribed, or no longer calls the strip).
        [Fact]
        public void ParsekFlight_SubscribesTheFarSwitchStrip()
        {
            string flight = ReadSource("Controllers/ParsekFlight.cs");
            Assert.Contains("GameEvents.onVesselSwitchingToUnloaded.Add(OnVesselSwitchingToUnloaded);", flight);
            Assert.Contains("GameEvents.onVesselSwitchingToUnloaded.Remove(OnVesselSwitchingToUnloaded);", flight);

            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                ReadSource("Controllers/ParsekFlight.SwitchContinuation.cs"));
            int sig = prepared.IndexOf("void OnVesselSwitchingToUnloaded(", StringComparison.Ordinal);
            Assert.True(sig >= 0, "OnVesselSwitchingToUnloaded not found");
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
            Assert.Contains("GhostMapPresence.RemoveAllGhostVesselsBeforeFarSwitch(to)", body);
        }

        private static string ReadSource(string relative)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return File.ReadAllText(Path.Combine(root, "Source", "Parsek", relative));
        }
    }
}
