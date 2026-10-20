using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    public class ReFlyInvokeHoldTests
    {
        private const string GuidA = "11111111111111111111111111111111";
        private const string GuidB = "22222222222222222222222222222222";
        private const double RewindUT = 1000.0;

        private static Recording Rec(string id, uint pid, string guid, double startUt, double endUt,
            TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = startUt,
                ExplicitEndUT = endUt,
            };
            rec.TerminalStateValue = terminal;
            return rec;
        }

        private static GameAction RecoveryFunds(string recId, double ut, float funds)
        {
            return new GameAction
            {
                ActionId = "act-" + recId + "-" + ut.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RecordingId = recId,
                UT = ut,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                FundsAwarded = funds,
            };
        }

        private static List<ReFlyInvokeHold.Entry> Classify(
            List<(uint, string)> survivors, List<Recording> recs, List<GameAction> ledger,
            out int matchedWithoutFuture, params uint[] excluded)
        {
            return ReFlyInvokeHold.Classify(
                survivors, new HashSet<uint>(excluded), recs, ledger, RewindUT, out matchedWithoutFuture);
        }

        [Fact]
        public void RecoveredAfterRewind_IsHeldUntilTheRecovery_AndItsRowIsLeftInTheLedger()
        {
            var flea = Rec("flea", 42u, GuidA, 100.0, 500.0, TerminalState.Landed);
            var row = RecoveryFunds("flea", 1500.0, 50000f);
            var ledger = new List<GameAction> { row };

            int matchedWithoutFuture;
            var held = Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { flea }, ledger, out matchedWithoutFuture);

            var entry = Assert.Single(held);
            Assert.Equal(42u, entry.Pid);
            Assert.Equal("flea", entry.RecordingId);
            Assert.Equal(1500.0, entry.FirstClaimUT);
            Assert.Equal(ReFlyInvokeHold.EvidenceRecovery, entry.Evidence);
            Assert.Empty(entry.FlyingRecordingIds);
            Assert.Same(row, Assert.Single(ledger));
            Assert.Equal(0, matchedWithoutFuture);
        }

        [Fact]
        public void NoCommittedFutureAfterRewind_StaysLive()
        {
            var flea = Rec("flea", 42u, GuidA, 100.0, 500.0, TerminalState.Landed);
            var ledger = new List<GameAction> { RecoveryFunds("flea", 800.0, 50000f) };

            int matchedWithoutFuture;
            var held = Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { flea }, ledger, out matchedWithoutFuture);

            Assert.Empty(held);
            Assert.Equal(1, matchedWithoutFuture);
        }

        [Fact]
        public void LaterCommittedFlight_IsHeldUntilItStarts_AndItsSpawnClaimIsNamed()
        {
            var first = Rec("first", 42u, GuidA, 100.0, 500.0, TerminalState.Landed);
            var later = Rec("later", 42u, GuidA, 1500.0, 2000.0, TerminalState.Orbiting);

            int matchedWithoutFuture;
            var held = Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { first, later }, new List<GameAction>(), out matchedWithoutFuture);

            var entry = Assert.Single(held);
            Assert.Equal(1500.0, entry.FirstClaimUT);
            Assert.Equal("later", entry.RecordingId);
            Assert.Equal(ReFlyInvokeHold.EvidenceFlies, entry.Evidence);
            Assert.Equal(new[] { "later" }, entry.FlyingRecordingIds.ToArray());
        }

        [Fact]
        public void RecordingAlreadyPlayingAtRewind_ClaimsAtTheRewindUT()
        {
            var spanning = Rec("span", 42u, GuidA, 500.0, 2000.0, TerminalState.Landed);

            int matchedWithoutFuture;
            var held = Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { spanning }, new List<GameAction>(), out matchedWithoutFuture);

            Assert.Equal(RewindUT, Assert.Single(held).FirstClaimUT);
        }

        [Fact]
        public void EarliestCommittedUseWins()
        {
            var landed = Rec("landed", 42u, GuidA, 100.0, 500.0, TerminalState.Landed);
            var later = Rec("later", 42u, GuidA, 1500.0, 2000.0, TerminalState.Orbiting);
            var ledger = new List<GameAction> { RecoveryFunds("landed", 1800.0, 50000f) };

            int matchedWithoutFuture;
            var entry = Assert.Single(Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { landed, later }, ledger, out matchedWithoutFuture));

            Assert.Equal(1500.0, entry.FirstClaimUT);
            Assert.Equal("later", entry.RecordingId);
        }

        [Fact]
        public void ReFlownAndStrippedVessels_AreNeverHeld()
        {
            var a = Rec("a", 42u, GuidA, 500.0, 2000.0);
            var b = Rec("b", 43u, GuidB, 500.0, 2000.0);

            int matchedWithoutFuture;
            var held = Classify(new List<(uint, string)> { (42u, GuidA), (43u, GuidB) },
                new List<Recording> { a, b }, new List<GameAction>(), out matchedWithoutFuture, 42u, 43u);

            Assert.Empty(held);
        }

        [Fact]
        public void OtherLaunchOfTheSameCraft_OrUnknownGuid_IsNotHeld()
        {
            var rec = Rec("a", 42u, GuidA, 500.0, 2000.0);

            int matchedWithoutFuture;
            Assert.Empty(Classify(new List<(uint, string)> { (42u, GuidB) },
                new List<Recording> { rec }, new List<GameAction>(), out matchedWithoutFuture));
            Assert.Empty(Classify(new List<(uint, string)> { (42u, null) },
                new List<Recording> { rec }, new List<GameAction>(), out matchedWithoutFuture));
            Assert.Empty(Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { Rec("u", 42u, null, 500.0, 2000.0) }, new List<GameAction>(),
                out matchedWithoutFuture));
        }

        [Fact]
        public void DestroyedTerminalWithRecoveryRow_IsNotARecovery_ButAFlightAfterRewindStillHolds()
        {
            var destroyedBefore = Rec("d", 42u, GuidA, 100.0, 500.0, TerminalState.Destroyed);
            var ledger = new List<GameAction> { RecoveryFunds("d", 1500.0, 1000f) };

            int matchedWithoutFuture;
            Assert.Empty(Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { destroyedBefore }, ledger, out matchedWithoutFuture));

            var destroyedAfter = Rec("d2", 42u, GuidA, 500.0, 1500.0, TerminalState.Destroyed);
            var entry = Assert.Single(Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { destroyedAfter }, new List<GameAction>(), out matchedWithoutFuture));
            Assert.Equal(ReFlyInvokeHold.EvidenceFlies, entry.Evidence);
        }

        [Fact]
        public void RecoveredTerminalEndingAfterRewind_ClaimsWhileItPlays()
        {
            var rec = Rec("r", 42u, GuidA, 500.0, 1500.0, TerminalState.Recovered);

            int matchedWithoutFuture;
            var entry = Assert.Single(Classify(new List<(uint, string)> { (42u, GuidA) },
                new List<Recording> { rec }, new List<GameAction>(), out matchedWithoutFuture));

            Assert.Equal(RewindUT, entry.FirstClaimUT);
            Assert.Equal(ReFlyInvokeHold.EvidenceRecovery, entry.Evidence);
            Assert.Empty(entry.FlyingRecordingIds);
        }

        [Fact]
        public void BuildInvokeHoldExcludedPids_IsTheReFlownVesselAndTheStrippedOnes()
        {
            var strip = new PostLoadStripResult
            {
                SelectedPid = 7u,
                StrippedPids = new List<uint> { 8u, 9u },
            };

            Assert.Equal(new HashSet<uint> { 7u, 8u, 9u }, RewindInvoker.BuildInvokeHoldExcludedPids(strip));
        }

        // The invoke no longer retires anything: no RewindInvoker code (closures included)
        // constructs a LedgerTombstone, and the hold feed is wired.
        [Fact]
        public void RewindInvoker_WritesNoTombstoneAtInvoke_AndFeedsTheHold()
        {
            Type invoker = typeof(RewindInvoker);
            ConstructorInfo tombCtor = typeof(LedgerTombstone).GetConstructor(Type.EmptyTypes);
            MethodInfo hold = invoker.GetMethod("HoldResurrectedVesselsWithCommittedFuture",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(hold);
            Assert.Null(invoker.GetMethod("RetireResurrectedVesselRecoveryRows",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

            bool anyTombstone = false;
            bool anyHoldCall = false;
            foreach (MethodBase m in AllMethods(invoker))
            {
                anyTombstone |= References(m, tombCtor, 0x73);
                anyHoldCall |= References(m, hold, 0x28);
            }
            Assert.False(anyTombstone);
            Assert.True(anyHoldCall);
        }

        private static IEnumerable<MethodBase> AllMethods(Type t)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(Any)) yield return m;
            foreach (var c in t.GetConstructors(Any)) yield return c;
            foreach (var nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in AllMethods(nested)) yield return m;
        }

        private static bool References(MethodBase method, MethodBase target, byte opcode)
        {
            if (target == null) return false;
            var body = method.GetMethodBody();
            if (body == null) return false;
            byte[] il = body.GetILAsByteArray();
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != opcode) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                MethodBase resolved;
                try
                {
                    resolved = method.Module.ResolveMethod(token,
                        method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException) { continue; }
                catch (BadImageFormatException) { continue; }
                if (resolved == target) return true;
            }
            return false;
        }
    }
}
