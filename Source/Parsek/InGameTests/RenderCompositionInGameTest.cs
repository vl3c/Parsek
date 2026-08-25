using System.Collections;
using System.Globalization;
using System.IO;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // M-A7 Phase 1 (docs/dev/design-autotest-render-composition.md, "Test plan") - the in-game
    // WELL-FORMEDNESS gate for the render composition manifest.
    //
    // SCOPE, and the boundary is the whole point: this cell asserts that an ARMED recorder driven
    // by the LIVE flight scene produces a manifest that EXISTS, PARSES, and carries every section
    // with a payload that agrees with the file. It asserts NOTHING about composition itself. The
    // RC-* rules (coverage, seams, holds, cuts, cycles, route clipping, ownership) are evaluated in
    // Python alone - encoding one of them here would put the same rule in two languages, which is
    // exactly the second-copy drift the design forbids for the constant catalog.
    //
    // WHAT IT ACTUALLY DRIVES: the recorder's real per-frame path. Arming the force flag and
    // letting the live FLIGHT scene run a handful of frames is enough for ParsekFlight's own
    // mission-loop drive to call NotePlan and for the render Director to observe whatever ghosts
    // the scene has; a scene with no looping mission and no ghosts legitimately produces EMPTY
    // observed sections, and this cell asserts section PRESENCE in that case rather than inventing
    // a fixture. Where records DO exist, their counts must match the export payload exactly.
    //
    // WHY IT SKIPS WHEN THE ENV VAR IS ARMED: the force flag is a session-wide switch on a
    // DontDestroyOnLoad addon, and the teardown here calls Reset(). On a run where
    // PARSEK_RENDER_MANIFEST=1 armed the recorder for the WHOLE session, that Reset would throw
    // away the accumulation the run exists to measure, and the export would clobber that run's
    // manifest with this cell's few frames. Skipping is the honest answer: the env-armed session
    // already exercises this exact path at its own scene-exit flush.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); FLIGHT only; career-independent.
    public class RenderCompositionInGameTest
    {
        // Enough frames for the flight scene's own per-frame drives (plan capture, Director
        // observation, unit-clock sampling) to run several times, short enough that the cell adds
        // no meaningful time to a batch.
        private const int ObservationFrames = 12;

        [InGameTest(Category = "RenderComposition", Scene = GameScenes.FLIGHT,
            Description = "Render composition manifest well-formedness: with the recorder force-armed and the "
                + "live flight scene driving its real per-frame capture path, ExportRenderManifest's export "
                + "writes a file that parses as a RENDER_MANIFEST node carrying CONSTANTS + PLAN + CHAIN + "
                + "OBSERVED, with record counts that match the export payload (no RC-* rule logic in C#)")]
        public IEnumerator RenderManifest_ExportsAWellFormedFile()
        {
            // ---- PRECONDITIONS (nothing mutated yet; a skip here is a clean no-op) ----
            if (FlightGlobals.fetch == null || FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("No active vessel in FLIGHT; the recorder's capture path needs a live scene");
            if (Planetarium.fetch == null)
                InGameAssert.Skip("Planetarium not ready; the manifest header needs a real UT");
            if (RenderCompositionRecorder.EnvArmed)
                InGameAssert.Skip("Recorder is env-armed (" + RenderCompositionRecorder.EnvVarName
                    + "=1) for this whole session; running here would Reset the session's own accumulation "
                    + "and clobber its manifest");

            string manifestPath = Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty, RenderCompositionRecorder.ManifestFileName);
            bool existedBefore = File.Exists(manifestPath);

            bool prevForce = RenderCompositionRecorder.ForceEnabledForTesting;
            try
            {
                RenderCompositionRecorder.Reset();
                RenderCompositionRecorder.ForceEnabledForTesting = true;

                // Let the LIVE scene drive the real hooks. No synthetic Note* calls: a manifest
                // assembled by hand would prove the writer works and the wiring does not.
                for (int i = 0; i < ObservationFrames; i++)
                    yield return null;

                bool exported = RenderCompositionRecorder.TryExportNow(
                    RenderCompositionRecorder.ReasonVerb, out string path, out int dwells,
                    out int transitions, out int planUnits, out int clockEvents, out string error);

                InGameAssert.IsTrue(exported,
                    "TryExportNow failed with error='" + (error ?? "(none)") + "'");
                InGameAssert.IsFalse(string.IsNullOrEmpty(path), "export reported no path");
                InGameAssert.IsTrue(File.Exists(path), "export reported success but no file at " + path);

                ConfigNode file = ConfigNode.Load(path);
                InGameAssert.IsNotNull(file, "ConfigNode.Load returned null for " + path);

                // ConfigNode.Save writes CONTENTS only, so the loaded node holds RENDER_MANIFEST
                // as a child - the wrapper level the writer adds on purpose.
                ConfigNode root = file.GetNode(RenderCompositionManifest.RootNodeName);
                InGameAssert.IsNotNull(root,
                    "no " + RenderCompositionManifest.RootNodeName + " node in the exported manifest");

                AssertHeader(root);
                AssertSections(root, dwells, transitions, planUnits, clockEvents);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "RenderComposition: manifest well-formed at {0} - planUnits={1} dwells={2} "
                    + "transitions={3} clockEvents={4}",
                    path, planUnits, dwells, transitions, clockEvents));
            }
            finally
            {
                RenderCompositionRecorder.ForceEnabledForTesting = prevForce;
                RenderCompositionRecorder.Reset();
                // Leave the KSP root as we found it: this cell's manifest is a test artifact, and a
                // later reader must never mistake it for a driven run's evidence.
                if (!existedBefore)
                {
                    try
                    {
                        if (File.Exists(manifestPath))
                            File.Delete(manifestPath);
                    }
                    catch (IOException ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            "RenderComposition: could not remove the test manifest at "
                            + manifestPath + ": " + ex.Message);
                    }
                }
            }
        }

        // The header is a fixed, closed shape: every key the Python parser reads must be present,
        // and the two that this cell KNOWS the value of are asserted rather than merely counted.
        private static void AssertHeader(ConfigNode root)
        {
            InGameAssert.AreEqual(
                RenderCompositionManifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                root.GetValue("schemaVersion"), "schemaVersion missing or wrong");
            InGameAssert.AreEqual(RenderCompositionRecorder.ReasonVerb, root.GetValue("exportReason"),
                "exportReason must be the token the export was asked for");
            InGameAssert.AreEqual("False", root.GetValue("envArmed"),
                "envArmed must be False - this cell skips on an env-armed session");
            InGameAssert.AreEqual("True", root.GetValue("forceArmed"),
                "forceArmed must be True - the cell armed the recorder through the force flag");
            InGameAssert.AreEqual(GameScenes.FLIGHT.ToString(), root.GetValue("scene"),
                "scene must be the live FLIGHT scene the cell ran in");
            InGameAssert.IsNotNull(root.GetValue("exportUT"), "exportUT missing");
            InGameAssert.IsNotNull(root.GetValue("saveName"), "saveName missing");
            InGameAssert.IsNotNull(root.GetValue("mapRenderTracingOn"), "mapRenderTracingOn missing");
        }

        private static void AssertSections(
            ConfigNode root, int dwells, int transitions, int planUnits, int clockEvents)
        {
            ConfigNode constants = root.GetNode("CONSTANTS");
            InGameAssert.IsNotNull(constants, "CONSTANTS section missing");
            // The catalog is compile-time constant, so it is the ONE section that is never empty
            // regardless of what the scene was doing.
            InGameAssert.IsGreaterThan(constants.values.Count, 0.0,
                "CONSTANTS section is empty - the Layer 2 catalog did not export");

            ConfigNode plan = root.GetNode("PLAN");
            ConfigNode chain = root.GetNode("CHAIN");
            ConfigNode observed = root.GetNode("OBSERVED");
            InGameAssert.IsNotNull(plan, "PLAN section missing");
            InGameAssert.IsNotNull(chain, "CHAIN section missing");
            InGameAssert.IsNotNull(observed, "OBSERVED section missing");

            // Presence, not counts: a scene with no looping mission and no ghosts legitimately
            // observes nothing. What must ALWAYS hold is that the file agrees with the payload the
            // verb reports - a mismatch means the writer and the counters disagree about what was
            // written, which is the well-formedness failure this cell exists to catch.
            InGameAssert.AreEqual(planUnits, plan.GetNodes("UNIT").Length,
                "PLAN UNIT node count disagrees with the reported planUnits");
            InGameAssert.AreEqual(dwells, observed.GetNodes("DWELL").Length,
                "OBSERVED DWELL node count disagrees with the reported dwells");
            InGameAssert.AreEqual(transitions, observed.GetNodes("TRANSITION").Length,
                "OBSERVED TRANSITION node count disagrees with the reported transitions");
            InGameAssert.AreEqual(clockEvents, observed.GetNodes("CLOCK_EVENT").Length,
                "OBSERVED CLOCK_EVENT node count disagrees with the reported clockEvents");

            AssertDwellRecordsSane(observed);
            AssertPlanRecordsSane(plan);
        }

        // Every dwell that DID get recorded must be internally sane: a real ghost id, at least one
        // observed frame, and a non-inverted span. Vacuous when the scene had no ghosts, which is
        // why the section-presence assertions above stand on their own.
        private static void AssertDwellRecordsSane(ConfigNode observed)
        {
            ConfigNode[] dwellNodes = observed.GetNodes("DWELL");
            for (int i = 0; i < dwellNodes.Length; i++)
            {
                ConfigNode d = dwellNodes[i];
                string where = "DWELL[" + i.ToString(CultureInfo.InvariantCulture) + "]";

                InGameAssert.IsFalse(string.IsNullOrEmpty(d.GetValue("pid")), where + " has no pid");
                InGameAssert.IsFalse(string.IsNullOrEmpty(d.GetValue("treatment")),
                    where + " has no treatment token");

                int frames = ParseInt(d.GetValue("frames"));
                InGameAssert.IsGreaterThan(frames, 0.0,
                    where + " recorded zero frames - a dwell only exists because a frame observed it");

                double openUT = ParseDouble(d.GetValue("openUT"));
                double closeUT = ParseDouble(d.GetValue("closeUT"));
                InGameAssert.IsTrue(closeUT >= openUT,
                    where + " closes before it opens (openUT=" + d.GetValue("openUT")
                    + " closeUT=" + d.GetValue("closeUT") + ")");
            }
        }

        private static void AssertPlanRecordsSane(ConfigNode plan)
        {
            ConfigNode[] unitNodes = plan.GetNodes("UNIT");
            for (int i = 0; i < unitNodes.Length; i++)
            {
                ConfigNode u = unitNodes[i];
                string where = "PLAN UNIT[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                InGameAssert.IsFalse(string.IsNullOrEmpty(u.GetValue("host")), where + " has no host");
                InGameAssert.IsFalse(string.IsNullOrEmpty(u.GetValue("planSeq")), where + " has no planSeq");
                InGameAssert.IsFalse(string.IsNullOrEmpty(u.GetValue("ownerIndex")),
                    where + " has no ownerIndex");
            }
        }

        private static int ParseInt(string raw)
        {
            int v;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        private static double ParseDouble(string raw)
        {
            double v;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                ? v : double.NaN;
        }
    }
}
