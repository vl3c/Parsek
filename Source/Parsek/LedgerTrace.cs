using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Gated, EVENT-DRIVEN observability for the ledger / game-actions apply
    /// boundary (the code that rewrites real career scalars — funds, science,
    /// reputation, facility levels, tech nodes, contracts — every recalc).
    /// Off by default behind the <c>ledgerTracing</c> setting; read-only
    /// instrumentation that never mutates ledger or KSP state.
    ///
    /// <para><b>Why event-driven, not a per-frame probe.</b> Unlike the render
    /// tracer (<see cref="MapRenderTrace"/> + its per-frame <c>MapRenderProbe</c>
    /// truth probe), ledger state changes only on discrete recalc / patch events,
    /// not every frame. So there is NO MonoBehaviour LateUpdate probe here: the
    /// read-back reconcile runs SYNCHRONOUSLY inside each <c>KspStatePatcher</c>
    /// <c>Patch*</c> call, immediately after that patch writes the live value.
    /// There is also NO detailed-window registry (the render tracer keeps
    /// per-frame detail around interesting events; there is no per-frame stream to
    /// keep a window over here — each recalc emits its lines once).</para>
    ///
    /// <para><b>Intent → truth → reconcile mapping</b> (the render tracer's core
    /// mechanism, adapted): <i>intent</i> = the computed recalc target the patcher
    /// is about to write; <i>truth</i> = the live value read back from
    /// <c>Funding</c> / <c>ResearchAndDevelopment</c> / <c>Reputation.Instance</c>
    /// + the facility level after the write; <i>per-entity key</i> = an
    /// action / subject / node / facility / contract id (NOT a vessel
    /// <c>persistentId</c> as in the render tracer). A pure, Unity-free predicate
    /// (e.g. <see cref="IsResourceDrift"/>) decides mismatch → Tier-C anomaly.</para>
    ///
    /// <para><b>Three tiers</b> (mirroring the render tracer):
    /// Tier-A structural (<see cref="EmitStructural"/> → Info, one grep-stable
    /// snapshot per recalc); Tier-B change-based truth (<see cref="EmitOnChange"/>
    /// → Verbose, one line per CHANGED identity, primarily driven by the #1098
    /// changed-sets at the call sites with the owned <c>lastValueByKey</c> dict as
    /// the steady-state guard); Tier-C anomaly (<see cref="EmitAnomaly"/> → Warn,
    /// the read-back <c>ledger-vs-truth</c> reconcile via the pure predicates).</para>
    ///
    /// <para>The formatters are SELF-CONTAINED (duplicated, copied from
    /// <see cref="MapRenderTrace"/>), deliberately not shared — do NOT refactor a
    /// shared formatter out of the render tracers.</para>
    /// </summary>
    internal static class LedgerTrace
    {
        // Single subsystem tag for the whole ledger apply surface, so one grep
        // filter lights up every tier around a recalc.
        internal const string Tag = "LedgerTrace";

        // ---- Gate (near-zero cost when off: one bool early-return per emit) ----

        internal static bool ForceEnabledForTesting;

        internal static bool IsEnabled =>
            ForceEnabledForTesting
            || (ParsekSettings.Current != null && ParsekSettings.Current.ledgerTracing);

        // ---- Recalc lifecycle (owned sequence + Tier-B steady-state guard) ----

        // Monotonic recalc-burst sequence. Every line emitted during one recalc
        // shares this value (carried in BuildPrefix), so a grep slices the log to a
        // single reconstruction. Owned here (LedgerTrace mints it; nothing else
        // touches it), unlike the render tracer which keys on the Unity frame.
        private static long recalcSeq;

        // Tier-B owned steady-state guard: the last changeDetails emitted per
        // "resource|id" key. EmitOnChange suppresses a repeat of the same details
        // for the same key. Cleared on each BeginRecalc so a fresh recalc burst
        // never inherits a stale value (the #1098 changed-sets are the PRIMARY
        // change signal at the call sites; this dict is the secondary guard the
        // gating / steady-state tests drive).
        private static readonly Dictionary<string, string> lastValueByKey =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Opens a recalc burst: bumps the owned <see cref="recalcSeq"/> so every
        /// line in this burst shares a grep-sliceable sequence, and clears the
        /// Tier-B steady-state dict so a fresh burst's first change always emits.
        /// No-op when disabled (a closed tracer never advances the sequence or
        /// touches the dict).
        /// </summary>
        internal static void BeginRecalc()
        {
            if (!IsEnabled)
                return;
            recalcSeq++;
            lastValueByKey.Clear();
        }

        /// <summary>
        /// Test-only reset: clears the Tier-B dict, zeroes the sequence, and clears
        /// the force-enable seam. Called from test ctor/Dispose so shared static
        /// state never leaks between cases.
        /// </summary>
        internal static void ResetForTesting()
        {
            lastValueByKey.Clear();
            recalcSeq = 0;
            ForceEnabledForTesting = false;
        }

        /// <summary>Test-only: the current owned recalc sequence value.</summary>
        internal static long CurrentRecalcSeqForTesting => recalcSeq;

        // ---- Tier-C anomaly predicates (pure; Unity-free; exhaustively tested) ----

        /// <summary>
        /// Per-resource read-back tolerance: the same magnitudes the
        /// <c>KspStatePatcher</c> <c>Patch*</c> no-op guards use (funds 0.01,
        /// science 0.001, reputation 0.01), so a drift the patcher would treat as
        /// "no change needed" is never flagged. Unknown resource names default to
        /// the loosest (0.01).
        /// </summary>
        internal static double ResourceTolerance(string resource)
        {
            if (string.Equals(resource, "science", StringComparison.OrdinalIgnoreCase)
                || string.Equals(resource, "subject-science", StringComparison.OrdinalIgnoreCase))
                return 0.001;
            // funds and reputation both use 0.01; unknown falls back to the same.
            return 0.01;
        }

        /// <summary>
        /// Pure <c>resource-drift</c> predicate (Tier C): true when the live read-back
        /// <paramref name="actual"/> diverges from the computed <paramref name="target"/>
        /// by more than <paramref name="tol"/>. Mirrors RewindReadbackGuard semantics
        /// for non-finite inputs: a NaN/Inf <paramref name="actual"/> returns false (no
        /// trustworthy read-back to compare, so do not flag), but a NaN/Inf
        /// <paramref name="target"/> returns true (a corrupt COMPUTED target is itself
        /// the bug). A boundary <c>|diff| == tol</c> is within tolerance → false.
        /// </summary>
        internal static bool IsResourceDrift(double target, double actual, double tol)
        {
            if (double.IsNaN(target) || double.IsInfinity(target))
                return true;
            if (double.IsNaN(actual) || double.IsInfinity(actual))
                return false;
            return Math.Abs(target - actual) > tol;
        }

        /// <summary>
        /// Pure <c>subject-science-drift</c> predicate (Tier C). Same shape as
        /// <see cref="IsResourceDrift"/> (the per-subject science write is the
        /// UNCLAMPED gap-1 path, so its read-back is worth its own named predicate):
        /// NaN/Inf <paramref name="actual"/> → false; NaN/Inf <paramref name="target"/>
        /// → true; boundary at <paramref name="tol"/> → within tolerance → false.
        /// </summary>
        internal static bool IsSubjectScienceDrift(double target, double actual, double tol)
        {
            if (double.IsNaN(target) || double.IsInfinity(target))
                return true;
            if (double.IsNaN(actual) || double.IsInfinity(actual))
                return false;
            return Math.Abs(target - actual) > tol;
        }

        /// <summary>
        /// Pure <c>facility-level-mismatch</c> predicate (Tier C): true when the
        /// live read-back facility level differs from the computed target level.
        /// Integers — no tolerance.
        /// </summary>
        internal static bool IsFacilityLevelMismatch(int targetLevel, int actualLevel)
        {
            return targetLevel != actualLevel;
        }

        /// <summary>
        /// Pure <c>tech-node-presence-mismatch</c> predicate (Tier C): true when the
        /// intended availability of a tech node disagrees with the live read-back
        /// availability (a wrongly re-locked researched node, or a node that failed
        /// to become available).
        /// </summary>
        internal static bool IsTechNodePresenceMismatch(bool intendedAvailable, bool actualAvailable)
        {
            return intendedAvailable != actualAvailable;
        }

        /// <summary>
        /// Pure <c>contract-presence-mismatch</c> predicate (Tier C): true when the
        /// intended presence of a contract (restored → should be present; removed →
        /// should be absent) disagrees with the live read-back presence.
        /// </summary>
        internal static bool IsContractPresenceMismatch(bool intendedPresent, bool actualPresent)
        {
            return intendedPresent != actualPresent;
        }

        // ---- Tier-A structural snapshot ----

        /// <summary>
        /// One recalc's structural state, captured AFTER the apply burst from data
        /// already in hand at the orchestrator. Funds / science / rep are the
        /// running/available pool targets; <see cref="Facilities"/> is a compact
        /// <c>name:level</c> list; <see cref="TechNodes"/> / <see cref="Contracts"/>
        /// are counts; <see cref="Cutoff"/> is the UT cutoff (null on a live walk);
        /// <see cref="AuthoritativeReduction"/> is the LedgerOrchestrator
        /// authorized-drawdown decision.
        /// </summary>
        internal struct LedgerStructuralSnapshot
        {
            public double Funds;
            public double Science;
            public double Reputation;
            public string Facilities;
            public int TechNodes;
            public int Contracts;
            public double? Cutoff;
            public bool AuthoritativeReduction;
        }

        /// <summary>
        /// Tier-A structural emit: one grep-stable <c>phase=Structural</c> Info line
        /// per recalc when enabled. Early-returns when disabled so the call site
        /// pays only its own <see cref="IsEnabled"/> guard. Emit ONCE per recalc
        /// (from the orchestrator), never from inside a Patch* (which would emit 7x).
        /// </summary>
        internal static void EmitStructural(LedgerStructuralSnapshot snap)
        {
            if (!IsEnabled)
                return;
            ParsekLog.Info(Tag, FormatStructural(recalcSeq, snap));
        }

        /// <summary>
        /// PURE, grep-stable, field-ordered structural line builder. Field order is
        /// fixed (a downstream grep / parser relies on it): <c>phase=Structural
        /// recalcSeq=N funds=… science=… rep=… facilities=[…] techNodes=N contracts=M
        /// cutoff=&lt;R|null&gt; authReduction=&lt;bool&gt;</c>. NaN/Inf-safe via
        /// <see cref="FormatDouble"/>.
        /// </summary>
        internal static string FormatStructural(long recalcSeq, LedgerStructuralSnapshot snap)
        {
            string cutoff = snap.Cutoff.HasValue
                ? snap.Cutoff.Value.ToString("R", CultureInfo.InvariantCulture)
                : "null";
            return "phase=Structural"
                + " recalcSeq=" + recalcSeq.ToString(CultureInfo.InvariantCulture)
                + " funds=" + FormatDouble(snap.Funds, "F2")
                + " science=" + FormatDouble(snap.Science, "F2")
                + " rep=" + FormatDouble(snap.Reputation, "F2")
                + " facilities=[" + (string.IsNullOrEmpty(snap.Facilities) ? string.Empty : snap.Facilities) + "]"
                + " techNodes=" + snap.TechNodes.ToString(CultureInfo.InvariantCulture)
                + " contracts=" + snap.Contracts.ToString(CultureInfo.InvariantCulture)
                + " cutoff=" + cutoff
                + " authReduction=" + Bool(snap.AuthoritativeReduction);
        }

        // ---- Tier-B change-based truth ----

        /// <summary>
        /// Tier-B change-based emit: one <c>phase=Change</c> Verbose line for one
        /// identity whose value just changed within the current recalc. Keyed by
        /// <c>resource|id</c>; emits ONLY when <paramref name="changeDetails"/>
        /// differs from the last stored details for that key (the owned dict is the
        /// steady-state guard; the call sites primarily drive this off the #1098
        /// changed-sets, which already fire only on a flip). Early-returns when
        /// disabled. Carries the owned <see cref="recalcSeq"/>.
        /// </summary>
        internal static void EmitOnChange(string resource, string id, string changeDetails)
        {
            if (!IsEnabled)
                return;

            string key = Token(resource) + "|" + Token(id);
            string last;
            if (lastValueByKey.TryGetValue(key, out last)
                && string.Equals(last, changeDetails, StringComparison.Ordinal))
                return; // unchanged for this identity this recalc -> suppress

            lastValueByKey[key] = changeDetails;
            ParsekLog.Verbose(Tag, FormatChange(recalcSeq, resource, id, changeDetails));
        }

        /// <summary>PURE Tier-B change-line builder (grep-stable field order).</summary>
        internal static string FormatChange(long recalcSeq, string resource, string id, string changeDetails)
        {
            return "phase=Change"
                + " recalcSeq=" + recalcSeq.ToString(CultureInfo.InvariantCulture)
                + " resource=" + Token(resource)
                + " id=" + Token(id)
                + " change=" + Token(changeDetails);
        }

        // ---- Tier-C anomaly ----

        /// <summary>
        /// Tier-C anomaly emit: a loud <c>phase=Anomaly reason=ledger-vs-truth</c>
        /// Warn naming the diverging resource + identity and carrying the caller's
        /// detail tail (target / actual / delta / tol). Early-returns when disabled.
        /// </summary>
        internal static void EmitAnomaly(string resource, string id, string reason, string details)
        {
            if (!IsEnabled)
                return;
            ParsekLog.Warn(Tag, FormatAnomaly(recalcSeq, resource, id, reason, details));
        }

        /// <summary>PURE Tier-C anomaly-line builder (grep-stable field order).</summary>
        internal static string FormatAnomaly(long recalcSeq, string resource, string id, string reason, string details)
        {
            return "phase=Anomaly"
                + " recalcSeq=" + recalcSeq.ToString(CultureInfo.InvariantCulture)
                + " resource=" + Token(resource)
                + " id=" + Token(id)
                + " reason=" + Token(reason)
                + (string.IsNullOrEmpty(details) ? string.Empty : " " + details);
        }

        // ---- Self-contained formatters (duplicated from MapRenderTrace; NOT shared) ----

        /// <summary>
        /// NaN/Inf-safe double formatter, copied verbatim from
        /// <see cref="MapRenderTrace.FormatDouble"/> so the two tracers share one
        /// numeric schema without sharing code.
        /// </summary>
        internal static string FormatDouble(double value, string format)
        {
            if (double.IsNaN(value))
                return "NaN";
            if (double.IsPositiveInfinity(value))
                return "Infinity";
            if (double.IsNegativeInfinity(value))
                return "-Infinity";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        internal static string Token(string value)
        {
            return string.IsNullOrEmpty(value) ? "<none>" : value.Replace(' ', '_');
        }

        internal static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        /// <summary>
        /// PURE prefix builder for ad-hoc lines that want the owned
        /// <see cref="recalcSeq"/> in front (so every line of one burst is
        /// grep-sliceable by a single sequence). The tier emitters build their own
        /// full lines via the Format* helpers; this is exposed for testing the
        /// sequence-stamping contract.
        /// </summary>
        internal static string BuildPrefix(string phase, long recalcSeq)
        {
            return "phase=" + Token(phase)
                + " recalcSeq=" + recalcSeq.ToString(CultureInfo.InvariantCulture);
        }
    }
}
