using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostMapTargetingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostMapTargetingTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TargetVerification_Accepted_LogsSetAsTargetSuccess()
        {
            bool accepted = GhostMapPresence.LogGhostTargetVerificationForTesting(
                "Ghost: Kerbal X",
                1,
                "icon click",
                GhostMapPresence.GhostTargetVerificationStatus.Accepted,
                "target-vessel-matches-ghost",
                "target=ghost");

            Assert.True(accepted);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[GhostMap]")
                && l.Contains("Ghost 'Ghost: Kerbal X' set as target via icon click")
                && l.Contains("verified")
                && l.Contains("recIndex=1"));
        }

        [Theory]
        [InlineData((int)GhostMapPresence.GhostTargetVerificationStatus.MissingFlightGlobals, "FlightGlobals.fetch=null")]
        [InlineData((int)GhostMapPresence.GhostTargetVerificationStatus.NullTarget, "FlightGlobals.fetch.VesselTarget=null")]
        [InlineData((int)GhostMapPresence.GhostTargetVerificationStatus.CurrentMainBody, "target-driver-celestialBody-is-current-main-body body=Kerbin")]
        [InlineData((int)GhostMapPresence.GhostTargetVerificationStatus.ParentBody, "target-driver-celestialBody-is-parent-body targetBody=Sun currentBody=Kerbin")]
        [InlineData((int)GhostMapPresence.GhostTargetVerificationStatus.WrongVessel, "target-vessel-mismatch ghostPid=100 targetPid=200")]
        [InlineData((int)GhostMapPresence.GhostTargetVerificationStatus.WrongObject, "target-is-not-the-ghost-vessel")]
        public void TargetVerification_Rejected_DoesNotLogSetAsTargetSuccess(
            int statusValue,
            string reason)
        {
            var status = (GhostMapPresence.GhostTargetVerificationStatus)statusValue;
            bool accepted = GhostMapPresence.LogGhostTargetVerificationForTesting(
                "Ghost: Kerbal X",
                1,
                "icon click",
                status,
                reason,
                "final-target-state");

            Assert.False(accepted);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("set as target via icon click"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[GhostMap]")
                && l.Contains("target rejected via icon click")
                && l.Contains("status=" + status)
                && l.Contains(reason)
                && l.Contains("final-target-state"));
        }

        [Fact]
        public void TargetVerification_Unavailable_DoesNotLogSetAsTargetSuccess()
        {
            bool accepted = GhostMapPresence.LogGhostTargetVerificationForTesting(
                "Ghost: Kerbal X",
                1,
                "icon click",
                GhostMapPresence.GhostTargetVerificationStatus.VerificationUnavailable,
                "ParsekScenario.Instance=null; post-validation target state cannot be observed",
                "unverified-target-state");

            Assert.False(accepted);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("set as target via icon click"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[GhostMap]")
                && l.Contains("target verification unavailable via icon click")
                && l.Contains("status=VerificationUnavailable")
                && l.Contains("ParsekScenario.Instance=null")
                && l.Contains("unverified-target-state"));
        }
    }
}
