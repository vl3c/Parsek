using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// Golden characterization of <see cref="BackgroundRecorder.HandleBackgroundVesselSplit"/>,
    /// recorded against the method before its blocks were extracted (wave 2, W9b-3).
    ///
    /// Headless reach: the method's guards (no tree, a missing parent recording, a destroyed
    /// parent) and the first live read, <c>CollectNewBackgroundSplitVessels</c>, whose
    /// <c>FlightGlobals.Vessels</c> read fails on mono (FlightGlobals' static initializer calls
    /// native Unity), so a case past the guards records that exception type and the untouched
    /// tree. Everything after the vessel scan needs a live split; the extracted units carry their
    /// own direct tests.
    /// </summary>
    [Collection("Sequential")]
    public class HandleBackgroundVesselSplitCharacterizationTests : IDisposable
    {
        private const uint ParentPid = 4301u;
        private const string ParentRecId = "bg-split-parent";

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public HandleBackgroundVesselSplitCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            // Several log lines format numbers with the thread culture; the golden is
            // recorded under the invariant one so a ro-RO host reads the same text.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            // ParsekSettings' static constructor logs once per AppDomain; run it before the
            // sink is installed so the golden does not depend on which test touched it first.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParsekSettings).TypeHandle);
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private sealed class Fixture
        {
            public RecordingTree Tree;
            public BackgroundRecorder Bg;
            public Recording Parent;
        }

        private static Fixture Build(bool withParent = true, bool destroyed = false, int generation = 0,
            bool debris = false)
        {
            var tree = new RecordingTree { Id = "bg-split-tree" };
            Recording parent = null;
            if (withParent)
            {
                parent = new Recording
                {
                    RecordingId = ParentRecId,
                    TreeId = tree.Id,
                    VesselName = "Split Parent",
                    VesselPersistentId = ParentPid,
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 140.0,
                    Generation = generation,
                    IsDebris = debris,
                };
                parent.Points.Add(new TrajectoryPoint { ut = 120.0, altitude = 1000.0, bodyName = "Kerbin" });
                if (destroyed)
                    parent.MarkDestroyedAtTerminal(140.0, "characterization");
                tree.Recordings[ParentRecId] = parent;
                tree.BackgroundMap[ParentPid] = ParentRecId;
            }
            var bg = new BackgroundRecorder(tree);
            bg.InjectLoadedStateForTesting(ParentPid, ParentRecId);
            return new Fixture { Tree = tree, Bg = bg, Parent = parent };
        }

        private void Split(StringBuilder sb, string label, Fixture f, double branchUT,
            HashSet<uint> preBreakPids, TrajectoryPoint? boundary = null, string cause = "DECOUPLE")
        {
            logLines.Clear();
            string threw = "none";
            try
            {
                f.Bg.HandleBackgroundVesselSplit(ParentPid, branchUT, ParentRecId, preBreakPids, boundary, cause);
            }
            catch (Exception e)
            {
                threw = e.GetType().Name;
            }
            sb.Append("-- split ").Append(label).Append(" branchUT=").Append(D(branchUT))
                .Append(" threw=").Append(threw).Append('\n');
            foreach (string l in logLines)
                sb.Append("  log ").Append(l).Append('\n');
            sb.Append("  tree branchPoints=").Append(f.Tree.BranchPoints.Count)
                .Append(" recordings=").Append(string.Join(",", f.Tree.Recordings.Keys.OrderBy(k => k, StringComparer.Ordinal)))
                .Append(" backgroundMap=").Append(string.Join(",", f.Tree.BackgroundMap
                    .OrderBy(kv => kv.Key).Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture) + "=" + kv.Value)))
                .Append('\n');
            if (f.Parent != null)
                sb.Append("  parent childBp=").Append(f.Parent.ChildBranchPointId ?? "null")
                    .Append(" destroyed=").Append(f.Parent.VesselDestroyed).Append('\n');
            DumpRecorder(sb, f.Bg, ParentPid);
            DumpRecording(sb, f.Parent);
        }

        private string Run(string name, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.Append("=== ").Append(name).Append('\n');
            ParsekLog.ResetRateLimitsForTesting();
            body(sb);
            return sb.ToString();
        }

        private IEnumerable<string> Cases()
        {
            yield return Run("S01 null tree", sb =>
            {
                Fixture f = Build();
                typeof(BackgroundRecorder).GetField("tree", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(f.Bg, null);
                Split(sb, "a", f, 150.0, new HashSet<uint>());
            });
            yield return Run("S02 parent recording missing", sb =>
            {
                Fixture f = Build(withParent: false);
                Split(sb, "a", f, 150.0, new HashSet<uint>());
            });
            yield return Run("S03 destroyed parent", sb =>
            {
                Fixture f = Build(destroyed: true);
                Split(sb, "a", f, 150.0, new HashSet<uint> { ParentPid });
                Split(sb, "b-rate-limited", f, 151.0, null);
            });
            yield return Run("S04 live parent reaches the vessel scan", sb =>
            {
                Fixture f = Build();
                Split(sb, "a", f, 150.0, new HashSet<uint> { ParentPid },
                    new TrajectoryPoint { ut = 150.0, altitude = 1100.0, bodyName = "Kerbin" });
            });
            yield return Run("S05 parent at the cascade cap reaches the vessel scan first", sb =>
            {
                Fixture f = Build(generation: 1, debris: true);
                Split(sb, "a", f, 150.0, null, cause: null);
            });
        }

        [UnityEntryFact]
        public void HandleBackgroundVesselSplit_Golden()
        {
            var sb = new StringBuilder();
            foreach (string c in Cases())
                sb.Append(c);
            AssertGolden("handle-background-vessel-split.golden.txt", sb.ToString());
        }
    }
}
