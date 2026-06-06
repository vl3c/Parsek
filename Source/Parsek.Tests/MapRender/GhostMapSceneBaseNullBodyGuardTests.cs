using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Display;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression guard for the suppressed map-render shadow crash: a null/empty body name fed into
    /// the scene adapter's body lookups (<see cref="GhostMapSceneBase.IsStarBody"/>, the
    /// <c>BodySurface</c> provider, <see cref="GhostMapSceneBase.ResolveBody"/>) used to reach
    /// <c>FlightGlobals.GetBodyByName(null)</c>, which resolves through a
    /// <c>Dictionary&lt;string,CelestialBody&gt;</c> and calls <c>ContainsKey(null)</c> -> throws
    /// <see cref="ArgumentNullException"/> with <c>paramName == "key"</c>. The decision-only shadow
    /// (<c>ShadowRenderDriver.RunFrame</c>) feeds a null body name on every hidden-intent frame
    /// (<c>GhostRenderIntent.Hidden</c> carries <c>FrameBodyName == null</c>) and for any OrbitSegment
    /// lacking a body, so the unguarded lookup threw (caught + suppressed) and dropped that frame's
    /// director-drive seed production to the legacy path.
    ///
    /// These tests exercise ONLY the null/empty inputs, which short-circuit in the new guard BEFORE
    /// any <c>FlightGlobals</c> ECall, so they run headless. What makes them fail: the guard is removed
    /// and the lookup throws (or no longer emits the rate-limited skip diagnostic).
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapSceneBaseNullBodyGuardTests : IDisposable
    {
        public void Dispose() => ParsekLog.ResetTestOverrides();

        // A plain (non-Unity) GhostMapSceneBase; MapViewScene's only Unity touch is IsActive, which the
        // body-lookup guard never calls. Construction does no Unity work.
        private static GhostMapSceneBase NewScene() => new MapViewScene();

        [Fact]
        public void IsStarBody_NullName_ReturnsFalseAndDoesNotThrow()
        {
            GhostMapSceneBase scene = NewScene();

            Exception ex = Record.Exception(() => Assert.False(scene.IsStarBody(null)));

            Assert.Null(ex); // never ArgumentNullException("key") from the Dictionary lookup
        }

        [Fact]
        public void IsStarBody_EmptyName_ReturnsFalseAndDoesNotThrow()
        {
            GhostMapSceneBase scene = NewScene();

            Exception ex = Record.Exception(() => Assert.False(scene.IsStarBody("")));

            Assert.Null(ex);
        }

        [Fact]
        public void ResolveBody_NullName_ReturnsNullAndDoesNotThrow()
        {
            GhostMapSceneBase scene = NewScene();

            CelestialBody body = null;
            Exception ex = Record.Exception(() => body = scene.ResolveBody(null));

            Assert.Null(ex);
            Assert.Null(body);
        }

        [Fact]
        public void BodySurfaceProvider_NullName_ReturnsFalseAndDoesNotThrow()
        {
            GhostMapSceneBase scene = NewScene();
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface = scene.BodySurface;

            bool resolved = true;
            Exception ex = Record.Exception(
                () => resolved = surface(null, out GhostTrajectoryPolylineRenderer.BodySurfaceInfo _));

            Assert.Null(ex);
            Assert.False(resolved);
        }

        [Fact]
        public void NullBodyName_EmitsRateLimitedSkipDiagnostic()
        {
            // ParsekLog's test hooks are [ThreadStatic]; set them on THIS (the emit) thread so the
            // assertion is independent of the ctor thread under full-suite parallelism. Reset first for
            // a clean baseline (SuppressLogging=false, fresh rate-limit dict), then force verbose on
            // (the diagnostic goes through verbose-gated VerboseRateLimited).
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            NewScene().IsStarBody(null);

            Assert.Contains(logLines, l =>
                l.Contains("[MapRender]") && l.Contains("null/empty body name"));
        }
    }
}
