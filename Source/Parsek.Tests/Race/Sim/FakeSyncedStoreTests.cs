using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Xunit;

namespace Parsek.Tests.Race.Sim
{
    /// <summary>
    /// The fake synced store, its schedule and its clock: each control on its own, then a
    /// seeded workload that must give the same events and the same files for the same seed,
    /// and replay from its recorded draws alone. No shared static state, no file system.
    /// </summary>
    public class FakeSyncedStoreTests
    {
        private const string Payload = "players/p_a/payload/r1.0123456789abcdef.prec";
        private const string Standing1 = "players/p_a/standings/000001.pstd";
        private const string Standing2 = "players/p_a/standings/000002.pstd";

        private static byte[] B(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static string S(byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        private static FakeSyncedStore Store(long seed = 1, FakeSyncPolicy a = null, FakeSyncPolicy b = null)
        {
            var store = new FakeSyncedStore(SimSchedule.FromSeed(seed));
            store.AddMachine("A", a);
            store.AddMachine("B", b);
            return store;
        }

        private static string Read(FakeSyncedStore store, string machine, string path)
        {
            byte[] content;
            string reason;
            Assert.True(store.TryOpen(machine, path, out content, out reason),
                machine + " cannot open " + path + ": " + reason + "\n" + store.Schedule.Describe());
            return S(content);
        }

        private static List<string> PathsOf(FakeSyncedStore store, SimEventKind kind, string machine)
        {
            var paths = new List<string>();
            foreach (SimEvent e in store.Schedule.Events)
            {
                if (e.Kind == kind && e.Machine == machine)
                    paths.Add(e.Path);
            }
            return paths;
        }

        // ---- the basic contract ----------------------------------------------------------

        [Fact]
        public void Write_LandsInWritersViewAtOnce_OthersOnlyAfterDelivery()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));

            Assert.Equal("one", Read(store, "A", Standing1));
            Assert.False(store.Exists("B", Standing1));
            Assert.Empty(store.CloudList(""));
            Assert.Equal(1, store.PendingCount);

            store.DeliverDue();

            Assert.Equal(new[] { Standing1 }, store.CloudList(""));
            Assert.Equal("one", Read(store, "B", Standing1));
            Assert.Equal(0, store.PendingCount);
        }

        [Fact]
        public void Deliveries_WithNoControl_ArriveInWriteOrder()
        {
            var store = Store();
            store.Write("A", Payload, B("payload"));
            store.Write("A", Standing1, B("standing"));
            store.DeliverDue();

            Assert.Equal(new List<string> { Payload, Standing1 }, PathsOf(store, SimEventKind.Downloaded, "B"));
        }

        [Fact]
        public void TryOpen_ReturnsACopy_AndMissingFileSaysSo()
        {
            var store = Store();
            store.Write("A", Standing1, B("abc"));
            byte[] first;
            string reason;
            Assert.True(store.TryOpen("A", Standing1, out first, out reason));
            first[0] = (byte)'z';
            Assert.Equal("abc", Read(store, "A", Standing1));

            Assert.False(store.TryOpen("A", "players/p_a/none.pstd", out first, out reason));
            Assert.Equal("missing", reason);
        }

