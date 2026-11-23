using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Part parity P8 F2 (design-part-ghost-parity.md, Appendix E.2 / E.4): the ghost-side seams
    /// the FX sweep reads, all extracted without a behaviour change (IL proofs in the PR).
    /// <list type="bullet">
    /// <item><c>GhostPlaybackEngine.ApplyUnsuppressedFxRestore</c>: the FX restore tail of an
    /// unsuppressed <c>ApplyFrameVisuals</c> frame, pinned by call order (source) and by its
    /// headless-observable effects (RCS flag, unmute, the RCS restore line).</item>
    /// <item><c>GhostFxFingerprint.BuildEntries</c>: the FX inventory of one ghost info, the
    /// lines <c>LogInfo</c> would write but never rate-limited.</item>
    /// <item><c>RcsGhostInfo.particleThrusters</c>: the read-only thruster record.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class PartParityFxGhostSeamTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PartParityFxGhostSeamTests()
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

        private static string GhostDir()
            => Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Ghost");

        private static string GhostSource(string file)
            => PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(GhostDir(), file)));

        private static string Body(string text, string signature, string what)
        {
            string body = PartParityStateRulesTests.MethodBody(text, signature);
            Assert.True(body != null, what + " moved; this cell is vacuous");
            return body;
        }

        /// <summary>Asserts every token appears in <paramref name="text"/>, each after the previous.</summary>
        private static void AssertInOrder(string text, string what, params string[] tokens)
        {
            int at = -1;
            foreach (string token in tokens)
            {
                int next = text.IndexOf(token, at + 1, StringComparison.Ordinal);
                Assert.True(next > at, what + ": '" + token + "' missing or out of order");
                at = next;
            }
        }

        /// <summary>A ParticleSystem Unity's managed null check reads as alive (no constructor ran,
        /// the cached native pointer is non-zero); only ever compared against null.</summary>
        private static ParticleSystem FakeLiveParticleSystem()
        {
            var ps = (ParticleSystem)FormatterServices.GetUninitializedObject(typeof(ParticleSystem));
            FieldInfo ptr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            ptr.SetValue(ps, new IntPtr(1));
            return ps;
        }

        // ----- ApplyUnsuppressedFxRestore: call order -----

        private static readonly string[] RestoreCallOrder =
        {
            "GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame(state);",
            "GhostPlaybackLogic.RestoreAllRcsEmissions(state);",
            "if (state.isBoundaryOverlapSecondary)",
            "GhostPlaybackLogic.MuteAllAudio(state);",
            "GhostPlaybackLogic.UnmuteAllAudio(state);",
            "if (ShouldRestartEngineFxAfterSuppression(false, priorVisualFxSuppressed))",
            "GhostPlaybackLogic.RestoreActiveEngineFx(state);",
            "GhostPlaybackLogic.UpdateEngineSmokeTrailFades(state);",
        };

        [Fact]
        public void ApplyUnsuppressedFxRestore_RunsTheTailInProductionOrder()
        {
            string text = GhostSource("GhostPlaybackEngine.InRangeRender.cs");
            string body = Body(text, "internal static void ApplyUnsuppressedFxRestore(",
                "GhostPlaybackEngine.ApplyUnsuppressedFxRestore");
            AssertInOrder(body, "ApplyUnsuppressedFxRestore", RestoreCallOrder);
            // Nothing else: the two instance calls before it and the audio update after it stay
            // with ApplyFrameVisuals.
            foreach (string absent in new[] { "UpdateReentryFx", "UpdateLaunchDust", "UpdateAudioAtmosphere",
                "TryPerformLazyReentryBuild" })
                Assert.DoesNotContain(absent, body);
            int calls = Regex.Matches(body, @"\b\w+\s*\(").Cast<Match>()
                .Count(m => !m.Value.StartsWith("if", StringComparison.Ordinal));
            Assert.Equal(7, calls);
        }

        [Fact]
        public void ApplyFrameVisuals_CallsTheTailAfterDust_AndAudioAfterBothBranches()
        {
            string text = GhostSource("GhostPlaybackEngine.InRangeRender.cs");
            string body = Body(text, "private void ApplyFrameVisuals(", "GhostPlaybackEngine.ApplyFrameVisuals");
            AssertInOrder(body, "ApplyFrameVisuals",
                "state.visualFxSuppressed = suppressVisualFx;",
                "if (suppressVisualFx)",
                "StopVisualFxForFrame(state, index);",
                "UpdateReentryFx(index, state, traj.VesselName, warpRate);",
                "UpdateLaunchDust(index, state, traj, warpRate);",
                "ApplyUnsuppressedFxRestore(state, priorVisualFxSuppressed);",
                "GhostPlaybackLogic.UpdateAudioAtmosphere(state);");
            // The moved calls live only in the tail now, so the two cannot drift apart.
            foreach (string moved in RestoreCallOrder.Where(t => t.StartsWith("GhostPlaybackLogic.", StringComparison.Ordinal)))
                Assert.DoesNotContain(moved, body);
            Assert.Single(Regex.Matches(body, @"ApplyUnsuppressedFxRestore\(").Cast<Match>());
        }

        // ----- ApplyUnsuppressedFxRestore: headless effects -----

        private static GhostPlaybackState IdleRcsState(bool boundarySecondary, bool audioMuted)
        {
            return new GhostPlaybackState
            {
                vesselName = "fx-seam",
                isBoundaryOverlapSecondary = boundarySecondary,
                audioMuted = audioMuted,
                rcsSuppressed = true,
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(7u, 0)] = new RcsGhostInfo { partPersistentId = 7u, currentPower = 0f },
                },
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(9u, 0)] = new EngineGhostInfo { partPersistentId = 9u, currentPower = 0f },
                },
            };
        }

        // The boundary-overlap secondary's MuteAllAudio body reads AudioSource.isPlaying (an
        // ECall the host cannot compile), so that arm is pinned by the order cell above only.
        [Fact]
        public void ApplyUnsuppressedFxRestore_RestoresRcsAndUnmutes()
        {
            GhostPlaybackState state = IdleRcsState(boundarySecondary: false, audioMuted: true);

            GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, priorVisualFxSuppressed: true);

            Assert.False(state.rcsSuppressed);
            Assert.False(state.audioMuted);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Restored RCS emissions for 0 of 1 modules from recorded power"));
            // An engine at power 0 is never restarted, even on the suppressed -> unsuppressed edge.
            Assert.DoesNotContain(logLines, l => l.Contains("RestoreActiveEngineFx"));
        }

        [Fact]
        public void ApplyUnsuppressedFxRestore_NoFxInfos_IsANoOp()
        {
            var state = new GhostPlaybackState { vesselName = "bare", audioMuted = false };

            GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, priorVisualFxSuppressed: true);

            Assert.False(state.audioMuted);
            Assert.Empty(logLines.Where(l => l.Contains("Restored RCS") || l.Contains("RestoreActiveEngineFx")));
        }

        // ----- BuildEntries -----

        private static GhostFxFingerprint.FxSystemReading Reading(string name, string parent, float y)
        {
            return new GhostFxFingerprint.FxSystemReading
            {
                objectName = name,
                parentName = parent,
                localPos = new Vector3(0f, y, 0f),
                emissionDir = new Vector3(0f, 1f, 0f),
                localScale = new Vector3(1f, 1f, 1f),
                startSizeMultiplier = 1.5f,
                startSpeedMultiplier = 2f,
            };
        }

        [Fact]
        public void BuildEntries_FormatsEveryLiveSystemInBuildOrder_AndCountsNulls()
        {
            ParticleSystem a = FakeLiveParticleSystem();
            ParticleSystem b = FakeLiveParticleSystem();
            var readings = new Dictionary<ParticleSystem, GhostFxFingerprint.FxSystemReading>
            {
                [a] = Reading("zz_flame(Clone)", "thrustTransform", 0.25f),
                [b] = Reading("aa_smoke", "thrustTransform", -1f),
            };
            var systems = new List<ParticleSystem> { a, null, b, null };

            List<string> entries = GhostFxFingerprint.BuildEntries(systems, ps => readings[ps], out int nulls);

            Assert.Equal(2, nulls);
            Assert.Equal(new[]
            {
                GhostFxFingerprint.FormatEntry("zz_flame(Clone)", "thrustTransform",
                    new Vector3(0f, 0.25f, 0f), Vector3.up, Vector3.one, 1.5f, 2f),
                GhostFxFingerprint.FormatEntry("aa_smoke", "thrustTransform",
                    new Vector3(0f, -1f, 0f), Vector3.up, Vector3.one, 1.5f, 2f),
            }, entries);
            // Build order, not sorted, and the canonical name strips the clone suffix.
            Assert.StartsWith("zz_flame<thrustTransform pos=(0.00,0.25,0.00)", entries[0]);
        }

        [Fact]
        public void BuildEntries_NullListOrInfo_IsEmpty()
        {
            Assert.Empty(GhostFxFingerprint.BuildEntries(null, ps => default(GhostFxFingerprint.FxSystemReading), out int nulls));
            Assert.Equal(0, nulls);
            Assert.Empty(GhostFxFingerprint.BuildEntries((EngineGhostInfo)null));
            Assert.Empty(GhostFxFingerprint.BuildEntries((RcsGhostInfo)null));
        }

        [Fact]
        public void BuildEntries_InfoOverloads_SkipDestroyedSystems()
        {
            var engine = new EngineGhostInfo();
            engine.particleSystems.Add(null);
            var rcs = new RcsGhostInfo();
            rcs.particleSystems.Add(null);
            rcs.particleSystems.Add(null);

            Assert.Empty(GhostFxFingerprint.BuildEntries(engine));
            Assert.Empty(GhostFxFingerprint.BuildEntries(rcs));
        }

        [Fact]
        public void LogInfo_ReadsItsLinesThroughBuildEntries()
        {
            // LogInfo's curve counts are Unity ECalls, so the wiring is pinned by source: the
            // rate-limited [FxFingerprint] line and BuildEntries share one loop.
            string text = GhostSource("GhostFxFingerprint.cs");
            string body = Body(text, "private static void LogInfo(", "GhostFxFingerprint.LogInfo");
            Assert.Contains("BuildEntries(particleSystems, ReadSystem, out int nullSystems)", body);
            Assert.DoesNotContain("FormatEntry(", body);
            Assert.DoesNotContain(".transform", body);
            foreach (string overload in new[] { "BuildEntries(EngineGhostInfo info)", "BuildEntries(RcsGhostInfo info)" })
                Assert.Contains("return BuildEntries(info?.particleSystems, ReadSystem, out _);",
                    Body(text, "internal static List<string> " + overload, overload));
        }

        // ----- RcsGhostInfo.particleThrusters -----

        [Fact]
        public void TryGetParticleThruster_ReadsTheRecordWhileTheListsLineUp()
        {
            var info = new RcsGhostInfo();
            info.particleSystems.Add(null);
            info.particleSystems.Add(null);
            info.particleThrusters.Add(new RcsParticleThruster { transformName = "RCSthruster", thrusterIndex = 3 });
            info.particleThrusters.Add(new RcsParticleThruster { transformName = "RCSthruster", thrusterIndex = 5 });

            Assert.True(info.TryGetParticleThruster(1, out RcsParticleThruster t));
            Assert.Equal("RCSthruster", t.transformName);
            Assert.Equal(5, t.thrusterIndex);
            Assert.False(info.TryGetParticleThruster(2, out _));
            Assert.False(info.TryGetParticleThruster(-1, out _));

            // Teardown clears the systems, not the record: the record is then unreadable.
            info.particleSystems.Clear();
            Assert.False(info.TryGetParticleThruster(0, out RcsParticleThruster none));
            Assert.Null(none.transformName);
        }

        [Fact]
        public void TheRcsBuilder_RecordsTheUnfilteredThrusterIndexBesideEverySystem()
        {
            string text = GhostSource("GhostVisualBuilder.RcsFx.cs");
            string body = Body(text, "private static List<RcsGhostInfo> TryBuildRcsFX(", "GhostVisualBuilder.TryBuildRcsFX");
            AssertInOrder(body, "TryBuildRcsFX",
                "var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);",
                "List<Transform> allFxTransforms = fxTransforms;",
                "allFxTransforms = new List<Transform>(fxTransforms);",
                "fxTransforms.RemoveAll(",
                "info.particleSystems.Add(ps);",
                "info.particleThrusters.Add(new RcsParticleThruster",
                "thrusterIndex = allFxTransforms.IndexOf(srcFxTransform),");
            Assert.Single(Regex.Matches(body, @"info\.particleSystems\.Add\(").Cast<Match>());
            Assert.Single(Regex.Matches(body, @"info\.particleThrusters\.Add\(").Cast<Match>());
            // Read-only: nothing in the ghost build or playback reads the record.
            foreach (string file in Directory.GetFiles(GhostDir(), "*.cs"))
            {
                if (Path.GetFileName(file) == "GhostTypes.cs") continue;
                string src = PartParityStateRulesTests.StripComments(File.ReadAllText(file));
                Assert.DoesNotMatch(@"particleThrusters\s*\[|TryGetParticleThruster\(", src);
            }
        }
    }
}
