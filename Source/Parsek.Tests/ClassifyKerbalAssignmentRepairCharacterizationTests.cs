using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.ClassifyKerbalAssignmentRepair</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. The method and its stats type are reached by reflection, so the
    /// same test reads the original and the split method. Scenarios: null and empty inputs,
    /// tourist rows (any case) and null rows filtered out, reorder-only repairs, end-state
    /// rewrites, renames (with and without an end-state change), the timing, role and
    /// recording windows (including a NaN EndUT), first-unconsumed-row wins, the exact-name
    /// pass taking a row before the rename pass, and null desired rows. It records the
    /// tourist count, every remap and every end-state rewrite, in order.
    /// </summary>
    [Collection("Sequential")]
    public class ClassifyKerbalAssignmentRepairCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ClassifyKerbalAssignmentRepairCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static readonly MethodInfo Classify = typeof(LedgerOrchestrator).GetMethod(
            "ClassifyKerbalAssignmentRepair", BindingFlags.NonPublic | BindingFlags.Static);

        private static GameAction Row(string kerbal, KerbalEndState end = KerbalEndState.Aboard,
            string role = "Pilot", string rec = "rec-a", double ut = 100.0,
            float startUT = 100f, float endUT = 200f)
        {
            return new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = rec,
                KerbalName = kerbal,
                KerbalRole = role,
                KerbalEndStateField = end,
                UT = ut,
                StartUT = startUT,
                EndUT = endUT,
            };
        }

        private static object Field(object o, string name) =>
            o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);

        private void Case(StringBuilder sb, string label, List<GameAction> existing, List<GameAction> desired)
        {
            logLines.Clear();
            object stats = Classify.Invoke(null, new object[] { existing, desired });
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("touristSkipped=")
                .Append(Convert.ToString(Field(stats, "TouristRowsSkipped"), CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append("remaps=");
            bool first = true;
            foreach (object r in (IEnumerable)Field(stats, "RemappedRows"))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Field(r, "FromName") ?? "<null>").Append("->").Append(Field(r, "ToName") ?? "<null>");
            }
            sb.Append('\n');
            sb.Append("rewrites=");
            first = true;
            foreach (object w in (IEnumerable)Field(stats, "EndStateRewrites"))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Field(w, "KerbalName") ?? "<null>").Append(':')
                    .Append(Field(w, "FromState")).Append("->").Append(Field(w, "ToState"));
            }
            sb.Append('\n');
            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
        }

        private static void AssertGolden(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".txt");
            File.WriteAllText(path, actual);
            Assert.True(false, "golden mismatch for " + name + "; actual transcript written to " + path);
        }

        [Fact]
        public void ClassifyKerbalAssignmentRepair_Golden()
        {
            Assert.NotNull(Classify);
            var sb = new StringBuilder();
            var L = new Func<GameAction[], List<GameAction>>(a => new List<GameAction>(a));

            Case(sb, "K01 null existing", null, L(new[] { Row("Jeb") }));
            Case(sb, "K02 empty existing", L(new GameAction[0]), L(new[] { Row("Jeb") }));
            Case(sb, "K03 tourists and nulls only",
                L(new[] { Row("T1", role: "Tourist"), null, Row("T2", role: "tOURIST") }),
                L(new[] { Row("Jeb") }));
            Case(sb, "K04 desired null",
                L(new[] { Row("T1", role: "Tourist"), Row("Jeb") }), null);
            Case(sb, "K05 desired empty",
                L(new[] { Row("Jeb") }), L(new GameAction[0]));
            Case(sb, "K06 reorder only",
                L(new[] { Row("Jeb"), Row("Bill"), Row("Bob") }),
                L(new[] { Row("Bob"), Row("Jeb"), Row("Bill") }));
            Case(sb, "K07 end-state rewrites on the same name",
                L(new[] { Row("Jeb", KerbalEndState.Aboard), Row("Bill", KerbalEndState.Recovered) }),
                L(new[] { Row("Bill", KerbalEndState.Dead), Row("Jeb", KerbalEndState.Recovered) }));
            Case(sb, "K08 rename",
                L(new[] { Row("Jeb"), Row("Old") }),
                L(new[] { Row("Jeb"), Row("New") }));
            Case(sb, "K09 rename with an end-state change, tourist skipped",
                L(new[] { Row("T1", role: "Tourist"), Row("Old", KerbalEndState.Aboard) }),
                L(new[] { Row("New", KerbalEndState.Dead) }));
            Case(sb, "K10 timing windows",
                L(new[]
                {
                    Row("A", ut: 100.0),
                    Row("B", startUT: 100f),
                    Row("C", endUT: 200f),
                    Row("D", endUT: float.NaN),
                }),
                L(new[]
                {
                    Row("A2", ut: 100.125),
                    Row("B2", startUT: 100.0625f),
                    Row("C2", endUT: 200.125f),
                    Row("D2", endUT: 500f),
                }));
            Case(sb, "K11 role and recording mismatch",
                L(new[] { Row("Jeb", role: "Pilot"), Row("Bill", rec: "rec-b") }),
                L(new[] { Row("Jeb", role: "Engineer"), Row("Bill", rec: "rec-a") }));
            Case(sb, "K12 first unconsumed row wins",
                L(new[] { Row("X", KerbalEndState.Dead), Row("Y", KerbalEndState.Recovered), Row("Z") }),
                L(new[] { Row("P"), Row("Q"), Row("R"), Row("S") }));
            Case(sb, "K13 exact-name pass takes its row first",
                L(new[] { Row("Jeb", KerbalEndState.Dead), Row("Bill") }),
                L(new[] { Row("New"), Row("Jeb") }));
            Case(sb, "K14 null desired rows",
                L(new[] { Row("Jeb"), Row("Bill") }),
                L(new[] { null, Row("Bill", KerbalEndState.Dead), null, Row("Neo") }));
            Case(sb, "K15 duplicate names",
                L(new[] { Row("Jeb", KerbalEndState.Dead), Row("Jeb", KerbalEndState.Aboard) }),
                L(new[] { Row("Jeb", KerbalEndState.Aboard), Row("Jeb", KerbalEndState.Recovered), Row("Jeb") }));

            AssertGolden("ClassifyKerbalAssignmentRepair", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== K01 null existing",
            "touristSkipped=0",
            "remaps=",
            "rewrites=",
            "== K02 empty existing",
            "touristSkipped=0",
            "remaps=",
            "rewrites=",
            "== K03 tourists and nulls only",
            "touristSkipped=2",
            "remaps=",
            "rewrites=",
            "== K04 desired null",
            "touristSkipped=1",
            "remaps=",
            "rewrites=",
            "== K05 desired empty",
            "touristSkipped=0",
            "remaps=",
            "rewrites=",
            "== K06 reorder only",
            "touristSkipped=0",
            "remaps=",
            "rewrites=",
            "== K07 end-state rewrites on the same name",
            "touristSkipped=0",
            "remaps=",
            "rewrites=Bill:Recovered->Dead,Jeb:Aboard->Recovered",
            "== K08 rename",
            "touristSkipped=0",
            "remaps=Old->New",
            "rewrites=",
            "== K09 rename with an end-state change, tourist skipped",
            "touristSkipped=1",
            "remaps=Old->New",
            "rewrites=New:Aboard->Dead",
            "== K10 timing windows",
            "touristSkipped=0",
            "remaps=A->B2,D->C2",
            "rewrites=",
            "== K11 role and recording mismatch",
            "touristSkipped=0",
            "remaps=Jeb->Bill",
            "rewrites=",
            "== K12 first unconsumed row wins",
            "touristSkipped=0",
            "remaps=X->P,Y->Q,Z->R",
            "rewrites=P:Dead->Aboard,Q:Recovered->Aboard",
            "== K13 exact-name pass takes its row first",
            "touristSkipped=0",
            "remaps=Bill->New",
            "rewrites=Jeb:Dead->Aboard",
            "== K14 null desired rows",
            "touristSkipped=0",
            "remaps=Jeb->Neo",
            "rewrites=Bill:Aboard->Dead",
            "== K15 duplicate names",
            "touristSkipped=0",
            "remaps=",
            "rewrites=Jeb:Dead->Aboard,Jeb:Aboard->Recovered",
        }) + "\n";
    }
}