        [Fact]
        public void OwnSuccessiveWrites_NeverConflict()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));
            store.Write("A", Standing1, B("two"));
            store.DeliverDue();
            store.Write("A", Standing1, B("three"));
            store.DeliverDue();

            Assert.Equal(0, store.Schedule.Count(SimEventKind.ConflictCopy));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.Coalesced));
            Assert.Equal(new[] { Standing1 }, store.CloudList(""));
            Assert.Equal("three", Read(store, "B", Standing1));
        }

        // ---- delay -----------------------------------------------------------------------

        [Fact]
        public void Delay_HoldsADeliveryUntilItsTick()
        {
            var store = Store(a: new FakeSyncPolicy { MinDelayTicks = 3, MaxDelayTicks = 3 });
            store.Write("A", Standing1, B("one"));

            store.Advance(2);
            Assert.Empty(store.CloudList(""));
            Assert.False(store.Exists("B", Standing1));

            store.Advance(1);
            Assert.Equal("one", Read(store, "B", Standing1));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.Delayed));
        }

        // ---- reorder ---------------------------------------------------------------------

        [Fact]
        public void Reorder_LetsALaterWriteArriveFirst()
        {
            var store = Store(a: new FakeSyncPolicy { ReorderPerMille = 1000 });
            store.Write("A", Payload, B("payload"));
            store.Write("A", Standing1, B("standing"));
            store.DeliverDue();

            Assert.Equal(new List<string> { Standing1, Payload }, PathsOf(store, SimEventKind.Downloaded, "B"));
            Assert.True(store.Schedule.Count(SimEventKind.Reordered) >= 1);
        }

        [Fact]
        public void Reorder_OfOnePath_NeverTakesAViewBackToAnOlderVersion()
        {
            var store = Store(b: new FakeSyncPolicy { MinDelayTicks = 2, MaxDelayTicks = 2, ReorderPerMille = 1000 });
            store.Write("A", Standing1, B("one"));
            store.DeliverDue();
            store.Write("A", Standing1, B("two"));
            store.DeliverDue();

            store.Advance(2);

            Assert.Equal("two", Read(store, "B", Standing1));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.SkippedStale));
        }

        // ---- drop until reconnect --------------------------------------------------------

        [Fact]
        public void Offline_DropsDownloadsAndBringsTheMachineToTheLatestOnReconnect()
        {
            var store = Store();
            store.SetOnline("B", false);
            store.Write("A", Standing1, B("one"));
            store.RunUntilQuiet();

            Assert.False(store.Exists("B", Standing1));
            Assert.True(store.Schedule.Count(SimEventKind.DroppedOffline) >= 1);

            store.Write("A", Standing1, B("two"));
            store.RunUntilQuiet();
            store.SetOnline("B", true);
            store.RunUntilQuiet();

            Assert.Equal("two", Read(store, "B", Standing1));
            Assert.Equal(new List<string> { Standing1 }, PathsOf(store, SimEventKind.Downloaded, "B"));
        }

        [Fact]
        public void Offline_DropsQueuedDownloadsToo()
        {
            var store = Store(b: new FakeSyncPolicy { MinDelayTicks = 5, MaxDelayTicks = 5 });
            store.Write("A", Standing1, B("one"));
            store.DeliverDue();
            Assert.Equal(1, store.PendingCount);

            store.SetOnline("B", false);

            Assert.Equal(0, store.PendingCount);
            Assert.Equal(1, store.Schedule.Count(SimEventKind.DroppedOffline));
        }

        [Fact]
        public void Offline_UploadsWaitForReconnect()
        {
            var store = Store();
            store.SetOnline("B", false);
            store.Write("B", "players/p_b/standings/000001.pstd", B("b1"));
            store.RunUntilQuiet();

            Assert.Empty(store.CloudList(""));
            Assert.False(store.Exists("A", "players/p_b/standings/000001.pstd"));

            store.SetOnline("B", true);
            store.RunUntilQuiet();

            Assert.Equal("b1", Read(store, "A", "players/p_b/standings/000001.pstd"));
        }

        // ---- partial content -------------------------------------------------------------

        [Fact]
        public void Partial_ShowsATruncatedFileBeforeTheRestArrives()
        {
            var store = Store(b: new FakeSyncPolicy { MinDelayTicks = 1, MaxDelayTicks = 1, PartialPerMille = 1000 });
            store.Write("A", Payload, B("0123456789"));
            store.DeliverDue();

            store.Advance(1);
            string seen = Read(store, "B", Payload);
            Assert.InRange(seen.Length, 1, 9);
            Assert.StartsWith(seen, "0123456789", StringComparison.Ordinal);
            Assert.Equal(seen.Length, store.List("B", "players/p_a/payload")[0].Length);

            store.Advance(1);
            Assert.Equal("0123456789", Read(store, "B", Payload));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.PartialLanded));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.RestLanded));
        }

        [Fact]
        public void Partial_WhoseRestWasDroppedOffline_CompletesOnReconnect()
        {
            var store = Store(b: new FakeSyncPolicy { MinDelayTicks = 1, MaxDelayTicks = 1, PartialPerMille = 1000 });
            store.Write("A", Payload, B("0123456789"));
            store.DeliverDue();
            store.Advance(1);
            Assert.NotEqual("0123456789", Read(store, "B", Payload));

            store.SetOnline("B", false);
            store.SetOnline("B", true);
            store.RunUntilQuiet();

            Assert.Equal("0123456789", Read(store, "B", Payload));
        }

        // ---- placeholder files -----------------------------------------------------------

        [Fact]
        public void Placeholder_ShowsTheNameUntilOpened_AndTheOpenDeliversTheContent()
        {
            var store = Store(b: new FakeSyncPolicy { PlaceholderPerMille = 1000 });
            store.Write("A", Payload, B("0123456789"));
            store.RunUntilQuiet();

            IReadOnlyList<SyncedFileInfo> listed = store.List("B", "players/p_a");
            Assert.Single(listed);
            Assert.True(listed[0].IsPlaceholder);
            Assert.Equal(10, listed[0].Length);

            store.SetOnline("B", false);
            byte[] content;
            string reason;
            Assert.False(store.TryOpen("B", Payload, out content, out reason));
            Assert.Equal("placeholder-offline", reason);

            store.SetOnline("B", true);
            Assert.Equal("0123456789", Read(store, "B", Payload));
            Assert.False(store.List("B", "")[0].IsPlaceholder);
            Assert.Equal(1, store.Schedule.Count(SimEventKind.PlaceholderLanded));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.PlaceholderOpened));
        }

        // ---- conflict copies -------------------------------------------------------------

        [Fact]
        public void TwoWritersOfOnePath_TheLaterBecomesAConflictCopyEverywhere()
        {
            var store = Store();
            store.Write("A", "race.cfg", B("from A"));
            store.Write("B", "race.cfg", B("from B"));
            store.RunUntilQuiet();

            string copy = "race (conflict B 1).cfg";
            Assert.Equal(new[] { copy, "race.cfg" }, store.CloudList(""));
            foreach (string m in new[] { "A", "B" })
            {
                Assert.Equal("from A", Read(store, m, "race.cfg"));
                Assert.Equal("from B", Read(store, m, copy));
            }
            Assert.Equal(1, store.Schedule.Count(SimEventKind.ConflictCopy));
        }

        [Fact]
        public void ConflictCopies_OfOnePath_CountUp()
        {
            var store = Store();
            store.AddMachine("C");
            store.Write("A", Standing1, B("a"));
            store.Write("B", Standing1, B("b"));
            store.Write("C", Standing1, B("c"));
            store.RunUntilQuiet();

            Assert.Equal(new[]
            {
                "players/p_a/standings/000001 (conflict B 1).pstd",
                "players/p_a/standings/000001 (conflict C 2).pstd",
                Standing1,
            }, store.CloudList("players/p_a/standings"));
        }

        [Theory]
        [InlineData("players/p_a/standings/000006.pstd", "B", 1, "players/p_a/standings/000006 (conflict B 1).pstd")]
        [InlineData("players/p_a/payload/r1.0123456789abcdef_ghost.craft", "m-2", 3, "players/p_a/payload/r1.0123456789abcdef_ghost (conflict m-2 3).craft")]
        [InlineData("README", "A", 1, "README (conflict A 1)")]
        [InlineData(".hidden", "A", 1, ".hidden (conflict A 1)")]
        [InlineData("players\\p_a\\player.cfg", "A", 2, "players/p_a/player (conflict A 2).cfg")]
        public void ConflictCopyName_Shape(string path, string machine, int n, string expected)
        {
            Assert.Equal(expected, FakeSyncedStore.ConflictCopyName(path, machine, n));
        }

        // ---- foreign edits and renames ---------------------------------------------------

        [Fact]
        public void ForeignEdit_ReachesEveryMachine()
        {
            var store = Store();
            store.Write("A", Standing1, B("mine"));
            store.RunUntilQuiet();

            store.ForeignEdit(Standing1, B("tampered"));
            store.RunUntilQuiet();

            Assert.Equal("tampered", Read(store, "A", Standing1));
            Assert.Equal("tampered", Read(store, "B", Standing1));
            Assert.Equal(1, store.Schedule.Count(SimEventKind.ForeignEdit));
        }

        [Fact]
        public void ForeignEdit_OverAnUnsentLocalChange_KeepsBoth()
        {
            var store = Store(a: new FakeSyncPolicy { ReorderPerMille = 1000 });
            store.Write("A", Standing1, B("mine"));
            store.ForeignEdit(Standing1, B("tampered"));
            store.RunUntilQuiet();

            Assert.Equal("tampered", Read(store, "A", Standing1));
            Assert.Equal("mine", Read(store, "B", "players/p_a/standings/000001 (conflict A 1).pstd"));
            Assert.True(store.Schedule.Count(SimEventKind.SkippedDirty) >= 1);
        }

        [Fact]
        public void ForeignRename_MovesTheFileOnEveryMachine()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));
            store.RunUntilQuiet();

            Assert.True(store.ForeignRename(Standing1, Standing2));
            store.RunUntilQuiet();

            foreach (string m in new[] { "A", "B" })
            {
                Assert.False(store.Exists(m, Standing1));
                Assert.Equal("one", Read(store, m, Standing2));
            }
            Assert.False(store.ForeignRename(Standing1, Standing2));
        }

        // ---- deletes ---------------------------------------------------------------------

        [Fact]
        public void Delete_RemovesTheFileEverywhere()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));
            store.RunUntilQuiet();

            Assert.True(store.Delete("A", Standing1));
            Assert.False(store.Exists("A", Standing1));
            store.RunUntilQuiet();

            Assert.False(store.Exists("B", Standing1));
            Assert.Empty(store.CloudList(""));
            Assert.False(store.Delete("A", Standing1));
        }

        [Fact]
        public void ForeignDelete_RemovesTheFileEverywhere()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));
            store.RunUntilQuiet();

            Assert.True(store.ForeignDelete(Standing1));
            store.RunUntilQuiet();

            Assert.False(store.Exists("A", Standing1));
            Assert.False(store.Exists("B", Standing1));
        }

        [Fact]
        public void Delete_RacingAnEdit_TheEditSurvivesAsAConflictCopy()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));
            store.RunUntilQuiet();

            store.Delete("A", Standing1);
            store.Write("B", Standing1, B("edited"));
            store.RunUntilQuiet();

            string copy = "players/p_a/standings/000001 (conflict B 1).pstd";
            Assert.Equal(new[] { copy }, store.CloudList(""));
            Assert.False(store.Exists("B", Standing1));
            Assert.Equal("edited", Read(store, "A", copy));
        }

        [Fact]
        public void Delete_OfAFileEditedMeanwhile_IsLostAndTheFileComesBack()
        {
            var store = Store();
            store.Write("A", Standing1, B("one"));
            store.RunUntilQuiet();

            store.Write("B", Standing1, B("edited"));
            store.Delete("A", Standing1);
            store.RunUntilQuiet();

            Assert.Equal(1, store.Schedule.Count(SimEventKind.DeleteLost));
            Assert.Equal("edited", Read(store, "A", Standing1));
            Assert.Equal("edited", Read(store, "B", Standing1));
        }

        [Fact]
        public void Rename_OnAMachine_IsADeleteAndAWriteForTheOthers()
        {
            var store = Store();
            store.Write("A", "players/p_a/player.cfg.tmp", B("id"));
            Assert.True(store.Rename("A", "players/p_a/player.cfg.tmp", "players/p_a/player.cfg"));
            store.RunUntilQuiet();

            Assert.Equal(new[] { "players/p_a/player.cfg" }, store.CloudList("players/p_a"));
            Assert.Equal("id", Read(store, "B", "players/p_a/player.cfg"));
            Assert.False(store.Exists("B", "players/p_a/player.cfg.tmp"));
        }

        // ---- paths -----------------------------------------------------------------------

        [Fact]
        public void Paths_AreOneCanonicalForm_AndListsAreOrdinal()
        {
            var store = Store();
            store.Write("A", "players\\p_a\\b.cfg", B("1"));
            store.Write("A", "players/p_a/B.cfg", B("2"));
            store.Write("A", "players/p_a/a.cfg", B("3"));
            store.Write("A", "players/p_ab/x.cfg", B("4"));

            Assert.True(store.Exists("A", "players/p_a/b.cfg"));
            var listed = new List<string>();
            foreach (SyncedFileInfo f in store.List("A", "players/p_a/"))
                listed.Add(f.Path);
            Assert.Equal(new List<string> { "players/p_a/B.cfg", "players/p_a/a.cfg", "players/p_a/b.cfg" }, listed);
        }

        [Theory]
        [InlineData("")]
        [InlineData("/players/x")]
        [InlineData("C:/x")]
        [InlineData("players//x")]
        [InlineData("players/../x")]
        [InlineData("players/./x")]
        [InlineData("players/x/")]
        public void NormalizePath_RefusesNonCanonicalPaths(string path)
        {
            Assert.Throws<ArgumentException>(() => FakeSyncedStore.NormalizePath(path));
        }

        [Fact]
        public void MachineIds_AreValidated()
        {
            var store = Store();
            Assert.Throws<ArgumentException>(() => store.AddMachine("A"));
            Assert.Throws<ArgumentException>(() => store.AddMachine(FakeSyncedStore.ForeignActor));
            Assert.Throws<ArgumentException>(() => store.AddMachine("a b"));
            Assert.Throws<ArgumentException>(() => store.Write("Z", Standing1, B("x")));
        }

        // ---- the seeded workload: determinism, replay, every control fires ---------------

        private static FakeSyncedStore RunWorkload(SimSchedule schedule)
        {
            var store = new FakeSyncedStore(schedule);
            store.AddMachine("A", new FakeSyncPolicy { MaxDelayTicks = 3, ReorderPerMille = 300 });
            store.AddMachine("B", new FakeSyncPolicy { MaxDelayTicks = 2, ReorderPerMille = 300, PartialPerMille = 500 });
            store.AddMachine("C", new FakeSyncPolicy { MaxDelayTicks = 3, PlaceholderPerMille = 500, PartialPerMille = 300 });

            store.Write("A", Payload, B("trajectory bytes of r1"));
            store.Write("A", Standing1, B("standing 1 names r1"));
            store.Advance(1);
            store.Write("B", "players/p_b/standings/000001.pstd", B("b standing 1"));
            store.SetOnline("C", false);
            store.Write("A", Standing2, B("standing 2 names r1"));
            store.Write("C", "players/p_c/player.cfg", B("c identity"));
            store.Advance(2);
            store.Write("A", "race.cfg", B("race from A"));
            store.Write("B", "race.cfg", B("race from B"));
            store.ForeignEdit(Standing1, B("standing 1 edited by hand"));
            store.SetOnline("C", true);
            store.Advance(1);
            store.ForeignRename("players/p_b/standings/000001.pstd", "players/p_b/standings/000001 (1).pstd");
            store.Delete("A", Payload);
            store.RunUntilQuiet();
            foreach (SyncedFileInfo f in store.List("C", ""))
            {
                if (f.IsPlaceholder)
                    Read(store, "C", f.Path);
            }
            store.Write("B", "players/p_b/standings/000002.pstd", B("b standing 2, a little longer"));
            store.RunUntilQuiet();
            return store;
        }

        private static string Dump(FakeSyncedStore store)
        {
            var sb = new StringBuilder();
            foreach (string path in store.CloudList(""))
            {
                byte[] content;
                store.TryReadCloud(path, out content);
                sb.Append("cloud ").Append(path).Append(" = ").Append(S(content)).Append('\n');
            }
            foreach (string m in store.Machines)
            {
                foreach (SyncedFileInfo f in store.List(m, ""))
                {
                    sb.Append(m).Append(' ').Append(f.Path)
                        .Append(" len=").Append(f.Length.ToString(CultureInfo.InvariantCulture))
                        .Append(f.IsPlaceholder ? " placeholder" : "");
                    if (!f.IsPlaceholder)
                        sb.Append(" = ").Append(Read(store, m, f.Path));
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        [Fact]
        public void Workload_SameSeed_SameEventsAndSameFiles()
        {
            for (long seed = 1; seed <= 20; seed++)
            {
                FakeSyncedStore first = RunWorkload(SimSchedule.FromSeed(seed));
                FakeSyncedStore second = RunWorkload(SimSchedule.FromSeed(seed));
                Assert.Equal(first.Schedule.ToText(), second.Schedule.ToText());
                Assert.Equal(Dump(first), Dump(second));
            }
        }

        [Fact]
        public void Workload_DifferentSeeds_GiveDifferentSchedules()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (long seed = 1; seed <= 20; seed++)
                seen.Add(RunWorkload(SimSchedule.FromSeed(seed)).Schedule.ToText());
            Assert.True(seen.Count >= 15, "only " + seen.Count + " distinct schedules over 20 seeds");
        }

        [Fact]
        public void Workload_DoesNotDependOnTheThreadCulture()
        {
            string invariant = null;
            string dumpInvariant = null;
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                FakeSyncedStore a = RunWorkload(SimSchedule.FromSeed(7));
                invariant = a.Schedule.ToText();
                dumpInvariant = Dump(a);
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                FakeSyncedStore b = RunWorkload(SimSchedule.FromSeed(7));
                Assert.Equal(invariant, b.Schedule.ToText());
                Assert.Equal(dumpInvariant, Dump(b));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Workload_ReplayedFromItsRecordedDraws_EqualsTheOriginal()
        {
            for (long seed = 1; seed <= 20; seed++)
            {
                FakeSyncedStore original = RunWorkload(SimSchedule.FromSeed(seed));
                SimSchedule replay = SimSchedule.Replay(original.Schedule);
                FakeSyncedStore again = RunWorkload(replay);

                Assert.True(replay.IsReplay);
                Assert.Equal(0, replay.ReplayRemaining);
                Assert.Equal(original.Schedule.Draws.Count, replay.Draws.Count);
                Assert.Equal(original.Schedule.ToText(), replay.ToText());
                Assert.Equal(Dump(original), Dump(again));
            }
        }

        [Fact]
        public void Workload_OverTheSeedSet_FiresEveryControl()
        {
            var counts = new Dictionary<SimEventKind, int>();
            foreach (SimEventKind kind in SimSchedule.ControlKinds)
                counts[kind] = 0;
            for (long seed = 1; seed <= 20; seed++)
            {
                SimSchedule s = RunWorkload(SimSchedule.FromSeed(seed)).Schedule;
                foreach (SimEventKind kind in SimSchedule.ControlKinds)
                    counts[kind] += s.Count(kind);
            }
            foreach (SimEventKind kind in SimSchedule.ControlKinds)
                Assert.True(counts[kind] > 0, kind + " never fired over seeds 1..20");
        }

        [Fact]
        public void Replay_ThatAsksForADifferentDraw_Throws()
        {
            var original = new FakeSyncedStore(SimSchedule.FromSeed(5));
            original.AddMachine("A", new FakeSyncPolicy { MaxDelayTicks = 3 });
            original.Write("A", Standing1, B("x"));

            var replayed = new FakeSyncedStore(SimSchedule.Replay(original.Schedule));
            replayed.AddMachine("A", new FakeSyncPolicy { MaxDelayTicks = 5 });
            var ex = Assert.Throws<InvalidOperationException>(() => replayed.Write("A", Standing1, B("x")));
            Assert.Contains("replay diverged at draw 0", ex.Message);
            Assert.Contains("Delay/6", ex.Message);

            var longer = new FakeSyncedStore(SimSchedule.Replay(original.Schedule));
            longer.AddMachine("A", new FakeSyncPolicy { MaxDelayTicks = 3 });
            longer.Write("A", Standing1, B("x"));
            ex = Assert.Throws<InvalidOperationException>(() => longer.Write("A", Standing2, B("y")));
            Assert.Contains("ran out", ex.Message);
        }

        [Fact]
        public void Generator_IsPinned_SoEveryHostDrawsTheSameSequence()
        {
            SimSchedule s = SimSchedule.FromSeed(42);
            var values = new int[5];
            for (int i = 0; i < values.Length; i++)
                values[i] = s.Next(SimDrawPurpose.Delay, 1000);
            Assert.Equal(new[] { 413, 291, 858, 764, 250 }, values);
        }

        [Fact]
        public void Chance_AtZeroAndAtOneThousand_DrawsNothing()
        {
            SimSchedule s = SimSchedule.FromSeed(1);
            Assert.False(s.Chance(SimDrawPurpose.Partial, 0));
            Assert.True(s.Chance(SimDrawPurpose.Partial, 1000));
            Assert.Equal(3, s.Between(SimDrawPurpose.Delay, 3, 3));
            Assert.Empty(s.Draws);
        }

        // ---- the clock -------------------------------------------------------------------

        [Fact]
        public void FakeClock_RealTimeAndUT_MoveIndependently_AndTwoClocksSkew()
        {
            var start = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            var ana = new FakeClock(start, 100.0);
            var bogdan = new FakeClock(start.AddMinutes(-7), 100.0);

            ana.AdvanceReal(TimeSpan.FromSeconds(30));
            Assert.Equal(start.AddSeconds(30), ana.UtcNow);
            Assert.Equal(100.0, ana.UT);

            ana.AdvanceUT(3600.5);
            Assert.Equal(3700.5, ana.UT);
            Assert.Equal(start.AddSeconds(30), ana.UtcNow);

            Assert.Equal(TimeSpan.FromMinutes(7).Add(TimeSpan.FromSeconds(30)), ana.UtcNow - bogdan.UtcNow);
            Assert.Equal(DateTimeKind.Utc, bogdan.UtcNow.Kind);

            bogdan.AdvanceReal(TimeSpan.FromHours(-1));
            Assert.Equal(start.AddMinutes(-67), bogdan.UtcNow);
            bogdan.SetUT(5.0);
            Assert.Equal(5.0, bogdan.UT);
        }

        [Fact]
        public void FakeClock_RefusesNonUtcTimesAndNonFiniteUT()
        {
            Assert.Throws<ArgumentException>(() => new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
            Assert.Throws<ArgumentException>(() => new FakeClock(new DateTime(2026, 1, 1)));
            var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetUT(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceUT(double.PositiveInfinity));
        }
    }
}
