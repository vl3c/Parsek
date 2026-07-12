using System;
using System.Collections.Generic;

namespace Parsek.Analyzer
{
    // Core model types for the M-A1 offline recording analyzer (module M-A1,
    // design doc docs/dev/design-autotest-offline-analyzer.md "Data Model").
    //
    // Everything here is the PURE, already-loaded input the invariant core runs
    // over. No Stream, no path, no FileInfo lives on AnalyzerModel: the loader
    // owns all file I/O (SaveDirectoryLoader) and the rules are pure functions
    // over the loaded objects so the future in-game H5 category (module M-A3)
    // can feed the same core a live RecordingStore and get identical findings.
    //
    // Types are internal: this pure core lives in Parsek.dll (module M-A3 moved it
    // here so the in-game H5 category can reuse it) and stays internal to Parsek,
    // reachable from Parsek.Tests through InternalsVisibleTo("Parsek.Tests").

    /// <summary>
    /// Verdict severity for a single <see cref="Finding"/>. Numeric order is
    /// load-bearing: report findings sort by (level DESC, ...), so StaleFixture
    /// prints before Fail before Warn before Info.
    /// </summary>
    internal enum VerdictLevel
    {
        Info = 0,
        Warn = 1,
        Fail = 2,
        StaleFixture = 3,
    }

    /// <summary>
    /// One observation from one rule. Immutable value type. <see cref="CitedContract"/>
    /// is REQUIRED on every finding (and every rule): it names the production
    /// member that defines the contract the rule checks, so a rule asserting a
    /// wrong contract dies in code review rather than as a nightly false alarm.
    /// </summary>
    internal struct Finding
    {
        /// <summary>Stable rule id, e.g. "INV1-UT-MONOTONIC".</summary>
        public string RuleId;

        public VerdictLevel Level;

        /// <summary>recordingId / treeId / "&lt;save&gt;" / file path.</summary>
        public string Target;

        /// <summary>-1 when the finding is not section-scoped.</summary>
        public int SectionIndex;

        /// <summary>Human, one line, ASCII.</summary>
        public string Message;

        /// <summary>Production member the rule checks (REQUIRED, never empty).</summary>
        public string CitedContract;

        public Finding(
            string ruleId,
            VerdictLevel level,
            string target,
            int sectionIndex,
            string message,
            string citedContract)
        {
            RuleId = ruleId;
            Level = level;
            Target = target;
            SectionIndex = sectionIndex;
            Message = message;
            CitedContract = citedContract;
        }
    }

    /// <summary>
    /// A file that failed to parse. In the analyzer a parse failure is a finding,
    /// never a crash: the loader records one <see cref="LoadFault"/> and keeps
    /// going. Rules (INV5 / INV4 / INV9) read these to emit their verdicts.
    /// </summary>
    internal struct LoadFault
    {
        public string FilePath;

        /// <summary>
        /// "sfs" / "trajectory" / "snapshot" / "tree-node" / "ledger" /
        /// "rewindpoint" / "annotation" / "career".
        /// </summary>
        public string FileKind;

        /// <summary>Parser's failure reason string.</summary>
        public string Reason;

        /// <summary>Recording id when resolvable from the path, else null.</summary>
        public string RecordingId;

        public LoadFault(string filePath, string fileKind, string reason, string recordingId)
        {
            FilePath = filePath;
            FileKind = fileKind;
            Reason = reason;
            RecordingId = recordingId;
        }
    }

    /// <summary>
    /// Fixture-corpus provenance stamp read from a corpus's
    /// <c>fixture-generation.txt</c>. Null for non-fixture subjects (harness /
    /// triage saves), which skip the STALE-FIXTURE check entirely.
    /// </summary>
    internal struct FixtureStamp
    {
        public int SchemaGeneration;

        /// <summary>"synthetic" | "harvested".</summary>
        public string Provenance;

        public FixtureStamp(int schemaGeneration, string provenance)
        {
            SchemaGeneration = schemaGeneration;
            Provenance = provenance;
        }
    }

    /// <summary>
    /// One named invariant over the loaded model. Every implementation MUST
    /// declare a non-empty <see cref="CitedContract"/> (enforced by the
    /// CitedContract-presence reflection test) and MUST NOT touch a file or a
    /// Stream (enforced by the core-purity test), so the same rule set is reusable
    /// in-game (module M-A3 / hook H5) against a live RecordingStore.
    /// </summary>
    internal interface IRecordingInvariant
    {
        string RuleId { get; }

