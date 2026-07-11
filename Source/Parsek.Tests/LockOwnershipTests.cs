using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the lock-file grammar and ownership decision
    /// (<see cref="LockFile"/>). Guards the single-session contract: two instances
    /// must never both consume, a live foreign lock must not be stolen (pid liveness
    /// beats a fresh t), and a crashed instance's lock must not wedge a fresh run (a
    /// dead pid is reclaimed even if its t looked recent).
    /// </summary>
    public class LockOwnershipTests
    {
        private sealed class FakeProbe : IPidLivenessProbe
        {
            private readonly PidLiveness result;
            public FakeProbe(PidLiveness result) { this.result = result; }
            public PidLiveness Probe(int pid) => result;
        }

        // ----- Grammar round-trip -----

        [Fact]
        public void FormatParse_RoundTrips_WithSpacedRootPath()
        {
            string line = LockFile.FormatLockLine("7f3a", 48213, 17390510.101,
                @"C:\Games\Kerbal Space Program");
            Assert.True(LockFile.TryParseLockLine(line, out LockInfo info));
            Assert.Equal("7f3a", info.Session);
            Assert.Equal(48213, info.Pid);
            Assert.Equal(@"C:\Games\Kerbal Space Program", info.Root); // spaces preserved
        }

        [Fact]
        public void Format_UsesInvariantCulture_NoCommaInT()
        {
            string line = LockFile.FormatLockLine("s", 1, 17390510.101, "root");
            // Only the escaped/root portion may contain arbitrary text; the numeric t
            // field must not carry a locale comma.
            Assert.Contains("t=17390510.101", line);
        }

        [Theory]
        [InlineData("pid=1 t=2 root=x")]     // no session
        [InlineData("session=s t=2 root=x")] // no pid
        [InlineData("")]
        public void TryParseLockLine_Missing_Fails(string line)
        {
            Assert.False(LockFile.TryParseLockLine(line, out _));
        }

        // ----- Ownership decisions -----

        [Fact]
        public void NoExistingLock_Acquires()
        {
            var decision = LockFile.DecideLockOwnership(
                default, existingPresent: false, ownSession: "me", ownT: 100.0,
                probe: new FakeProbe(PidLiveness.Alive));
            Assert.Equal(LockOwnership.Acquire, decision);
        }

        [Fact]
        public void OwnSessionLock_Reclaims()
        {
            var existing = new LockInfo { Ok = true, Session = "me", Pid = 42, T = 50.0 };
            var decision = LockFile.DecideLockOwnership(
                existing, existingPresent: true, ownSession: "me", ownT: 100.0,
                probe: new FakeProbe(PidLiveness.Dead)); // probe irrelevant when it's ours
            Assert.Equal(LockOwnership.Reclaim, decision);
        }

        [Fact]
        public void ForeignLiveLock_StandsDown_EvenWithOlderT()
        {
            // pid liveness is primary: a live foreign lock is never stolen even though
            // its t (10) looks older than ours (100).
            var existing = new LockInfo { Ok = true, Session = "other", Pid = 999, T = 10.0 };
            var decision = LockFile.DecideLockOwnership(
                existing, existingPresent: true, ownSession: "me", ownT: 100.0,
                probe: new FakeProbe(PidLiveness.Alive));
            Assert.Equal(LockOwnership.StandDown, decision);
        }

        [Fact]
        public void ForeignDeadLock_ReclaimsWithWarn_EvenWithRecentT()
        {
            // A crashed owner must not wedge a fresh run: reclaim even though its t
            // (200) looks newer than ours (100).
            var existing = new LockInfo { Ok = true, Session = "other", Pid = 999, T = 200.0 };
            var decision = LockFile.DecideLockOwnership(
                existing, existingPresent: true, ownSession: "me", ownT: 100.0,
                probe: new FakeProbe(PidLiveness.Dead));
            Assert.Equal(LockOwnership.ReclaimWithWarn, decision);
        }

        [Fact]
        public void ForeignUnknownProbe_OlderOrEqualT_ReclaimsWithWarn()
        {
            // Cannot probe: last-resort t tie-break. Existing t (50) <= ours (100) ->
            // treat as stale, reclaim.
            var existing = new LockInfo { Ok = true, Session = "other", Pid = 999, T = 50.0 };
            var decision = LockFile.DecideLockOwnership(
                existing, existingPresent: true, ownSession: "me", ownT: 100.0,
                probe: new FakeProbe(PidLiveness.Unknown));
            Assert.Equal(LockOwnership.ReclaimWithWarn, decision);
        }

        [Fact]
        public void ForeignUnknownProbe_NewerT_StandsDown()
        {
            // Existing t (300) newer than ours (100) -> defer to it.
            var existing = new LockInfo { Ok = true, Session = "other", Pid = 999, T = 300.0 };
            var decision = LockFile.DecideLockOwnership(
                existing, existingPresent: true, ownSession: "me", ownT: 100.0,
                probe: new FakeProbe(PidLiveness.Unknown));
            Assert.Equal(LockOwnership.StandDown, decision);
        }

        [Fact]
        public void ParseThenDecide_EndToEnd_ForeignLive_StandsDown()
        {
            string line = LockFile.FormatLockLine("other", 12345, 5.0, @"C:\KSP");
            Assert.True(LockFile.TryParseLockLine(line, out LockInfo info));
            var decision = LockFile.DecideLockOwnership(
                info, existingPresent: true, ownSession: "mine", ownT: 500.0,
                probe: new FakeProbe(PidLiveness.Alive));
            Assert.Equal(LockOwnership.StandDown, decision);
        }
    }
}
