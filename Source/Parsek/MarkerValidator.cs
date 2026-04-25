using System;

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

        public static MarkerValidationResult Ok()
        {
            return new MarkerValidationResult { Valid = true, Reason = null };
        }

        public static MarkerValidationResult Invalid(string reason)
        {
            return new MarkerValidationResult { Valid = false, Reason = reason };
        }
    }

    /// <summary>
    /// Phase 13 of Rewind-to-Staging (design §6.9 step 3): validates the six
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
    ///   <item><description><see cref="ReFlySessionMarker.RewindPointId"/>
    ///   resolves to a live entry in
    ///   <see cref="ParsekScenario.RewindPoints"/>.</description></item>
    ///   <item><description><see cref="ReFlySessionMarker.InvokedUT"/>
    ///   not strictly greater than the current Planetarium UT (set via
    ///   <see cref="NowUtProvider"/> in tests; <c>Planetarium.GetUniversalTime()</c>
    ///   in production).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The validator is pure — no mutation. The caller
    /// (<see cref="LoadTimeSweep"/>) is responsible for clearing the marker
    /// on <see cref="MarkerValidationResult.Valid"/> = <c>false</c>.
    /// </para>
    /// </summary>
    internal static class MarkerValidator
    {
        /// <summary>
        /// Test seam: override the "current UT" used for the future-UT check.
        /// Production code leaves this null and the validator falls back to
        /// <c>Planetarium.GetUniversalTime()</c>, guarded so tests that do not
        /// have a Planetarium singleton still work (returns
        /// <see cref="double.PositiveInfinity"/>, making any finite InvokedUT
        /// pass the future check).
        /// </summary>
        internal static Func<double> NowUtProvider;

        /// <summary>Clears all test seams.</summary>
        internal static void ResetTestOverrides()
        {
            NowUtProvider = null;
        }

        /// <summary>
        /// Validates the six durable fields of <paramref name="marker"/>.
        /// A null marker returns <see cref="MarkerValidationResult.Valid"/> =
        /// <c>true</c> (no marker means "no session to validate", which is a
        /// legitimate state and the sweep short-circuits downstream).
        /// </summary>
        public static MarkerValidationResult Validate(ReFlySessionMarker marker)
        {
            if (marker == null)
                return MarkerValidationResult.Ok();

            if (string.IsNullOrEmpty(marker.SessionId))
                return MarkerValidationResult.Invalid("SessionId");

            if (string.IsNullOrEmpty(marker.TreeId))
                return MarkerValidationResult.Invalid("TreeId");
            if (!TreeExists(marker.TreeId))
                return MarkerValidationResult.Invalid("TreeId");

            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return MarkerValidationResult.Invalid("ActiveReFlyRecordingId");
            var active = FindRecordingById(marker.ActiveReFlyRecordingId);
            if (active == null)
                return MarkerValidationResult.Invalid("ActiveReFlyRecordingId");

            if (string.IsNullOrEmpty(marker.OriginChildRecordingId))
                return MarkerValidationResult.Invalid("OriginChildRecordingId");
            var origin = FindRecordingById(marker.OriginChildRecordingId);
            if (origin == null)
                return MarkerValidationResult.Invalid("OriginChildRecordingId");

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
                return MarkerValidationResult.Invalid("ActiveReFlyRecordingId");

            if (string.IsNullOrEmpty(marker.RewindPointId))
                return MarkerValidationResult.Invalid("RewindPointId");
            if (!RewindPointExists(marker.RewindPointId))
                return MarkerValidationResult.Invalid("RewindPointId");

            double now = CurrentUt();
            if (marker.InvokedUT > now)
                return MarkerValidationResult.Invalid("InvokedUT");

            return MarkerValidationResult.Ok();
        }

        private static bool TreeExists(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return false;
            for (int i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                if (t == null) continue;
                if (string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // Allowlisted raw read: the validator MUST see NotCommitted
            // recordings (ERS filters them out).
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

        private static bool RewindPointExists(string rewindPointId)
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario)) return false;
            var rps = scenario.RewindPoints;
            if (rps == null) return false;
            for (int i = 0; i < rps.Count; i++)
            {
                var rp = rps[i];
                if (rp == null) continue;
                if (string.Equals(rp.RewindPointId, rewindPointId, StringComparison.Ordinal))
                    return true;
            }
            return false;
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
                // No Planetarium singleton (unit tests without KSP). Treat
                // "now" as +infinity so any finite InvokedUT passes — the
                // future-UT check is only meaningful against a live clock.
                return double.PositiveInfinity;
            }
        }
    }
}
