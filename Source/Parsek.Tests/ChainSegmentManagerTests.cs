using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for ChainSegmentManager state clearing methods.
    /// Verifies that each Clear/Stop method zeroes exactly the right fields
    /// and leaves all other fields unchanged. Also verifies log output.
    /// </summary>
    [Collection("Sequential")]
    public class ChainSegmentManagerTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainSegmentManagerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Sets every field on ChainSegmentManager to a non-default value.
        /// Used to verify that clearing methods only touch the expected fields.
        /// </summary>
        private static void PopulateAllFields(ChainSegmentManager csm)
        {
            // Chain identity
            csm.ActiveChainId = "chain-abc123";
            csm.ActiveChainNextIndex = 5;
            csm.ActiveChainPrevId = "prev-xyz";
            csm.ActiveChainCrewName = "Jebediah Kerman";

            // Pending transition
            csm.PendingContinuation = true;
            csm.PendingIsBoarding = true;
            csm.PendingEvaName = "Valentina Kerman";
            csm.PendingBoundaryAnchor = new TrajectoryPoint
            {
                ut = 17000, latitude = -0.09, longitude = -74.55, altitude = 70
            };

            // Continuation
            csm.ContinuationVesselPid = 42;
            csm.ContinuationRecordingIdx = 3;
            csm.ContinuationLastVelocity = new Vector3(100f, 200f, 300f);
            csm.ContinuationLastUT = 17050.0;

            // Undock continuation
            csm.UndockContinuationPid = 99;
            csm.UndockContinuationRecIdx = 7;
            csm.UndockContinuationLastVel = new Vector3(10f, 20f, 30f);
            csm.UndockContinuationLastUT = 17100.0;
        }

        #region Constructor

        [Fact]
        public void Constructor_LogsCreation()
        {
            logLines.Clear();
            var csm = new ChainSegmentManager();

            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") && l.Contains("ChainSegmentManager created"));
        }

        [Fact]
        public void Constructor_InitializesFieldsToDefaults()
        {
            var csm = new ChainSegmentManager();

            Assert.Null(csm.ActiveChainId);
            Assert.Equal(0, csm.ActiveChainNextIndex);
            Assert.Null(csm.ActiveChainPrevId);
            Assert.Null(csm.ActiveChainCrewName);
            Assert.False(csm.PendingContinuation);
            Assert.False(csm.PendingIsBoarding);
            Assert.Null(csm.PendingEvaName);
            Assert.Null(csm.PendingBoundaryAnchor);
            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(-1, csm.ContinuationRecordingIdx);
            Assert.Equal(Vector3.zero, csm.ContinuationLastVelocity);
            Assert.Equal(-1.0, csm.ContinuationLastUT);
            Assert.Equal(0u, csm.UndockContinuationPid);
            Assert.Equal(-1, csm.UndockContinuationRecIdx);
            Assert.Equal(Vector3.zero, csm.UndockContinuationLastVel);
            Assert.Equal(-1.0, csm.UndockContinuationLastUT);
        }

        [Fact]
        public void HasActiveChain_FalseWhenIdNull()
        {
            var csm = new ChainSegmentManager();
            Assert.False(csm.HasActiveChain);
        }

        [Fact]
        public void HasActiveChain_TrueWhenIdSet()
        {
            var csm = new ChainSegmentManager();
            csm.ActiveChainId = "some-chain";
            Assert.True(csm.HasActiveChain);
        }

        #endregion

        #region ClearAll

        [Fact]
        public void ClearAll_ResetsAllSixteenFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            logLines.Clear();
            csm.ClearAll();

            // Chain identity
            Assert.Null(csm.ActiveChainId);
            Assert.Equal(0, csm.ActiveChainNextIndex);
            Assert.Null(csm.ActiveChainPrevId);
            Assert.Null(csm.ActiveChainCrewName);

            // Pending transition
            Assert.False(csm.PendingContinuation);
            Assert.False(csm.PendingIsBoarding);
            Assert.Null(csm.PendingEvaName);
            Assert.Null(csm.PendingBoundaryAnchor);

            // Continuation
            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(-1, csm.ContinuationRecordingIdx);
            Assert.Equal(Vector3.zero, csm.ContinuationLastVelocity);
            Assert.Equal(-1.0, csm.ContinuationLastUT);

            // Undock continuation
            Assert.Equal(0u, csm.UndockContinuationPid);
            Assert.Equal(-1, csm.UndockContinuationRecIdx);
            Assert.Equal(Vector3.zero, csm.UndockContinuationLastVel);
            Assert.Equal(-1.0, csm.UndockContinuationLastUT);
        }

        [Fact]
        public void ClearAll_HasActiveChainFalseAfterClear()
        {
            var csm = new ChainSegmentManager();
            csm.ActiveChainId = "chain-123";
            Assert.True(csm.HasActiveChain);

            csm.ClearAll();
            Assert.False(csm.HasActiveChain);
        }

        [Fact]
        public void ClearAll_Logs()
        {
            var csm = new ChainSegmentManager();
            logLines.Clear();
            csm.ClearAll();

            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") && l.Contains("ClearAll") &&
                l.Contains("all chain state reset"));
        }

        #endregion

        #region ClearChainIdentity

        [Fact]
        public void ClearChainIdentity_ClearsOnlyIdentityFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.ClearChainIdentity();

            // Cleared: chain identity
            Assert.Null(csm.ActiveChainId);
            Assert.Equal(0, csm.ActiveChainNextIndex);
            Assert.Null(csm.ActiveChainPrevId);
            Assert.Null(csm.ActiveChainCrewName);

            // Untouched: pending transition
            Assert.True(csm.PendingContinuation);
            Assert.True(csm.PendingIsBoarding);
            Assert.Equal("Valentina Kerman", csm.PendingEvaName);
            Assert.NotNull(csm.PendingBoundaryAnchor);

            // Untouched: continuation
            Assert.Equal(42u, csm.ContinuationVesselPid);
            Assert.Equal(3, csm.ContinuationRecordingIdx);
            Assert.Equal(new Vector3(100f, 200f, 300f), csm.ContinuationLastVelocity);
            Assert.Equal(17050.0, csm.ContinuationLastUT);

            // Untouched: undock continuation
            Assert.Equal(99u, csm.UndockContinuationPid);
            Assert.Equal(7, csm.UndockContinuationRecIdx);
            Assert.Equal(new Vector3(10f, 20f, 30f), csm.UndockContinuationLastVel);
            Assert.Equal(17100.0, csm.UndockContinuationLastUT);
        }

        [Fact]
        public void ClearChainIdentity_HasActiveChainFalseAfterClear()
        {
            var csm = new ChainSegmentManager();
            csm.ActiveChainId = "chain-xyz";
            Assert.True(csm.HasActiveChain);

            csm.ClearChainIdentity();
            Assert.False(csm.HasActiveChain);
        }

        #endregion

        #region StopContinuation

        [Fact]
        public void StopContinuation_ClearsFourContinuationFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            logLines.Clear();
            csm.StopContinuation("test reason");

            // Cleared: continuation fields
            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(-1, csm.ContinuationRecordingIdx);
            Assert.Equal(Vector3.zero, csm.ContinuationLastVelocity);
            Assert.Equal(-1.0, csm.ContinuationLastUT);
        }

        [Fact]
        public void StopContinuation_DoesNotTouchChainIdentity()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopContinuation("test");

            Assert.Equal("chain-abc123", csm.ActiveChainId);
            Assert.Equal(5, csm.ActiveChainNextIndex);
            Assert.Equal("prev-xyz", csm.ActiveChainPrevId);
            Assert.Equal("Jebediah Kerman", csm.ActiveChainCrewName);
        }

        [Fact]
        public void StopContinuation_DoesNotTouchPendingFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopContinuation("test");

            Assert.True(csm.PendingContinuation);
            Assert.True(csm.PendingIsBoarding);
            Assert.Equal("Valentina Kerman", csm.PendingEvaName);
            Assert.NotNull(csm.PendingBoundaryAnchor);
        }

        [Fact]
        public void StopContinuation_DoesNotTouchUndockContinuationFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopContinuation("test");

            Assert.Equal(99u, csm.UndockContinuationPid);
            Assert.Equal(7, csm.UndockContinuationRecIdx);
            Assert.Equal(new Vector3(10f, 20f, 30f), csm.UndockContinuationLastVel);
            Assert.Equal(17100.0, csm.UndockContinuationLastUT);
        }

        [Fact]
        public void StopContinuation_LogsReasonAndPreviousState()
        {
            var csm = new ChainSegmentManager();
            csm.ContinuationVesselPid = 42;
            csm.ContinuationRecordingIdx = 3;

            logLines.Clear();
            csm.StopContinuation("vessel null");

            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") &&
                l.Contains("Continuation stopped") &&
                l.Contains("vessel null") &&
                l.Contains("pid=42") &&
                l.Contains("recording #3"));
        }

        #endregion

        #region StopUndockContinuation

        [Fact]
        public void StopUndockContinuation_ClearsThreeFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            logLines.Clear();
            csm.StopUndockContinuation("test reason");

            // Cleared: undock PID, recording index, last UT
            Assert.Equal(0u, csm.UndockContinuationPid);
            Assert.Equal(-1, csm.UndockContinuationRecIdx);
            Assert.Equal(-1.0, csm.UndockContinuationLastUT);
        }

        [Fact]
        public void StopUndockContinuation_DoesNotClearUndockLastVel()
        {
            // StopUndockContinuation explicitly does NOT zero UndockContinuationLastVel.
            // This test documents that behavior.
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopUndockContinuation("test");

            Assert.Equal(new Vector3(10f, 20f, 30f), csm.UndockContinuationLastVel);
        }

        [Fact]
        public void StopUndockContinuation_DoesNotTouchChainIdentity()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopUndockContinuation("test");

            Assert.Equal("chain-abc123", csm.ActiveChainId);
            Assert.Equal(5, csm.ActiveChainNextIndex);
            Assert.Equal("prev-xyz", csm.ActiveChainPrevId);
            Assert.Equal("Jebediah Kerman", csm.ActiveChainCrewName);
        }

        [Fact]
        public void StopUndockContinuation_DoesNotTouchPendingFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopUndockContinuation("test");

            Assert.True(csm.PendingContinuation);
            Assert.True(csm.PendingIsBoarding);
            Assert.Equal("Valentina Kerman", csm.PendingEvaName);
            Assert.NotNull(csm.PendingBoundaryAnchor);
        }

        [Fact]
        public void StopUndockContinuation_DoesNotTouchContinuationFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopUndockContinuation("test");

            Assert.Equal(42u, csm.ContinuationVesselPid);
            Assert.Equal(3, csm.ContinuationRecordingIdx);
            Assert.Equal(new Vector3(100f, 200f, 300f), csm.ContinuationLastVelocity);
            Assert.Equal(17050.0, csm.ContinuationLastUT);
        }

        [Fact]
        public void StopUndockContinuation_LogsReasonAndPreviousState()
        {
            var csm = new ChainSegmentManager();
            csm.UndockContinuationPid = 99;
            csm.UndockContinuationRecIdx = 7;

            logLines.Clear();
            csm.StopUndockContinuation("replaced by new undock");

            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") &&
                l.Contains("Undock continuation stopped") &&
                l.Contains("replaced by new undock") &&
                l.Contains("pid=99") &&
                l.Contains("recording #7"));
        }

        #endregion

        #region Cross-method isolation (sequential calls)

        [Fact]
        public void ClearChainIdentity_ThenStopContinuation_OnlyClearsRespectiveFields()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.ClearChainIdentity();

            // Identity cleared
            Assert.Null(csm.ActiveChainId);
            Assert.Equal(0, csm.ActiveChainNextIndex);
            // Continuations still intact
            Assert.Equal(42u, csm.ContinuationVesselPid);
            Assert.Equal(99u, csm.UndockContinuationPid);

            csm.StopContinuation("phase 2");

            // Now continuation is cleared too
            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(-1, csm.ContinuationRecordingIdx);
            // But undock is still intact
            Assert.Equal(99u, csm.UndockContinuationPid);
            Assert.Equal(7, csm.UndockContinuationRecIdx);
        }

        [Fact]
        public void StopContinuation_ThenStopUndockContinuation_ClearsBothIndependently()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.StopContinuation("first");

            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(99u, csm.UndockContinuationPid);

            csm.StopUndockContinuation("second");

            Assert.Equal(0u, csm.UndockContinuationPid);
            Assert.Equal(-1, csm.UndockContinuationRecIdx);

            // Chain identity untouched throughout
            Assert.Equal("chain-abc123", csm.ActiveChainId);
            Assert.Equal(5, csm.ActiveChainNextIndex);
        }

        [Fact]
        public void ClearAll_AfterPartialClears_StillResetsEverything()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            // Partial clears first
            csm.ClearChainIdentity();
            csm.StopContinuation("partial");

            // Re-populate to prove ClearAll handles already-cleared fields
            PopulateAllFields(csm);
            csm.ClearAll();

            Assert.Null(csm.ActiveChainId);
            Assert.Equal(0, csm.ActiveChainNextIndex);
            Assert.Null(csm.ActiveChainPrevId);
            Assert.Null(csm.ActiveChainCrewName);
            Assert.False(csm.PendingContinuation);
            Assert.False(csm.PendingIsBoarding);
            Assert.Null(csm.PendingEvaName);
            Assert.Null(csm.PendingBoundaryAnchor);
            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(-1, csm.ContinuationRecordingIdx);
            Assert.Equal(Vector3.zero, csm.ContinuationLastVelocity);
            Assert.Equal(-1.0, csm.ContinuationLastUT);
            Assert.Equal(0u, csm.UndockContinuationPid);
            Assert.Equal(-1, csm.UndockContinuationRecIdx);
            Assert.Equal(Vector3.zero, csm.UndockContinuationLastVel);
            Assert.Equal(-1.0, csm.UndockContinuationLastUT);
        }

        #endregion

        #region ClearAll vs StopContinuation default values

        [Fact]
        public void ClearAll_SetsRecordingIdxToNegativeOne_NotZero()
        {
            // ContinuationRecordingIdx default is -1, not 0.
            // ClearAll must restore this sentinel, not zero.
            var csm = new ChainSegmentManager();
            csm.ContinuationRecordingIdx = 5;
            csm.UndockContinuationRecIdx = 8;

            csm.ClearAll();

            Assert.Equal(-1, csm.ContinuationRecordingIdx);
            Assert.Equal(-1, csm.UndockContinuationRecIdx);
        }

        [Fact]
        public void ClearAll_SetsLastUT_ToNegativeOne_NotZero()
        {
            // UT sentinel is -1, not 0.
            var csm = new ChainSegmentManager();
            csm.ContinuationLastUT = 17000.0;
            csm.UndockContinuationLastUT = 17100.0;

            csm.ClearAll();

            Assert.Equal(-1.0, csm.ContinuationLastUT);
            Assert.Equal(-1.0, csm.UndockContinuationLastUT);
        }

        #endregion

        #region Idempotency

        [Fact]
        public void ClearAll_CalledTwice_IsIdempotent()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.ClearAll();
            csm.ClearAll();

            Assert.Null(csm.ActiveChainId);
            Assert.Equal(0u, csm.ContinuationVesselPid);
            Assert.Equal(-1, csm.ContinuationRecordingIdx);
        }

        [Fact]
        public void StopContinuation_CalledOnAlreadyStoppedState_IsIdempotent()
        {
            var csm = new ChainSegmentManager();
            // Default state has pid=0, already "stopped"

            logLines.Clear();
            csm.StopContinuation("redundant");

            // Should log even when already stopped (contains "pid=0")
            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") && l.Contains("Continuation stopped") &&
                l.Contains("pid=0"));
        }

        [Fact]
        public void StopUndockContinuation_CalledOnAlreadyStoppedState_IsIdempotent()
        {
            var csm = new ChainSegmentManager();

            logLines.Clear();
            csm.StopUndockContinuation("redundant");

            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") && l.Contains("Undock continuation stopped") &&
                l.Contains("pid=0"));
        }

        #endregion

        #region Boundary anchor through ClearAll

        [Fact]
        public void ClearAll_NullsPendingBoundaryAnchor()
        {
            var csm = new ChainSegmentManager();
            csm.PendingBoundaryAnchor = new TrajectoryPoint
            {
                ut = 17000, latitude = -0.09, longitude = -74.55, altitude = 70
            };

            csm.ClearAll();

            Assert.Null(csm.PendingBoundaryAnchor);
        }

        [Fact]
        public void ClearChainIdentity_DoesNotClearPendingBoundaryAnchor()
        {
            var csm = new ChainSegmentManager();
            PopulateAllFields(csm);

            csm.ClearChainIdentity();

            Assert.NotNull(csm.PendingBoundaryAnchor);
            Assert.Equal(17000.0, csm.PendingBoundaryAnchor.Value.ut);
        }

        #endregion
    }
}
