using Parsek.MapRender;

namespace Parsek.TestCommands
{
    /// <summary>
    /// M-A7 partial: the thin Unity applier for the single-phase
    /// <c>ExportRenderManifest</c> verb (design-autotest-render-composition.md).
    ///
    /// <para>
    /// WHY THIS VERB EXISTS. The render-composition recorder auto-flushes only on a
    /// lifecycle boundary (scene switch, process teardown). A lane that wants the manifest
    /// for a MEASURED window - after a driven warp, before a scene it does not intend to
    /// leave - would otherwise have to provoke a scene bounce to get one, which changes the
    /// very composition being measured. This verb takes the export at the instant the spec
    /// asks for it, through the SAME <c>TryExportNow</c> the auto-flush calls.
    /// </para>
    ///
    /// <para>
    /// NO ARGS. The reason token is fixed (<c>verb</c>) because the manifest header's
    /// <c>exportReason</c> is a closed vocabulary the Python parser reads; letting a spec
    /// write it would put an open string into a gated field.
    /// </para>
    ///
    /// <para>
    /// TERMINALS. REJECTED <c>manifest-not-armed</c> when the recorder is not armed (the env
    /// var is read once at Awake, so this can never become true by waiting); ERROR with the
    /// recorder's own reason when the build / write failed; otherwise OK with the written
    /// path and the four headline record counts.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void ExportRenderManifestImpl(ParsedCommand cmd)
        {
            if (!RenderCompositionRecorder.IsEnabled)
            {
                ParsekLog.Warn(Tag, "exportrendermanifest rejected reason="
                    + TestCommandExportRenderManifest.NotArmedReason);
                SetExecResult("REJECTED", null, TestCommandExportRenderManifest.NotArmedReason);
                return;
            }

            bool ok = RenderCompositionRecorder.TryExportNow(
                RenderCompositionRecorder.ReasonVerb, out string path, out int dwells,
                out int transitions, out int planUnits, out int clockEvents, out string error);

            if (!ok)
            {
                string reason = TestCommandExportRenderManifest.ErrorReason(error);
                ParsekLog.Warn(Tag, "exportrendermanifest error reason=" + reason);
                SetExecResult("ERROR", null, reason);
                return;
            }

            ParsekLog.Info(Tag, "exportrendermanifest ok path=" + (path ?? string.Empty)
                + " planUnits=" + Int(planUnits) + " dwells=" + Int(dwells)
                + " transitions=" + Int(transitions) + " clockEvents=" + Int(clockEvents));

            SetExecResult("OK", TestCommandExportRenderManifest.BuildResultPayload(
                path, dwells, transitions, planUnits, clockEvents), null);
        }
    }
}
