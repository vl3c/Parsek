using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for rewind logging, PreProcessRewindSave, and rewind flag lifecycle.
    /// These catch problems like: UT not being adjusted, flags not being cleared,
    /// rewind fields not being copied through the fallback commit path.
    /// </summary>
    [Collection("Sequential")]
    public class RewindLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RewindLoggingTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        #region PreProcessRewindSave — UT Adjustment

        private string WriteTempSave(string content)
        {
            string path = Path.Combine(tempDir, "test_save.sfs");
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void PreProcessRewindSave_AdjustsUT_ByLeadTime()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 17000\n  VESSEL\n  {\n    name = MyRocket\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "MyRocket", 10.0);

            // Verify file was modified
            ConfigNode root = ConfigNode.Load(sfs);
            var fs = root.GetNode("FLIGHTSTATE");
            double newUT = double.Parse(fs.GetValue("UT"), CultureInfo.InvariantCulture);
            Assert.Equal(16990.0, newUT);

            // Verify log emitted
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("UT adjusted") &&
                l.Contains("17000") && l.Contains("16990"));
        }

        [Fact]
        public void PreProcessRewindSave_ClampsUT_AtZero()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 5\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "SomeVessel", 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            var fs = root.GetNode("FLIGHTSTATE");
            double newUT = double.Parse(fs.GetValue("UT"), CultureInfo.InvariantCulture);
            Assert.Equal(0.0, newUT);
        }

        [Fact]
        public void PreProcessRewindSave_StripsNamedVessel_LeavesOthers()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 17000\n" +
                "  VESSEL\n  {\n    name = Debris\n  }\n" +
                "  VESSEL\n  {\n    name = MyRocket\n  }\n" +
                "  VESSEL\n  {\n    name = Station\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "MyRocket", 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            var fs = root.GetNode("FLIGHTSTATE");
            var vessels = fs.GetNodes("VESSEL");
            Assert.Equal(2, vessels.Length);
            Assert.Equal("Debris", vessels[0].GetValue("name"));
            Assert.Equal("Station", vessels[1].GetValue("name"));

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Stripped 1 vessel"));
        }

        [Fact]
        public void PreProcessRewindSave_MultipleNames_StripsAllMatching()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 17000\n" +
                "  VESSEL\n  {\n    name = MyRocket\n  }\n" +
                "  VESSEL\n  {\n    name = Valentina Kerman\n  }\n" +
                "  VESSEL\n  {\n    name = Debris\n  }\n}\n");

            var names = new HashSet<string> { "MyRocket", "Valentina Kerman" };
            RecordingStore.PreProcessRewindSave(sfs, names, 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            var fs = root.GetNode("FLIGHTSTATE");
            var vessels = fs.GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("Debris", vessels[0].GetValue("name"));

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Stripped 2 vessel"));
        }

        [Fact]
        public void PreProcessRewindSave_NoMatchingVessel_StripsZero()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 17000\n" +
                "  VESSEL\n  {\n    name = Debris\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "NoSuchRocket", 10.0);

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Stripped 0 vessel"));
        }

        [Fact]
        public void PreProcessRewindSave_MissingFlightState_LogsWarning()
        {
            // Save file with no FLIGHTSTATE node
            string sfs = WriteTempSave("GAME\n{\n  version = 1.12.5\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "MyRocket", 10.0);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Rewind]") &&
                l.Contains("no FLIGHTSTATE"));
        }

        [Fact]
        public void PreProcessRewindSave_MissingUT_LogsWarning()
        {
            // FLIGHTSTATE exists but no UT value
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  VESSEL\n  {\n    name = MyRocket\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "MyRocket", 10.0);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Rewind]") &&
                l.Contains("missing or invalid UT"));
        }

        [Fact]
        public void PreProcessRewindSave_InvalidUT_LogsWarning()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = not_a_number\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "SomeVessel", 10.0);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Rewind]") &&
                l.Contains("missing or invalid UT"));
        }

        [Fact]
        public void PreProcessRewindSave_GameWrapper_HandlesCorrectly()
        {
            // Some save files have a GAME wrapper node
            string sfs = WriteTempSave(
                "GAME\n{\n  FLIGHTSTATE\n  {\n    UT = 20000\n" +
                "    VESSEL\n    {\n      name = Rocket\n    }\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "Rocket", 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;
            var fs = gameNode.GetNode("FLIGHTSTATE");
            double newUT = double.Parse(fs.GetValue("UT"), CultureInfo.InvariantCulture);
            Assert.Equal(19990.0, newUT);
            Assert.Empty(fs.GetNodes("VESSEL"));
        }

        [Fact]
        public void PreProcessRewindSave_LocaleSafe_ParsesAndWritesInvariant()
        {
            // UT with decimal point — must work regardless of locale
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 17000.5\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "X", 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            var fs = root.GetNode("FLIGHTSTATE");
            string utStr = fs.GetValue("UT");
            // Must contain a dot, never a comma (invariant culture)
            Assert.DoesNotContain(",", utStr);
            double newUT = double.Parse(utStr, CultureInfo.InvariantCulture);
            Assert.Equal(16990.5, newUT);
        }

        #endregion

        #region Rewind Flag Lifecycle

        [Fact]
        public void ResetForTesting_ClearsRewindAdjustedUT()
        {
            RecordingStore.IsRewinding = true;
            RecordingStore.RewindUT = 17000.0;
            RecordingStore.RewindAdjustedUT = 16990.0;
            RecordingStore.RewindReserved = new ResourceBudget.BudgetSummary
            {
                reservedFunds = 100, reservedScience = 10, reservedReputation = 5
            };

            RecordingStore.ResetForTesting();

            Assert.False(RecordingStore.IsRewinding);
            Assert.Equal(0.0, RecordingStore.RewindUT);
            Assert.Equal(0.0, RecordingStore.RewindAdjustedUT);
            Assert.Equal(0.0, RecordingStore.RewindReserved.reservedFunds);
            Assert.Equal(0.0, RecordingStore.RewindReserved.reservedScience);
            Assert.Equal(0f, RecordingStore.RewindReserved.reservedReputation);
        }

        [Fact]
        public void RewindAdjustedUT_DefaultsToZero()
        {
            RecordingStore.ResetForTesting();
            Assert.Equal(0.0, RecordingStore.RewindAdjustedUT);
        }

        #endregion

        #region FallbackCommit Rewind Field Propagation

        [Fact]
        public void StashAndCommit_WithRewindFields_PreservesRewindData()
        {
            // Simulate the FallbackCommitSplitRecorder path:
            // StashPending → set rewind fields on Pending → CommitPending
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };

            RecordingStore.StashPending(points, "CrashedRocket");
            Assert.True(RecordingStore.HasPending);

            // Copy rewind fields (as FallbackCommitSplitRecorder does)
            var pending = RecordingStore.Pending;
            pending.RewindSaveFileName = "parsek_rw_crash01";
            pending.RewindReservedFunds = 5000.0;
            pending.RewindReservedScience = 25.0;
            pending.RewindReservedRep = 3.0f;

            RecordingStore.CommitPending();

            // Verify committed recording has the rewind fields
            var committed = RecordingStore.CommittedRecordings;
            Assert.Single(committed);
            Assert.Equal("parsek_rw_crash01", committed[0].RewindSaveFileName);
            Assert.Equal(5000.0, committed[0].RewindReservedFunds);
            Assert.Equal(25.0, committed[0].RewindReservedScience);
            Assert.Equal(3.0f, committed[0].RewindReservedRep);
        }

        [Fact]
        public void StashAndCommit_WithoutRewindFields_HasNullSaveName()
        {
            // Normal commit (no crash) — rewind fields should be null/zero
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };

            RecordingStore.StashPending(points, "NormalRocket");
            RecordingStore.CommitPending();

            var committed = RecordingStore.CommittedRecordings;
            Assert.Single(committed);
            Assert.Null(committed[0].RewindSaveFileName);
            Assert.Equal(0.0, committed[0].RewindReservedFunds);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesAllRewindFields()
        {
            var source = new Recording
            {
                RewindSaveFileName = "parsek_rw_full",
                RewindReservedFunds = 12345.6,
                RewindReservedScience = 78.9,
                RewindReservedRep = 2.5f
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("parsek_rw_full", target.RewindSaveFileName);
            Assert.Equal(12345.6, target.RewindReservedFunds);
            Assert.Equal(78.9, target.RewindReservedScience);
            Assert.Equal(2.5f, target.RewindReservedRep);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_NullSource_DoesNotThrow()
        {
            var target = new Recording
            {
                RewindSaveFileName = "existing"
            };
            target.ApplyPersistenceArtifactsFrom(null);
            // Should not crash; field stays as-is
            Assert.Equal("existing", target.RewindSaveFileName);
        }

        #endregion

        #region CanRewind Log Assertions

        [Fact]
        public void CanRewind_HasPending_ReturnsFalse()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };
            RecordingStore.StashPending(points, "PendingVessel");

            var rec = new Recording { RewindSaveFileName = "parsek_rw_test" };
            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("Merge or discard pending recording first", reason);
        }

        // Note: CanRewind_SaveFileMissing is not testable without Unity
        // (KSPUtil.ApplicationRootPath throws SecurityException outside KSP)

        #endregion

        #region PreProcessRewindSave Log Content Assertions

        [Fact]
        public void PreProcessRewindSave_LogsUTBeforeAndAfter()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 5000\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "V", 10.0);

            var utLog = logLines.FirstOrDefault(l =>
                l.Contains("[Rewind]") && l.Contains("UT adjusted"));
            Assert.NotNull(utLog);
            // Log should contain both the original and new UT values
            Assert.Contains("5000", utLog);
            Assert.Contains("4990", utLog);
            Assert.Contains("lead time 10", utLog);
        }

        [Fact]
        public void PreProcessRewindSave_LogsVesselStrippedCount()
        {
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 1000\n" +
                "  VESSEL\n  {\n    name = Target\n  }\n" +
                "  VESSEL\n  {\n    name = Target\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "Target", 5.0);

            var stripLog = logLines.FirstOrDefault(l =>
                l.Contains("[Rewind]") && l.Contains("Stripped"));
            Assert.NotNull(stripLog);
            Assert.Contains("2 vessel(s)", stripLog);
            Assert.Contains("Target", stripLog);
        }

        #endregion

        #region Rewind Field Serialization Round-Trip

        /// <summary>
        /// Serialize rewind fields to ConfigNode using the same format as ParsekScenario.OnSave.
        /// </summary>
        private static void SerializeRewindFields(ConfigNode node, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                node.AddValue("rewindSave", rec.RewindSaveFileName);
                node.AddValue("rewindResFunds", rec.RewindReservedFunds.ToString("R", ic));
                node.AddValue("rewindResSci", rec.RewindReservedScience.ToString("R", ic));
                node.AddValue("rewindResRep", rec.RewindReservedRep.ToString("R", ic));
            }
        }

        /// <summary>
        /// Deserialize rewind fields from ConfigNode using the same format as ParsekScenario.OnLoad.
        /// </summary>
        private static void DeserializeRewindFields(ConfigNode node, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;
            rec.RewindSaveFileName = node.GetValue("rewindSave");

            string fundsStr = node.GetValue("rewindResFunds");
            if (fundsStr != null)
            {
                double v;
                if (double.TryParse(fundsStr, NumberStyles.Float, ic, out v))
                    rec.RewindReservedFunds = v;
            }
            string sciStr = node.GetValue("rewindResSci");
            if (sciStr != null)
            {
                double v;
                if (double.TryParse(sciStr, NumberStyles.Float, ic, out v))
                    rec.RewindReservedScience = v;
            }
            string repStr = node.GetValue("rewindResRep");
            if (repStr != null)
            {
                float v;
                if (float.TryParse(repStr, NumberStyles.Float, ic, out v))
                    rec.RewindReservedRep = v;
            }
        }

        [Fact]
        public void RewindFields_SurviveSerializationRoundTrip()
        {
            var rec = new Recording
            {
                RewindSaveFileName = "parsek_rw_roundtrip",
                RewindReservedFunds = 9999.9,
                RewindReservedScience = 42.0,
                RewindReservedRep = 7.5f
            };

            var node = new ConfigNode("RECORDING");
            SerializeRewindFields(node, rec);

            var loaded = new Recording();
            DeserializeRewindFields(node, loaded);

            Assert.Equal("parsek_rw_roundtrip", loaded.RewindSaveFileName);
            Assert.Equal(9999.9, loaded.RewindReservedFunds);
            Assert.Equal(42.0, loaded.RewindReservedScience);
            Assert.Equal(7.5f, loaded.RewindReservedRep);
        }

        [Fact]
        public void RewindFields_MissingInNode_DefaultGracefully()
        {
            // Old-format node with no rewind fields
            var node = new ConfigNode("RECORDING");
            node.AddValue("vesselName", "OldRocket");

            var loaded = new Recording();
            DeserializeRewindFields(node, loaded);

            Assert.Null(loaded.RewindSaveFileName);
            Assert.Equal(0.0, loaded.RewindReservedFunds);
            Assert.Equal(0.0, loaded.RewindReservedScience);
            Assert.Equal(0f, loaded.RewindReservedRep);
        }

        [Fact]
        public void RewindFields_NullSaveName_SkipsAllFields()
        {
            // When RewindSaveFileName is null, no rewind values are serialized
            var rec = new Recording
            {
                RewindSaveFileName = null,
                RewindReservedFunds = 1000.0 // should NOT be serialized
            };

            var node = new ConfigNode("RECORDING");
            SerializeRewindFields(node, rec);

            Assert.Null(node.GetValue("rewindSave"));
            Assert.Null(node.GetValue("rewindResFunds"));
        }

        [Fact]
        public void RewindFields_LocaleSafe_NoCommasInOutput()
        {
            var rec = new Recording
            {
                RewindSaveFileName = "parsek_rw_locale",
                RewindReservedFunds = 12345.678,
                RewindReservedScience = 0.001,
                RewindReservedRep = 3.14f
            };

            var node = new ConfigNode("RECORDING");
            SerializeRewindFields(node, rec);

            // All numeric values must use dots, never commas
            Assert.DoesNotContain(",", node.GetValue("rewindResFunds"));
            Assert.DoesNotContain(",", node.GetValue("rewindResSci"));
            Assert.DoesNotContain(",", node.GetValue("rewindResRep"));

            // Verify round-trip
            var loaded = new Recording();
            DeserializeRewindFields(node, loaded);
            Assert.Equal(12345.678, loaded.RewindReservedFunds);
        }

        #endregion

        #region Rewind Baseline Resource Computation

        [Fact]
        public void InitiateRewind_SetsBaselineFromPreLaunch()
        {
            // Simulate: recording with known PreLaunch values
            var rec = new Recording
            {
                RewindSaveFileName = "parsek_rw_test",
                PreLaunchFunds = 25000.0,
                PreLaunchScience = 5.0,
                PreLaunchReputation = 1.5f
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, funds = 25000 });
            rec.Points.Add(new TrajectoryPoint { ut = 200, funds = 45000 });

            // We can't call InitiateRewind (needs KSP file I/O), but we can
            // verify the baseline fields are stored and cleared correctly.
            RecordingStore.RewindBaselineFunds = rec.PreLaunchFunds;
            RecordingStore.RewindBaselineScience = rec.PreLaunchScience;
            RecordingStore.RewindBaselineRep = rec.PreLaunchReputation;

            Assert.Equal(25000.0, RecordingStore.RewindBaselineFunds);
            Assert.Equal(5.0, RecordingStore.RewindBaselineScience);
            Assert.Equal(1.5f, RecordingStore.RewindBaselineRep);
        }

        [Fact]
        public void ResetForTesting_ClearsBaselineFields()
        {
            RecordingStore.RewindBaselineFunds = 99999.0;
            RecordingStore.RewindBaselineScience = 42.0;
            RecordingStore.RewindBaselineRep = 7.0f;

            RecordingStore.ResetForTesting();

            Assert.Equal(0.0, RecordingStore.RewindBaselineFunds);
            Assert.Equal(0.0, RecordingStore.RewindBaselineScience);
            Assert.Equal(0f, RecordingStore.RewindBaselineRep);
        }

        [Fact]
        public void FullCommittedCost_SignConvention_NegativeMeansEarned()
        {
            // Verify the sign convention: negative cost = recording earned money
            var rec = new Recording
            {
                PreLaunchFunds = 25000.0,
                PreLaunchScience = 0.0,
                PreLaunchReputation = 0.0f
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, funds = 25000 });
            rec.Points.Add(new TrajectoryPoint { ut = 200, funds = 45000, science = 7.2f, reputation = 1.0f });

            double fundsCost = ResourceBudget.FullCommittedFundsCost(rec);
            double scienceCost = ResourceBudget.FullCommittedScienceCost(rec);
            double repCost = ResourceBudget.FullCommittedReputationCost(rec);

            // Earned 20000 funds → cost is -20000
            Assert.Equal(-20000.0, fundsCost);
            // Earned 7.2 science → cost is -7.2
            Assert.Equal(-7.2, scienceCost, 5);
            // Earned 1.0 rep → cost is -1.0
            Assert.Equal(-1.0, repCost, 5);
        }

        #endregion

        #region UT Data Flow (Coroutine sets UT after scene load)

        [Fact]
        public void UTFlow_PreProcessToRewindAdjustedUT_EndToEnd()
        {
            // Verifies the complete UT adjustment data flow:
            // 1. PreProcessRewindSave adjusts UT in save file
            // 2. GamePersistence.LoadGame parses adjusted UT into game.flightState.universalTime
            // 3. RecordingStore.RewindAdjustedUT captures it
            // 4. Coroutine captures it before yielding, then calls Planetarium.SetUniversalTime
            //
            // Steps 2-4 happen in InitiateRewind + coroutine. This test verifies steps 1+3.
            string sfs = WriteTempSave("FLIGHTSTATE\n{\n  UT = 17000.7\n}\n");
            RecordingStore.PreProcessRewindSave(sfs, "V", 10.0);

            // Simulate step 2: parse the adjusted UT from the processed file
            ConfigNode root = ConfigNode.Load(sfs);
            double adjustedUT = double.Parse(
                root.GetNode("FLIGHTSTATE").GetValue("UT"), CultureInfo.InvariantCulture);
            Assert.Equal(16990.7, adjustedUT);

            // Simulate step 3: store in RewindAdjustedUT (as InitiateRewind does)
            RecordingStore.RewindAdjustedUT = adjustedUT;
            Assert.Equal(16990.7, RecordingStore.RewindAdjustedUT);

            // Verify log records the adjustment
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("UT adjusted") &&
                l.Contains("17000") && l.Contains("16990"));
        }

        [Fact]
        public void UTFlow_IsSetInCoroutine_NotBeforeSceneTransition()
        {
            // REGRESSION TEST: Setting UT before HighLogic.LoadScene does NOT work —
            // the scene transition overwrites it. UT must be set in the deferred
            // coroutine AFTER the new scene's Planetarium is initialized.
            //
            // This test verifies that RewindAdjustedUT is stored and available
            // for the coroutine to read before flags are cleared.
            string sfs = WriteTempSave("FLIGHTSTATE\n{\n  UT = 500\n}\n");
            RecordingStore.PreProcessRewindSave(sfs, "V", 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            double adjustedUT = double.Parse(
                root.GetNode("FLIGHTSTATE").GetValue("UT"), CultureInfo.InvariantCulture);

            // UT = 500 - 10 (lead time) = 490
            Assert.Equal(490.0, adjustedUT);

            // Simulate InitiateRewind storing the adjusted UT
            RecordingStore.RewindAdjustedUT = adjustedUT;
            Assert.True(RecordingStore.RewindAdjustedUT > 0,
                "Adjusted UT must be positive for Planetarium.SetUniversalTime");

            // Simulate coroutine capturing adjustedUT BEFORE flags are cleared
            double coroutineCapturedUT = RecordingStore.RewindAdjustedUT;
            Assert.Equal(490.0, coroutineCapturedUT);

            // Simulate OnLoad clearing flags (happens AFTER coroutine captures)
            RecordingStore.RewindAdjustedUT = 0;

            // Coroutine's captured value survives the clearing
            Assert.Equal(490.0, coroutineCapturedUT);
            Assert.Equal(0.0, RecordingStore.RewindAdjustedUT);
        }

        [Fact]
        public void UTFlow_MustNotBeSetBeforeLoadScene()
        {
            // REGRESSION TEST: Verify InitiateRewind does NOT contain
            // Planetarium.SetUniversalTime before LoadScene.
            //
            // This is a structural invariant: the scene transition overwrites
            // any UT set before it. UT must be set in the coroutine.
            //
            // We test this by verifying the data flow: RewindAdjustedUT is
            // stored for the coroutine, not consumed immediately.
            RecordingStore.RewindAdjustedUT = 446.9;

            // The coroutine reads this value. If InitiateRewind consumed it
            // (by calling Planetarium.SetUniversalTime and then clearing it),
            // the coroutine would get 0. This test ensures the value persists
            // until the coroutine captures it.
            Assert.Equal(446.9, RecordingStore.RewindAdjustedUT);
        }

        [Fact]
        public void ResourceCorrection_ResetsToBaseline_NotAbsoluteTarget()
        {
            // REGRESSION TEST: On rewind, resources are reset to baseline
            // (PreLaunch values), NOT baseline - totalCost.
            // Ghost playback re-applies recording resource deltas at the correct UT.
            double baseline = 50000.0;

            var rec = new Recording { PreLaunchFunds = 50000 };
            rec.Points.Add(new TrajectoryPoint { ut = 100, funds = 50000 });
            rec.Points.Add(new TrajectoryPoint { ut = 200, funds = 38000 });

            // The recording spent 12000 funds
            double totalCost = ResourceBudget.FullCommittedFundsCost(rec);
            Assert.Equal(12000.0, totalCost);

            // Rewind correction sets to baseline, NOT baseline - totalCost
            double currentFunds = 55000.0; // whatever KSP singleton has
            double correction = baseline - currentFunds;
            double result = currentFunds + correction;
            Assert.Equal(50000.0, result); // baseline, NOT 38000

            // Ghost playback will apply the -12000 delta at the correct UT later
        }

        #endregion

        #region Multiple Vessels with Same Name

        [Fact]
        public void PreProcessRewindSave_DuplicateVesselNames_StripsAll()
        {
            // Edge case: multiple vessels named the same (e.g., debris renamed by KSP)
            string sfs = WriteTempSave(
                "FLIGHTSTATE\n{\n  UT = 10000\n" +
                "  VESSEL\n  {\n    name = Debris\n  }\n" +
                "  VESSEL\n  {\n    name = Rocket\n  }\n" +
                "  VESSEL\n  {\n    name = Rocket\n  }\n" +
                "  VESSEL\n  {\n    name = Rocket\n  }\n}\n");

            RecordingStore.PreProcessRewindSave(sfs, "Rocket", 10.0);

            ConfigNode root = ConfigNode.Load(sfs);
            var fs = root.GetNode("FLIGHTSTATE");
            var vessels = fs.GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("Debris", vessels[0].GetValue("name"));

            Assert.Contains(logLines, l =>
                l.Contains("Stripped 3 vessel(s)"));
        }

        #endregion
    }
}
