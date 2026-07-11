using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek.Analyzer.Rules
{
    // LOADER-FAULT rule (design doc "The invariant rules" / "The loader": a file
    // that fails to parse IS a finding, never a crash).
    //
    // The loader (SaveDirectoryLoader.Load) records a LoadFault for every file it
    // could not parse and keeps going. Trajectory (.prec) faults are owned by INV5
    // and snapshot (_vessel/_ghost.craft) faults by INV4, which each surface them
    // with their own recording-scoped context. Nothing, however, turned a corrupt
    // persistent.sfs, a throwing RECORDING tree node, or an unparsable ledger.pgld
    // into a report finding: those faults have no other rule, so a corrupt save
    // analyzed GREEN. This rule closes that hole by emitting a FAIL for every
    // LoadFault whose FileKind is one of {sfs, tree-node, ledger}, and deliberately
    // ignores the trajectory/snapshot kinds so a fault is reported exactly once.
    internal sealed class LoadFaultRule : IRecordingInvariant
    {
        internal const string RuleIdConst = "LOADER-FAULT";

        // FileKinds this rule owns. trajectory -> INV5, snapshot -> INV4; excluded
        // here so a single fault never produces two findings.
        private static readonly HashSet<string> OwnedKinds =
            new HashSet<string>(System.StringComparer.Ordinal)
            {
                "sfs",
                "tree-node",
                "ledger",
            };

        public string RuleId => RuleIdConst;

        public string CitedContract => "SaveDirectoryLoader.Load / ConfigNode.Load";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.LoadFaults == null)
                return findings;

            foreach (LoadFault f in model.LoadFaults)
            {
                if (!OwnedKinds.Contains(f.FileKind))
                    continue;

                // Target is deterministic (never the machine-specific absolute
                // path): the recording id when the fault carries one, else a
                // kind token. The filename (not the directory) goes in the
                // message so a triage grep can name the file without leaking the
                // temp/save path into the report bytes.
                string target = !string.IsNullOrEmpty(f.RecordingId)
                    ? f.RecordingId
                    : "<" + (f.FileKind ?? "unknown") + ">";
                string file = string.IsNullOrEmpty(f.FilePath) ? "" : Path.GetFileName(f.FilePath);

                findings.Add(new Finding(
                    RuleIdConst,
                    VerdictLevel.Fail,
                    target,
                    -1,
                    Inv("LOADER-FAULT kind={0} recording={1} file={2} reason={3}",
                        f.FileKind ?? "unknown",
                        f.RecordingId ?? "<none>",
                        file,
                        f.Reason ?? "unknown"),
                    "SaveDirectoryLoader.Load / ConfigNode.Load"));
            }

            return findings;
        }

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
