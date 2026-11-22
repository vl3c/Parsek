using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless half of the two ghost FX fixes: the RCS restore from recorded power (todo
    /// GHOST-RCS-DARK-AFTER-FX-SUPPRESSION) and the stock SmokeTrailControl air-density fade
    /// transcribed onto ghost engine smoke (todo GHOST-SMOKE-IN-VACUUM). The renderer and
    /// material writes are Unity ECalls the xUnit host cannot run; the decisions are pinned here.
    /// </summary>
    [Collection("Sequential")]
    public class GhostFxSmokeAndRcsRestoreTests : IDisposable
    {
        // Stock SmokeTrailControl field defaults (decompiled, KSP 1.12.5).
        private const float StockFadeStartDns = 0.06089431f;
        private const float StockFadeEndDns = 0.001362253f;

        private readonly List<string> logLines = new List<string>();

        public GhostFxSmokeAndRcsRestoreTests()
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

        #region Smoke trail fade (pure)

        [Theory]
        [InlineData(1.2, 1f)]          // sea level: full smoke
        [InlineData(0.06089431, 1f)]   // fade start
        [InlineData(0.001362253, 0f)]  // fade end
        [InlineData(0.0, 0f)]          // vacuum
        [InlineData(-1.0, 0f)]         // clamped below
        public void ComputeSmokeTrailAirDensity_MatchesStockInverseLerpEnds(double density, float expected)
        {
            float f = GhostPlaybackLogic.ComputeSmokeTrailAirDensity(StockFadeEndDns, StockFadeStartDns, density);
            Assert.Equal(expected, f, 1e-4f);
        }

        [Fact]
        public void ComputeSmokeTrailAirDensity_Midpoint_IsHalf()
        {
            double mid = (StockFadeEndDns + StockFadeStartDns) / 2.0;
            float f = GhostPlaybackLogic.ComputeSmokeTrailAirDensity(StockFadeEndDns, StockFadeStartDns, mid);
            Assert.Equal(0.5f, f, 1e-3f);
        }

        [Fact]
        public void ComputeSmokeTrailAirDensity_EqualEnds_IsZero_LikeMathfInverseLerp()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSmokeTrailAirDensity(0.01f, 0.01f, 1.0));
        }

        [Fact]
        public void ComputeSmokeTrailAirDensity_NaNDensity_IsZero()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSmokeTrailAirDensity(
                StockFadeEndDns, StockFadeStartDns, double.NaN));
        }

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(0.001f, 0)]      // stock: drawn only when > 0.001
        [InlineData(0.0011f, 1)]
        [InlineData(0.5f, 17)]
        [InlineData(1f, 32)]
        [InlineData(2f, 32)]
        [InlineData(float.NaN, 0)]
        public void QuantizeSmokeTrailAirDensity_HiddenAtStockThreshold(float airDensity, int expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.QuantizeSmokeTrailAirDensity(airDensity));
        }

        [Fact]
        public void ComputeSmokeTrailTint_PlainFade_IsWhiteWithScaledAlpha()
        {
            Color c = GhostPlaybackLogic.ComputeSmokeTrailTint(0.9f, false, new Color(0.2f, 0.3f, 0.4f, 0.5f), 0.5f);
            Assert.Equal(1f, c.r, 1e-4f);
            Assert.Equal(1f, c.g, 1e-4f);
            Assert.Equal(1f, c.b, 1e-4f);
            Assert.Equal(0.45f, c.a, 1e-4f);
        }

        [Fact]
        public void ComputeSmokeTrailTint_FadeToColor_LerpsEveryChannelToMaxAlpha()
        {
            var faded = new Color(0.2f, 0.4f, 0.6f, 0f);
            Color c = GhostPlaybackLogic.ComputeSmokeTrailTint(0.8f, true, faded, 0.5f);
            Assert.Equal(0.5f, c.r, 1e-4f);
            Assert.Equal(0.6f, c.g, 1e-4f);
            Assert.Equal(0.7f, c.b, 1e-4f);
            Assert.Equal(0.4f, c.a, 1e-4f);

            Color full = GhostPlaybackLogic.ComputeSmokeTrailTint(0.8f, true, faded, 1f);
            Assert.Equal(0.8f, full.r, 1e-4f);
            Assert.Equal(0.8f, full.a, 1e-4f);
        }

        [Theory]
        [InlineData(true, 70000.0, 0.0, true)]
        [InlineData(true, 70000.0, 69999.0, true)]
        [InlineData(true, 70000.0, 70000.0, false)]   // at the top: vacuum
        [InlineData(true, 70000.0, 100000.0, false)]
        [InlineData(false, 0.0, 10.0, false)]          // airless body
        [InlineData(true, 70000.0, double.NaN, false)]
        public void IsInsideAtmosphere_Cases(bool hasAtm, double depth, double alt, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.IsInsideAtmosphere(hasAtm, depth, alt));
        }

        [Theory]
        [InlineData("Kerbin", 1000.0, "Kerbin", 1005.0, true)]
        [InlineData("Kerbin", 1000.0, "Kerbin", 1010.0, false)]
        [InlineData("Kerbin", 1000.0, "Mun", 1000.0, false)]
        [InlineData(null, 1000.0, "Kerbin", 1000.0, false)]
        [InlineData("Kerbin", double.NaN, "Kerbin", 1000.0, false)]
        public void CanReuseSmokeTrailDensity_KeysOnBodyAndAltitude(
            string cachedBody, double cachedAlt, string body, double alt, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.CanReuseSmokeTrailDensity(cachedBody, cachedAlt, body, alt));
        }

        [Fact]
        public void HasLitEngineSmokeTrail_RequiresPowerAndACapturedFade()
        {
            var smoky = new EngineGhostInfo { partPersistentId = 1u, currentPower = 0f };
            smoky.smokeTrailFades.Add(new GhostSmokeTrailFade());
            var litNoSmoke = new EngineGhostInfo { partPersistentId = 2u, currentPower = 1f };
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(1u, 0)] = smoky,
                    [FlightRecorder.EncodeEngineKey(2u, 0)] = litNoSmoke
                }
            };

            Assert.False(GhostPlaybackLogic.HasLitEngineSmokeTrail(state));
            smoky.currentPower = 0.4f;
            Assert.True(GhostPlaybackLogic.HasLitEngineSmokeTrail(state));
            Assert.False(GhostPlaybackLogic.HasLitEngineSmokeTrail(null));
            Assert.False(GhostPlaybackLogic.HasLitEngineSmokeTrail(new GhostPlaybackState()));
        }

        [Fact]
        public void UpdateEngineSmokeTrailFades_NoLitSmokingEngine_ReadsNoDensityAndLogsNothing()
        {
            var info = new EngineGhostInfo { partPersistentId = 1u, currentPower = 0f };
            info.smokeTrailFades.Add(new GhostSmokeTrailFade { fadeStartDns = StockFadeStartDns, fadeEndDns = StockFadeEndDns });
            var state = new GhostPlaybackState
            {
                vesselName = "t",
                lastInterpolatedBodyName = "Kerbin",
                lastInterpolatedAltitude = 100000.0,
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { [FlightRecorder.EncodeEngineKey(1u, 0)] = info }
            };

            GhostPlaybackLogic.UpdateEngineSmokeTrailFades(state);

            Assert.Null(state.smokeDensityBodyName);
            Assert.Equal(-1, info.smokeTrailFades[0].appliedLevel);
            Assert.DoesNotContain(logLines, l => l.Contains("Smoke trail fade"));
        }

        [Fact]
        public void UpdateEngineSmokeTrailFades_CachedDensity_IsReusedWithoutABodyLookup()
        {
            // A fade with no renderer is skipped, so this drives the density path (cache hit,
            // no FlightGlobals) without reaching the Unity write.
            var info = new EngineGhostInfo { partPersistentId = 1u, currentPower = 1f };
            info.smokeTrailFades.Add(new GhostSmokeTrailFade { fadeStartDns = StockFadeStartDns, fadeEndDns = StockFadeEndDns });
            var state = new GhostPlaybackState
            {
                vesselName = "t",
                lastInterpolatedBodyName = "Kerbin",
                lastInterpolatedAltitude = 80003.0,
                smokeDensityBodyName = "Kerbin",
                smokeDensityAltitude = 80000.0,
                smokeDensity = 0.0,
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { [FlightRecorder.EncodeEngineKey(1u, 0)] = info }
            };

            GhostPlaybackLogic.UpdateEngineSmokeTrailFades(state);

            Assert.Equal(80000.0, state.smokeDensityAltitude);
            Assert.Equal(-1, info.smokeTrailFades[0].appliedLevel);
        }

        [Fact]
        public void UpdateEngineSmokeTrailFades_UnknownAltitude_LeavesSmokeAlone()
        {
            var info = new EngineGhostInfo { partPersistentId = 1u, currentPower = 1f };
            info.smokeTrailFades.Add(new GhostSmokeTrailFade());
            var state = new GhostPlaybackState
            {
                vesselName = "t",
                lastInterpolatedBodyName = "Kerbin",
                lastInterpolatedAltitude = double.NaN,
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { [FlightRecorder.EncodeEngineKey(1u, 0)] = info }
            };

            GhostPlaybackLogic.UpdateEngineSmokeTrailFades(state);

            Assert.Null(state.smokeDensityBodyName);
            Assert.Equal(-1, info.smokeTrailFades[0].appliedLevel);
        }

        #endregion

        #region RCS restore from recorded power

        [Fact]
        public void RestoreAllRcsEmissions_IdleModules_ClearsSuppressionAndRestoresNone()
        {
            var state = new GhostPlaybackState
            {
                rcsSuppressed = true,
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(7u, 0)] = new RcsGhostInfo { partPersistentId = 7u, currentPower = 0f },
                    [FlightRecorder.EncodeEngineKey(8u, 1)] = new RcsGhostInfo { partPersistentId = 8u, moduleIndex = 1, currentPower = 0f }
                }
            };

            GhostPlaybackLogic.RestoreAllRcsEmissions(state);

            Assert.False(state.rcsSuppressed);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Restored RCS emissions for 0 of 2 modules from recorded power"));
        }

        [Fact]
        public void RestoreAllRcsEmissions_NotSuppressed_IsNoOp()
        {
            var state = new GhostPlaybackState
            {
                rcsSuppressed = false,
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(7u, 0)] = new RcsGhostInfo { partPersistentId = 7u, currentPower = 0.5f }
                }
            };

            GhostPlaybackLogic.RestoreAllRcsEmissions(state);

            Assert.DoesNotContain(logLines, l => l.Contains("Restored RCS emissions"));
        }

        [Fact]
        public void RcsRestoreSet_IsTheFiringModules_NotTheRendererState()
        {
            // The restore set is exactly the modules with recorded power, so a module StopAllRcsFx
            // left with every renderer disabled is still re-lit when it was firing.
            var state = new GhostPlaybackState
            {
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(7u, 0)] = new RcsGhostInfo { partPersistentId = 7u, currentPower = 0.157f },
                    [FlightRecorder.EncodeEngineKey(8u, 0)] = new RcsGhostInfo { partPersistentId = 8u, currentPower = 0f }
                }
            };

            var restores = GhostPlaybackLogic.CollectDeferredRcsPowerRestores(state);

            Assert.Single(restores);
            Assert.Equal(FlightRecorder.EncodeEngineKey(7u, 0), restores[0].key);
            Assert.Equal(0.157f, restores[0].power);
        }

        [Fact]
        public void RestoreAllRcsEmissions_SourceRestoresFromRecordedPower()
        {
            // The firing branch calls SetRcsEmission (ParticleSystem ECalls), which the host cannot
            // JIT, so pin the wiring by source: the restore reads the recorded-power set, never a
            // renderer's enabled flag.
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "Ghost", "GhostPlaybackLogic.EngineRcsFx.cs");
            string src = System.IO.File.ReadAllText(path);
            int start = src.IndexOf("internal static void RestoreAllRcsEmissions(", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = src.IndexOf("internal static List<(ulong key, float power)> CollectDeferredEnginePowerRestores", start, StringComparison.Ordinal);
            string body = src.Substring(start, end - start);
            Assert.Contains("CollectDeferredRcsPowerRestores(state)", body);
            Assert.Contains("SetRcsEmission(state", body);
            Assert.DoesNotContain("renderer.enabled", body);
        }

        #endregion
    }
}