        /// <summary>Production member the rule checks, e.g. "RecordingStore.IsRecordingSchemaCompatible".</summary>
        string CitedContract { get; }

        IEnumerable<Finding> Evaluate(AnalyzerModel model);
    }

    /// <summary>
    /// The pure, already-loaded input to the invariant core. Built by
    /// <c>SaveDirectoryLoader</c> offline and (future) by the in-game H5 walker.
    /// No file I/O surface lives here.
    /// </summary>
    internal sealed class AnalyzerModel
    {
        public string SaveName { get; set; }

        /// <summary>Flat list, all trees flattened.</summary>
        public IReadOnlyList<Recording> Recordings { get; set; } = new List<Recording>();

        public IReadOnlyList<RecordingTree> Trees { get; set; } = new List<RecordingTree>();

        public IReadOnlyList<LedgerTombstone> Tombstones { get; set; } = new List<LedgerTombstone>();

        public IReadOnlyList<RecordingSupersedeRelation> SupersedeRelations { get; set; }
            = new List<RecordingSupersedeRelation>();

        /// <summary>Null when not career / unparsable.</summary>
        public CareerSaveSnapshot CareerSave { get; set; }

        /// <summary>
        /// RAW ledger actions (unfiltered), as materialized by the builder. INV8
        /// computes the ELS filter internally from <see cref="Tombstones"/>; the
        /// builder NEVER pre-filters (a pre-filtered list would make INV8's
        /// dangling-tombstone check vacuous by construction). This pure core reads
        /// no store symbols itself -- the caller supplies the materialized list.
        /// </summary>
        public IReadOnlyList<GameAction> Ledger { get; set; } = new List<GameAction>();

        /// <summary>Parse failures the loader recorded.</summary>
        public IReadOnlyList<LoadFault> LoadFaults { get; set; } = new List<LoadFault>();

        /// <summary>Null for non-fixture subjects.</summary>
        public FixtureStamp? FixtureStamp { get; set; }

        /// <summary>
        /// Side-table (correction C2) keyed by recording id, carrying the
        /// (schemaGeneration, formatVersion) each trajectory sidecar probe reported
        /// during load. INV5 uses it for the metadata-vs-sidecar generation-mismatch
        /// case; keys with no matching recording are orphan sidecars.
        /// </summary>
        public IReadOnlyDictionary<string, (int Generation, int FormatVersion)> SidecarSchema { get; set; }
            = new Dictionary<string, (int, int)>();

        /// <summary>Injected body resolver; TestBodyRegistry in xUnit.</summary>
        public Func<string, CelestialBody> BodyResolver { get; set; }

        /// <summary>
        /// The analyzed save directory, set by <c>SaveDirectoryLoader.Load</c>; null
        /// for a purely in-memory model (the H5 in-game walker and the core-purity
        /// test). Two SAVE-SCOPED rules read it to reach sidecars the loader does not
        /// pre-materialize into the model -- INV7b (.pann annotation staleness,
        /// probed against the paired .prec) and INV9 (RewindPoint quicksave
        /// existence) -- and no-op when it is null, so the pure invariant core stays
        /// reusable. It is the ONLY path-bearing field on the model; the design's
        /// "no path on the model" ideal yields here to the plan's explicit
        /// requirement that INV7b/INV9 probe files scoped to a save.
        ///
        /// Note these are not the only rules that touch the filesystem: INV10
        /// round-trips the trajectory codec through a SCRATCH temp file (its own,
        /// never save-scoped, so it does not read this field). The core-purity gate
        /// (InvariantRegistryTests.CorePurity_AllRules_RunOverInMemoryModel_WithoutFileAccess)
        /// therefore asserts NO RULE THROWS over an in-memory model (SaveDirectory
        /// null), NOT that no rule performs any file I/O -- a rule may touch scratch
        /// files as long as it stays exception-safe when the model carries no save.
        /// </summary>
        public string SaveDirectory { get; set; }

        /// <summary>
        /// Optional headless ledger reconstruction for INV8 part (b)'s career diff.
        /// Null offline in v1: the full headless recalculation seam is deferred
        /// (correction C5), so the loader never builds one and INV8(b) reports
        /// reconstruction-not-available INFO for career saves. Present only when a
        /// caller (a test, or a future headless recalc seam / the in-game H5 path)
        /// supplies one, in which case INV8(b) diffs it via
        /// <c>LedgerGroundTruthDiff.Compare</c>.
        /// </summary>
        public LedgerReconstructionSnapshot LedgerReconstruction { get; set; }
    }
}
