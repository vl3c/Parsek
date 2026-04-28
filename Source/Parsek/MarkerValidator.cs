using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Result of <see cref="MarkerValidator.Validate"/> on the
    /// <see cref="ReFlySessionMarker"/> read back from a save.
    /// Populates <see cref="Reason"/> with the first field that failed
    /// validation so callers can log a single-field explanation
    /// (per design §10.7 "Marker invalid field=&lt;f&gt;; cleared").
    /// </summary>
    internal struct MarkerValidationResult
    {
        public bool Valid;
        public string Reason;  // populated on invalid
        public string Details;

        public static MarkerValidationResult Ok(string details = null)
        {
            return new MarkerValidationResult { Valid = true, Reason = null, Details = details };
        }

        public static MarkerValidationResult Invalid(string reason, string details = null)
        {
            return new MarkerValidationResult { Valid = false, Reason = reason, Details = details };
        }
    }

    /// <summary>
    /// Phase 13 of Rewind-to-Staging (design §6.9 step 3): validates the
    /// durable fields of a <see cref="ReFlySessionMarker"/> against the
    /// scenario state loaded just before the sweep runs.
    ///
    /// <para>
    /// Validation checks (all must pass):
    /// <list type="bullet">
    ///   <item><description><see cref="ReFlySessionMarker.SessionId"/> non-empty.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.TreeId"/> present in
    ///   <see cref="RecordingStore.CommittedTrees"/>.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/>
    ///   resolves to a recording in
    ///   <see cref="RecordingStore.CommittedRecordings"/> with
    ///   <see cref="MergeState"/> = <see cref="MergeState.NotCommitted"/>.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.OriginChildRecordingId"/>
    ///   resolves to a recording in
    ///   <see cref="RecordingStore.CommittedRecordings"/>.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.SupersedeTargetId"/>
    ///   is either null or weakly resolves to a committed recording; invalid
    ///   non-null values are cleared without invalidating the marker.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.RewindPointId"/>
    ///   resolves to a live entry in
    ///   <see cref="ParsekScenario.RewindPoints"/>.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.InvokedUT"/>
    ///   is a finite, non-negative game UT within Parsek's sanity ceiling.
    ///   The current Planetarium UT (set via <see cref="NowUtProvider"/>
    ///   in tests; <c>Planetarium.GetUniversalTime()</c> in production) is
    ///   captured only for diagnostics because fresh scene loads can report
    ///   UT 0 before the loaded save's clock is meaningful.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The validator only mutates weak optional fields: an invalid non-null
    /// <see cref="ReFlySessionMarker.SupersedeTargetId"/> is cleared and logged
    /// without invalidating the whole marker. The caller
    /// (<see cref="LoadTimeSweep"/>) is responsible for clearing the marker on
    /// <see cref="MarkerValidationResult.Valid"/> = <c>false</c>.
    /// </para>
    /// </summary>
    internal static class MarkerValidator
    {
        private const string SessionTag = "ReFlySession";

        // 1e15 seconds is ~31.7 million years: far beyond any legitimate
        // KSP campaign, while still catching overflow/garbage marker values.
        internal const double MaxReasonableInvokedUT = 1.0e15;

        // Log-facing summary only. Keep in sync with the accept path below so
        // "Marker valid" lines do not drift from the actual validator rules.
        private const string AcceptedMarkerCheckPaths =
            "SessionId.nonEmpty,TreeId.exists,ActiveReFlyRecordingId.exists+mergeState," +
            "OriginChildRecordingId.exists,SupersedeTargetId.nullOrExists," +
            "RewindPointId.exists,InvokedUT.finite>=0<=1E+15";

        /// <summary>
        /// Test seam: override the "current UT" captured for diagnostics.
        /// Production code leaves this null and the validator falls back to
        /// <c>Planetarium.GetUniversalTime()</c>, guarded so tests that do not
        /// have a Planetarium singleton still work (returns
        /// <see cref="double.PositiveInfinity"/>).
        /// </summary>
        internal static Func<double> NowUtProvider;

        /// <summary>Clears all test seams.</summary>
        internal static void ResetTestOverrides()
        {
            NowUtProvider = null;
        }

        /// <summary>
        /// Validates the durable fields of <paramref name="marker"/>.
        /// A null marker returns <see cref="MarkerValidationResult.Valid"/> =
        /// <c>true</c> (no marker means "no session to validate", which is a
        /// legitimate state and the sweep short-circuits downstream).
        /// </summary>
        public static MarkerValidationResult Validate(ReFlySessionMarker marker)
        {
            if (marker == null)
                return MarkerValidationResult.Ok();

            if (string.IsNullOrEmpty(marker.SessionId))
                return MarkerValidationResult.Invalid(
                    "SessionId",
                    "checked=SessionId.nonEmpty; rejected because SessionId is empty");

            if (string.IsNullOrEmpty(marker.TreeId))
                return MarkerValidationResult.Invalid(
                    "TreeId",
                    "checked=TreeId.nonEmpty; rejected because TreeId is empty");
            if (!TreeExists(marker.TreeId))
                return MarkerValidationResult.Invalid(
                    "TreeId",
                    "checked=TreeId.exists; rejected because TreeId was not found in RecordingStore.CommittedTrees or PendingTree");

            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return MarkerValidationResult.Invalid(
                    "ActiveReFlyRecordingId",
                    "checked=ActiveReFlyRecordingId.nonEmpty; rejected because ActiveReFlyRecordingId is empty");
            var active = FindRecordingById(marker.ActiveReFlyRecordingId, marker.TreeId);
            if (active == null)
                return MarkerValidationResult.Invalid(
                    "ActiveReFlyRecordingId",
                    "checked=ActiveReFlyRecordingId.exists; rejected because active recording was not found in RecordingStore.CommittedRecordings");

            if (string.IsNullOrEmpty(marker.OriginChildRecordingId))
                return MarkerValidationResult.Invalid(
                    "OriginChildRecordingId",
                    "checked=OriginChildRecordingId.nonEmpty; rejected because OriginChildRecordingId is empty");
            var origin = FindRecordingById(marker.OriginChildRecordingId, marker.TreeId);
            if (origin == null)
                return MarkerValidationResult.Invalid(
                    "OriginChildRecordingId",
                    "checked=OriginChildRecordingId.exists; rejected because origin recording was not found in RecordingStore.CommittedRecordings");

            ValidateSupersedeTargetWeak(marker);

            // MergeState gate. The placeholder pattern (origin != active)
            // creates a fresh `NotCommitted` recording at re-fly start, so
            // the active row is always `NotCommitted` for that path. The
            // in-place continuation pattern (`origin == active`, see
            // RewindInvoker.AtomicMarkerWrite) reuses the existing
            // recording — and that recording can be EITHER
            // `CommittedProvisional` (a UF promoted from a fresh tree merge)
            // OR `Immutable` (a UF from an older / sealed recording where
            // the RP is still alive). `EffectiveState.IsUnfinishedFlight`
            // explicitly accepts both states (line 156-157), so the
            // validator MUST too — otherwise every save+load cycle during
            // an in-place re-fly silently wipes the marker and the merge
            // falls through to the regular tree-merge path (no force-
            // Immutable, no RP reap, UF row never clears). The placeholder
            // pattern stays NotCommitted-only because no committed
            // recording is being reused there.
            bool inPlaceContinuation = string.Equals(
                marker.ActiveReFlyRecordingId,
                marker.OriginChildRecordingId,
                StringComparison.Ordinal);
            bool acceptableState =
                active.MergeState == MergeState.NotCommitted
                || (inPlaceContinuation
                    && (active.MergeState == MergeState.CommittedProvisional
                        || active.MergeState == MergeState.Immutable));
            if (!acceptableState)
            {
                return MarkerValidationResult.Invalid(
                    "ActiveReFlyRecordingId",
                    "checked=ActiveReFlyRecordingId.mergeState " +
                    $"state={active.MergeState} inPlace={inPlaceContinuation} " +
                    "expected=NotCommitted-or-inPlace-CommittedProvisional/Immutable; rejected because state/pattern is not valid for a live marker");
            }

            if (string.IsNullOrEmpty(marker.RewindPointId))
                return MarkerValidationResult.Invalid(
                    "RewindPointId",
                    "checked=RewindPointId.nonEmpty; rejected because RewindPointId is empty");
            var rewindPoint = FindRewindPointById(marker.RewindPointId);
            if (rewindPoint == null)
                return MarkerValidationResult.Invalid(
                    "RewindPointId",
                    "checked=RewindPointId.exists; rejected because rewind point was not found in ParsekScenario.RewindPoints");

            double now = CurrentUt();
            if (!IsFinite(marker.InvokedUT))
                return MarkerValidationResult.Invalid(
                    "InvokedUT",
                    BuildInvokedUtDetails(marker.InvokedUT, now, rewindPoint.UT,
                        "not-finite",
                        "rejected because InvokedUT is NaN or Infinity"));
            if (marker.InvokedUT < 0.0)
                return MarkerValidationResult.Invalid(
                    "InvokedUT",
                    BuildInvokedUtDetails(marker.InvokedUT, now, rewindPoint.UT,
                        "negative",
                        "rejected because InvokedUT is less than 0"));
            if (marker.InvokedUT > MaxReasonableInvokedUT)
                return MarkerValidationResult.Invalid(
                    "InvokedUT",
                    BuildInvokedUtDetails(marker.InvokedUT, now, rewindPoint.UT,
                        "exceeds-sanity-ceiling",
                        "rejected because InvokedUT exceeds the 1E+15 sanity ceiling"));

            return MarkerValidationResult.Ok(
                BuildAcceptedDetails(active, inPlaceContinuation, marker.InvokedUT, now, rewindPoint.UT));
        }

        private static bool TreeExists(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    var t = trees[i];
                    if (t == null) continue;
                    if (string.Equals(t.Id, treeId, StringComparison.Ordinal))
                        return true;
                }
            }

            var pendingTree = RecordingStore.PendingTree;
            if (pendingTree != null
                && string.Equals(pendingTree.Id, treeId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static Recording FindRecordingById(string recordingId, string treeId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // Allowlisted raw read: the validator MUST see NotCommitted
            // recordings (ERS filters them out).
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                        return rec;
                }
            }

            var pendingTree = RecordingStore.PendingTree;
            if (pendingTree != null
                && string.Equals(pendingTree.Id, treeId, StringComparison.Ordinal)
                && pendingTree.Recordings != null
                && pendingTree.Recordings.TryGetValue(recordingId, out var pendingRec))
            {
                return pendingRec;
            }

            return null;
        }

        private static RewindPoint FindRewindPointById(string rewindPointId)
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario)) return null;
            var rps = scenario.RewindPoints;
            if (rps == null) return null;
            for (int i = 0; i < rps.Count; i++)
            {
                var rp = rps[i];
                if (rp == null) continue;
                if (string.Equals(rp.RewindPointId, rewindPointId, StringComparison.Ordinal))
                    return rp;
            }
            return null;
        }

        private static void ValidateSupersedeTargetWeak(ReFlySessionMarker marker)
        {
            if (marker == null || string.IsNullOrEmpty(marker.SupersedeTargetId))
                return;

            if (FindCommittedRecordingById(marker.SupersedeTargetId) != null)
                return;

            string invalidTarget = marker.SupersedeTargetId;
            marker.SupersedeTargetId = null;
            ParsekLog.Warn(SessionTag,
                $"Marker invalid field=SupersedeTargetId; clearing " +
                $"sess={marker.SessionId ?? "<no-id>"} " +
                $"target={invalidTarget ?? "<none>"}");
        }

        private static Recording FindCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        private static double CurrentUt()
        {
            var hook = NowUtProvider;
            if (hook != null)
            {
                try { return hook(); }
                catch { /* fall through */ }
            }
            try { return Planetarium.GetUniversalTime(); }
            catch
            {
                // No Planetarium singleton (unit tests without KSP). Use
                // +infinity so diagnostics clearly show the live clock was
                // unavailable without turning that into a hard rejection.
                return double.PositiveInfinity;
            }
        }

        private static string BuildAcceptedDetails(
            Recording active,
            bool inPlaceContinuation,
            double invokedUt,
            double currentUt,
            double rewindPointUt)
        {
            bool legacyFutureUtCheckTriggered = IsFinite(currentUt) && invokedUt > currentUt;
            return $"checked={AcceptedMarkerCheckPaths}; " +
                $"activeState={active.MergeState} inPlace={inPlaceContinuation} " +
                BuildInvokedUtComparison(invokedUt, currentUt, rewindPointUt) + " " +
                $"legacyFutureUtCheck={(legacyFutureUtCheckTriggered ? "triggered" : "none")}";
        }

        private static string BuildInvokedUtDetails(
            double invokedUt,
            double currentUt,
            double rewindPointUt,
            string failure,
            string explanation)
        {
            return "checked=InvokedUT.finite>=0<=1E+15; " +
                BuildInvokedUtComparison(invokedUt, currentUt, rewindPointUt) + " " +
                $"failure={failure}; {explanation}";
        }

        private static string BuildInvokedUtComparison(
            double invokedUt,
            double currentUt,
            double rewindPointUt)
        {
            double deltaCurrent = invokedUt - currentUt;
            double deltaRewindPoint = invokedUt - rewindPointUt;
            return $"invokedUT={FormatDouble(invokedUt)} " +
                $"currentUT={FormatDouble(currentUt)} " +
                $"rpUT={FormatDouble(rewindPointUt)} " +
                $"deltaCurrent={FormatDouble(deltaCurrent)} " +
                $"deltaRp={FormatDouble(deltaRewindPoint)} " +
                $"maxReasonableInvokedUT={FormatDouble(MaxReasonableInvokedUT)}";
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
