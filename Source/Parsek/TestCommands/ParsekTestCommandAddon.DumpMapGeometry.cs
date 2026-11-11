using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Map-geometry partial: the thin applier for the TWO-PHASE <c>DumpMapGeometry</c> verb.
    /// Every decision is delegated to the pure sibling <see cref="TestCommandDumpMapGeometry"/>;
    /// this file arms the two read taps (marker positions, orbit arc clips), runs the end-of-frame capture coroutine
    /// (<see cref="MapGeometryRecorder.Capture"/>) and polls its outcome.
    ///
    /// <para>WHY A COROUTINE ON THIS ADDON. The capture must read after every map draw of one
    /// frame (stock orbit lines in LateUpdate, Parsek's polylines in the map camera's
    /// OnPreCull, Parsek's markers in OnGUI); <c>WaitForEndOfFrame</c> is the one point
    /// after all of them. The addon is <c>DontDestroyOnLoad</c>, so the coroutine survives a
    /// scene change, which the capture then reports as a failure rather than reading a
    /// half-built scene.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // Two-phase state for the in-flight dump, read only by TryCompleteDumpMapGeometry
        // while completionVerb == "DumpMapGeometry". The arm id fences a superseded
        // coroutine (an earlier arm that timed out) from settling a later one.
        private int mapGeometryArmId;
        private string mapGeometryLabel;
        private string mapGeometryPath;
        private string mapGeometrySceneToken;
        private bool mapGeometryWritten;
        private string mapGeometryFailure;
        private MapGeometryCounts mapGeometryCounts;
        private int mapGeometryFrame;

        private void DumpMapGeometryImpl(ParsedCommand cmd)
        {
            if (!TestCommandDumpMapGeometry.TryParseLabel(
                    ArgOrNull(cmd, "label"), out string label, out string labelReject))
            {
                ParsekLog.Warn(Tag, $"dumpmapgeometry rejected reason={labelReject} "
                    + $"label={ArgOrNull(cmd, "label") ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{labelReject} label={ArgOrNull(cmd, "label") ?? string.Empty}");
                return;
            }

            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            string admission = TestCommandDumpMapGeometry.DecideAdmission(scene, MapView.MapIsEnabled);
            if (admission != null)
            {
                ParsekLog.Warn(Tag, $"dumpmapgeometry rejected reason={admission} label={label} "
                    + $"scene={HighLogic.LoadedScene}");
                SetExecResult("REJECTED", null, $"{admission} scene={HighLogic.LoadedScene}");
                return;
            }

            mapGeometryArmId++;
            mapGeometryLabel = label;
            mapGeometrySceneToken = TestCommandDumpMapGeometry.SceneToken(scene);
            mapGeometryPath = MapGeometryRecorder.ResolveOutputPath(label);
            mapGeometryWritten = false;
            mapGeometryFailure = null;
            mapGeometryCounts = null;
            mapGeometryFrame = 0;

            SetMapGeometryTapsArmed(true);
            ParsekLog.Info(Tag, $"dumpmapgeometry armed label={label} scene={mapGeometrySceneToken} "
                + $"path={TestCommandDumpMapGeometry.RelativePathFor(label)} armFrame={Time.frameCount}");
            StartCoroutine(MapGeometryCaptureRoutine(mapGeometryArmId, scene));
            SetExecResult(PendingVerdict, null, null);
        }

        private IEnumerator MapGeometryCaptureRoutine(int armId, TestCommandScene armedScene)
        {
            int armFrame = Time.frameCount;
            // The arm frame's OnGUI may have drawn markers before the tap was on, so the
            // captured frame is a later, whole one.
            while (Time.frameCount <= armFrame)
                yield return null;
            yield return new WaitForEndOfFrame();

            if (armId != mapGeometryArmId)
                yield break;

            RunMapGeometryCapture(armedScene);
        }

        // The end-of-frame body of the capture coroutine: re-checks the scene, captures,
        // writes, and records the outcome the poll reads. Never throws.
        private void RunMapGeometryCapture(TestCommandScene armedScene)
        {
            try
            {
                TestCommandScene scene = MapScene(HighLogic.LoadedScene);
                string admission = scene == armedScene
                    ? TestCommandDumpMapGeometry.DecideAdmission(scene, MapView.MapIsEnabled)
                    : TestCommandDumpMapGeometry.SceneChangedFailure;
                if (admission != null)
                {
                    mapGeometryFailure = admission;
                    return;
                }
                MapGeometryDocument doc = MapGeometryRecorder.Capture(mapGeometryLabel, mapGeometrySceneToken);
                mapGeometryFrame = doc.Frame ?? Time.frameCount;
                mapGeometryCounts = MapGeometryRecorder.WriteDocument(mapGeometryPath, doc);
                mapGeometryWritten = true;
            }
            catch (Exception ex)
            {
                mapGeometryFailure = "threw-" + ex.GetType().Name;
                ParsekLog.Error(Tag, $"dumpmapgeometry capture threw label={mapGeometryLabel}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetMapGeometryTapsArmed(false);
            }
        }

        private void TryCompleteDumpMapGeometry(double now)
        {
            double budget = DeferralBudget.BudgetSeconds("DumpMapGeometry");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            MapGeometryPollOutcome outcome = TestCommandDumpMapGeometry.DecidePoll(
                mapGeometryWritten, mapGeometryFailure != null, expired);
            if (outcome == MapGeometryPollOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            string label = mapGeometryLabel;
            ClearTwoPhase();

            CultureInfo ic = CultureInfo.InvariantCulture;
            string elapsedText = (now - completionStartedAt).ToString("F1", ic);

            if (outcome == MapGeometryPollOutcome.Failed)
            {
                ParsekLog.Error(Tag, $"dumpmapgeometry failed label={label} "
                    + $"reason={mapGeometryFailure} elapsed={elapsedText}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandDumpMapGeometry.CaptureFailedReason} reason={mapGeometryFailure}",
                    dequeueHead: true);
                return;
            }

            if (outcome == MapGeometryPollOutcome.TimedOut)
            {
                // Fence the coroutine: a late capture must not settle a later arm.
                mapGeometryArmId++;
                SetMapGeometryTapsArmed(false);
                ParsekLog.Error(Tag, $"dumpmapgeometry timed out label={label} "
                    + $"elapsed={elapsedText}s budget={budget.ToString("F0", ic)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandDumpMapGeometry.TimeoutReason, dequeueHead: true);
                return;
            }

            long bytes = ReadMapGeometryBytes(mapGeometryPath);
            MapGeometryCounts counts = mapGeometryCounts ?? new MapGeometryCounts();
            TestCommandDumpMapGeometry.LogWritten(
                label, mapGeometrySceneToken, bytes, mapGeometryFrame, counts, elapsedText);
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandDumpMapGeometry.BuildPayload(
                    label, mapGeometrySceneToken, bytes, mapGeometryFrame, counts.Ghosts,
                    counts.Lines, counts.Points, counts.Truncated),
                null, dequeueHead: true);
        }

        // The two read taps the capture needs, armed and disarmed together: the marker
        // positions and the orbit arc-clip windows of the captured frame.
        private static void SetMapGeometryTapsArmed(bool armed)
        {
            MapMarkerRenderer.SetDrawnMarkerTapArmed(armed);
            Patches.GhostOrbitArcPatch.SetDrawnArcTapArmed(armed);
        }

        private static long ReadMapGeometryBytes(string path)
        {
            try
            {
                var info = new FileInfo(path ?? string.Empty);
                return info.Exists ? info.Length : -1L;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"dumpmapgeometry stat failed path={path}: {ex.GetType().Name}");
                return -1L;
            }
        }
    }
}
