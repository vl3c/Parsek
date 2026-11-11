using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Map-camera partial: the thin applier for the TWO-PHASE <c>MapCamera</c> verb. The
    /// parse, the range checks, the clamp report and the settle decision are the pure sibling
    /// <see cref="TestCommandMapCamera"/>; this file makes the stock calls
    /// (<c>PlanetariumCamera.SetTarget(MapObject)</c>, <c>camPitch</c> / <c>camHdg</c>,
    /// <c>SetDistance</c>) and reads back what the camera holds once it has settled.
    ///
    /// <para>The target is set through the body's own <c>MapObject</c>, never through
    /// <c>SetTarget(string)</c> / <c>SetTarget(int)</c>: those resolve through the camera's
    /// target list and, for a name that is not in it, fall back to the ACTIVE VESSEL
    /// (decompiled KSP 1.12.5), which in the Tracking Station is null.</para>
    ///
    /// <para>A <c>hold=</c> request keeps the head for that many WALL-CLOCK seconds after the
    /// settle (the poll's own <c>now</c>), then reads the camera back and answers OK; the
    /// settle itself gets the budget minus the hold, so the two never outrun it.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private MapCameraRequest mapCameraRequest;
        private TestCommandScene mapCameraScene;
        private int mapCameraApplyFrame;
        private Vector3d mapCameraLastRel;
        private bool mapCameraHasLastRel;
        private int mapCameraStableFrames;
        private bool mapCameraHolding;
        private double mapCameraHoldStartedAt;

        private void MapCameraImpl(ParsedCommand cmd)
        {
            var bodyNames = new List<string>();
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies != null)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i] != null) bodyNames.Add(bodies[i].bodyName);

            string focusRaw = ArgOrNull(cmd, TestCommandMapCamera.FocusKey);
            if (!TestCommandMapCamera.TryParse(
                    focusRaw,
                    ArgOrNull(cmd, TestCommandMapCamera.PitchKey),
                    ArgOrNull(cmd, TestCommandMapCamera.HeadingKey),
                    ArgOrNull(cmd, TestCommandMapCamera.DistanceKey),
                    ArgOrNull(cmd, TestCommandMapCamera.HoldKey),
                    bodyNames, out MapCameraRequest request, out string reject))
            {
                ParsekLog.Warn(Tag, $"mapcamera rejected reason={reject} focus={focusRaw ?? string.Empty} "
                    + $"pitch={ArgOrNull(cmd, TestCommandMapCamera.PitchKey) ?? "(none)"} "
                    + $"heading={ArgOrNull(cmd, TestCommandMapCamera.HeadingKey) ?? "(none)"} "
                    + $"distance={ArgOrNull(cmd, TestCommandMapCamera.DistanceKey) ?? "(none)"} "
                    + $"hold={ArgOrNull(cmd, TestCommandMapCamera.HoldKey) ?? "(none)"}");
                SetExecResult("REJECTED", null, $"{reject} focus={focusRaw ?? string.Empty}");
                return;
            }

            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            string admission = TestCommandDumpMapGeometry.DecideAdmission(scene, MapView.MapIsEnabled);
            if (admission != null)
            {
                ParsekLog.Warn(Tag, $"mapcamera rejected reason={admission} scene={HighLogic.LoadedScene}");
                SetExecResult("REJECTED", null, $"{admission} scene={HighLogic.LoadedScene}");
                return;
            }

            PlanetariumCamera cam = PlanetariumCamera.fetch;
            if (cam == null)
            {
                ParsekLog.Error(Tag, "mapcamera: PlanetariumCamera.fetch is null in a map scene");
                SetExecResult("ERROR", null, TestCommandMapCamera.NoCameraReason);
                return;
            }

            CelestialBody body = FlightGlobals.GetBodyByName(request.Focus);
            MapObject target = body != null ? body.MapObject : null;
            if (target == null)
            {
                ParsekLog.Error(Tag, $"mapcamera: focus={request.Focus} has no MapObject");
                SetExecResult("ERROR", null,
                    $"{TestCommandMapCamera.FocusNotTargetableReason} focus={request.Focus}");
                return;
            }

            cam.SetTarget(target);
            if (cam.target != target)
            {
                ParsekLog.Error(Tag, $"mapcamera: stock did not take focus={request.Focus} "
                    + $"target={(cam.target != null ? cam.target.GetName() : "(null)")}");
                SetExecResult("ERROR", null,
                    $"{TestCommandMapCamera.FocusNotAppliedReason} focus={request.Focus}");
                return;
            }
            if (request.PitchDeg.HasValue)
                cam.camPitch = TestCommandMapCamera.DegToRad(request.PitchDeg.Value);
            if (request.HeadingDeg.HasValue)
                cam.camHdg = TestCommandMapCamera.DegToRad(request.HeadingDeg.Value);
            if (request.DistanceMetres.HasValue)
                cam.SetDistance(TestCommandMapCamera.MetresToScaled(
                    request.DistanceMetres.Value, ScaledSpace.ScaleFactor));

            mapCameraRequest = request;
            mapCameraScene = scene;
            mapCameraApplyFrame = Time.frameCount;
            mapCameraHasLastRel = false;
            mapCameraStableFrames = 0;
            mapCameraHolding = false;
            mapCameraHoldStartedAt = 0.0;
            CultureInfo ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag, $"mapcamera applied focus={request.Focus} "
                + $"pitch={(request.PitchDeg.HasValue ? request.PitchDeg.Value.ToString("R", ic) : "(keep)")} "
                + $"heading={(request.HeadingDeg.HasValue ? request.HeadingDeg.Value.ToString("R", ic) : "(keep)")} "
                + $"distance={(request.DistanceMetres.HasValue ? request.DistanceMetres.Value.ToString("R", ic) : "(keep)")} "
                + $"hold={request.HoldSeconds.ToString("R", ic)} "
                + $"frame={Time.frameCount}");
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteMapCamera(double now)
        {
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool sceneOk = scene == mapCameraScene
                && TestCommandDumpMapGeometry.DecideAdmission(scene, MapView.MapIsEnabled) == null;
            PlanetariumCamera cam = PlanetariumCamera.fetch;
            if (cam == null) sceneOk = false;

            double actual = double.NaN;
            double held = double.NaN;
            int frames = Time.frameCount - mapCameraApplyFrame;
            double holdSeconds = mapCameraRequest != null ? mapCameraRequest.HoldSeconds : 0.0;
            CultureInfo ic = CultureInfo.InvariantCulture;
            MapCameraSettleOutcome outcome;
            double budget = DeferralBudget.BudgetSeconds(TestCommandMapCamera.Verb);
            if (mapCameraHolding)
            {
                // The camera already settled; only the real-time hold is left. Its end
                // reads the camera back below, so the OK reports what the camera holds
                // when the screenshot that follows is taken.
                MapCameraHoldOutcome hold = TestCommandMapCamera.DecideHold(
                    sceneOk, now - mapCameraHoldStartedAt, holdSeconds);
                if (hold == MapCameraHoldOutcome.Holding)
                    return;
                outcome = hold == MapCameraHoldOutcome.SceneLost
                    ? MapCameraSettleOutcome.SceneLost
                    : MapCameraSettleOutcome.Settled;
            }
            else
            {
                if (sceneOk && cam.target != null)
                {
                    Vector3d rel = (Vector3d)(cam.transform.position - cam.target.transform.position);
                    actual = rel.magnitude;
                    held = cam.Distance;
                    if (mapCameraHasLastRel
                        && TestCommandMapCamera.IsStill((rel - mapCameraLastRel).magnitude, held))
                        mapCameraStableFrames++;
                    else
                        mapCameraStableFrames = 0;
                    mapCameraLastRel = rel;
                    mapCameraHasLastRel = true;
                }
                bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now,
                    TestCommandMapCamera.SettleBudgetSeconds(budget, holdSeconds));
                outcome = TestCommandMapCamera.DecideSettle(
                    sceneOk, frames, actual, held, mapCameraStableFrames, expired);
                if (outcome == MapCameraSettleOutcome.NotYet)
                    return;
                if (outcome == MapCameraSettleOutcome.Settled && holdSeconds > 0.0)
                {
                    mapCameraHolding = true;
                    mapCameraHoldStartedAt = now;
                    TestCommandMapCamera.LogHolding(
                        cam.target != null ? cam.target.GetName() : mapCameraRequest?.Focus,
                        holdSeconds, frames);
                    return;
                }
            }

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            MapCameraRequest request = mapCameraRequest;
            ClearTwoPhase();
            mapCameraHolding = false;

            if (outcome == MapCameraSettleOutcome.SceneLost)
            {
                ParsekLog.Error(Tag, $"mapcamera scene lost focus={request?.Focus} frames={Int(frames)} "
                    + $"hold={holdSeconds.ToString("R", ic)} "
                    + $"scene={HighLogic.LoadedScene} map={Bool(MapView.MapIsEnabled)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandMapCamera.SceneLostReason, dequeueHead: true);
                return;
            }
            if (outcome == MapCameraSettleOutcome.TimedOut)
            {
                ParsekLog.Error(Tag, $"mapcamera not settled focus={request?.Focus} frames={Int(frames)} "
                    + $"actualScaled={actual.ToString("R", ic)} heldScaled={held.ToString("R", ic)} "
                    + $"budget={TestCommandMapCamera.SettleBudgetSeconds(budget, holdSeconds).ToString("F0", ic)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandMapCamera.NotSettledReason, dequeueHead: true);
                return;
            }

            double pitchDeg = TestCommandMapCamera.RadToDeg(cam.camPitch);
            double headingDeg = TestCommandMapCamera.RadToDeg(cam.camHdg);
            double distanceMetres = (double)cam.Distance * ScaledSpace.ScaleFactor;
            bool pitchClamped = TestCommandMapCamera.AngleClamped(request?.PitchDeg, pitchDeg);
            bool distanceClamped = TestCommandMapCamera.DistanceClamped(request?.DistanceMetres, distanceMetres);
            string focus = cam.target != null ? cam.target.GetName() : request?.Focus;
            TestCommandMapCamera.LogSettled(
                focus, pitchDeg, headingDeg, distanceMetres, pitchClamped, distanceClamped,
                TestCommandMapCamera.RadToDeg(cam.minPitch), TestCommandMapCamera.RadToDeg(cam.maxPitch),
                frames, holdSeconds);
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandMapCamera.BuildPayload(
                    focus, pitchDeg, headingDeg, distanceMetres, pitchClamped, distanceClamped, frames,
                    holdSeconds),
                null, dequeueHead: true);
        }
    }
}
