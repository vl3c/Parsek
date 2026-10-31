using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The save-scoped session reset (design-load-restores-the-save.md 4.2, plan 1.1):
    /// <see cref="ParsekScenario.ResetSaveScopedSessionState"/> forgets every static of the
    /// seven save-scoped classes that is not named in <c>scripts/session-reset-allowlist.txt</c>.
    /// The inventory gate lists the statics by reflection, so a new static fails the suite until
    /// someone decides which side it is on; the generic cell sets every reset field, resets and
    /// reads it back; the group cells pin what the cold branch then reloads.
    /// </summary>
    [Collection("Sequential")]
    public class SaveScopedSessionResetTests : IDisposable
    {
        private static readonly Type[] InventoryTypes =
        {
            typeof(RecordingStore),
            typeof(GameStateStore),
            typeof(MilestoneStore),
            typeof(GameStateRecorder),
            typeof(LedgerOrchestrator),
            typeof(KspStatePatcher),
            typeof(ParsekScenario),
            typeof(InertRecordingTrees),
            // GC1-SAVE-SCOPED-STATE-LEFTOVERS: per-career state outside the seven stores.
            typeof(RewindContext),
            typeof(RewindInvokeContext),
            typeof(Parsek.Logistics.RouteRunPrompt),
            // Leftovers found by the save-switch audit (2026-10-31).
            typeof(FacilityStatePatcher),
            typeof(RecordingGroupStore),
        };

        private readonly List<string> logLines = new List<string>();
        private readonly string tempRoot;

        public SaveScopedSessionResetTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetAll();
            tempRoot = Path.Combine(Path.GetTempPath(), "parsek-session-reset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            ResetAll();
            ParsekLog.ResetTestOverrides();
            try { Directory.Delete(tempRoot, true); } catch { }
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ColdLoadsThisProcessForTesting = 0;
            RewindContext.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            Parsek.Logistics.RouteRunPrompt.ResetForTesting();
            FacilityStatePatcher.ResetForTesting();
            RecordingGroupStore.ResetForTesting();
        }

        // ---- the allowlist ----

        private static string AllowlistPath()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "scripts", "session-reset-allowlist.txt"));
        }

        private static Dictionary<string, string> ReadAllowlist()
        {
            string path = AllowlistPath();
            Assert.True(File.Exists(path), "allowlist not found at " + path);
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                int space = line.IndexOf(' ');
                Assert.True(space > 0, "session-reset allowlist line has no reason: '" + line + "'");
                string key = line.Substring(0, space);
                string reason = line.Substring(space + 1).Trim();
                Assert.False(string.IsNullOrEmpty(reason), "session-reset allowlist entry has no reason: " + key);
                Assert.False(entries.ContainsKey(key), "session-reset allowlist names " + key + " twice");
                entries.Add(key, reason);
            }
            return entries;
        }

        private static string FieldKey(FieldInfo f)
        {
            string name = f.Name;
            if (name.StartsWith("<", StringComparison.Ordinal) && name.Contains(">k__BackingField"))
                name = name.Substring(1, name.IndexOf('>') - 1);
            return f.DeclaringType.Name + "." + name;
        }

        private static List<FieldInfo> InventoryFields()
        {
            var fields = new List<FieldInfo>();
            foreach (Type t in InventoryTypes)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral)
                        continue;
                    fields.Add(f);
                }
            }
            return fields;
        }

        [Fact]
        public void Inventory_AllowlistNamesOnlyRealFields()
        {
            var allow = ReadAllowlist();
            var real = new HashSet<string>(InventoryFields().Select(FieldKey), StringComparer.Ordinal);
            var stale = allow.Keys.Where(k => !real.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(stale.Count == 0,
                "session-reset gate: scripts/session-reset-allowlist.txt names field(s) that no longer exist: "
                + string.Join(", ", stale));
        }

        // Every static of the inventory classes is either allowlisted or reset by the helper: this
        // cell sets each non-allowlisted one to a value the reset must undo, runs the reset and
        // reads it back. A new static fails here (or in the mutation step, naming the field)
        // until it is reset or allowlisted with a reason.
        [Fact]
        public void Inventory_EveryOtherStaticIsResetByTheHelper()
        {
            var allow = ReadAllowlist();
            var fields = InventoryFields().Where(f => !allow.ContainsKey(FieldKey(f))).ToList();
            Assert.NotEmpty(fields);

            ParsekScenario.ResetSaveScopedSessionState("test-baseline");
            var baseline = fields.ToDictionary(f => f, f => f.GetValue(null));
            var baselineCounts = fields.ToDictionary(f => f, f => CountOf(f.GetValue(null)));

            var mutated = new Dictionary<FieldInfo, object>();
            var cannotMutate = new List<string>();
            foreach (FieldInfo f in fields)
            {
                if (!TryMutate(f, baseline[f], out object mutatedValue))
                    cannotMutate.Add(FieldKey(f) + " (" + f.FieldType.Name + ")");
                else
                    mutated[f] = mutatedValue;
            }
            Assert.True(cannotMutate.Count == 0,
                "session-reset gate: the generic cell cannot set these statics; teach TryMutate the type or "
                + "allowlist the field with a reason: " + string.Join(", ", cannotMutate));

            ParsekScenario.ResetSaveScopedSessionState("test-gate");

            var notReset = new List<string>();
            foreach (FieldInfo f in fields)
            {
                object after = f.GetValue(null);
                object before = baseline[f];
                bool ok;
                if (IsCollectionValue(f.FieldType, before) || IsCollectionValue(f.FieldType, after))
                {
                    ok = (before == null) == (after == null)
                        && CountOf(after) == baselineCounts[f];
                }
                else if (f.FieldType.IsValueType || f.FieldType == typeof(string))
                {
                    ok = Equals(before, after);
                }
                else if (before == null)
                {
                    ok = after == null;
                }
                else
                {
                    ok = after != null && !ReferenceEquals(after, mutated[f]);
                }
                if (!ok)
                    notReset.Add(FieldKey(f));
            }
            Assert.True(notReset.Count == 0,
                "session-reset gate: ParsekScenario.ResetSaveScopedSessionState leaves these statics as set: "
                + string.Join(", ", notReset)
                + " (reset them in the helper, or allowlist them in scripts/session-reset-allowlist.txt with a reason)");
        }

        private static bool IsCollectionValue(Type declared, object value)
        {
            Type t = value?.GetType() ?? declared;
            return t != typeof(string) && t.GetProperty("Count") != null
                && typeof(IEnumerable).IsAssignableFrom(t);
        }

        private static int CountOf(object value)
        {
            if (value == null || value is string) return -1;
            PropertyInfo count = value.GetType().GetProperty("Count");
            if (count == null || !(value is IEnumerable)) return -1;
            return (int)count.GetValue(value, null);
        }

        private static object MakeInstance(Type t)
        {
            if (t == typeof(string)) return "mutated";
            if (t.IsValueType) return Activator.CreateInstance(t);
            if (t.IsAbstract || t.IsInterface) return null;
            return FormatterServices.GetUninitializedObject(t);
        }

        private static bool TryMutate(FieldInfo f, object current, out object mutatedValue)
        {
            mutatedValue = null;
            Type t = f.FieldType;
            if (t == typeof(bool)) { mutatedValue = !(bool)current; }
            else if (t == typeof(int)) { mutatedValue = (int)current + 7; }
            else if (t == typeof(uint)) { mutatedValue = (uint)current + 7u; }
            else if (t == typeof(double))
            {
                double d = (double)current;
                mutatedValue = double.IsNaN(d) ? 1234.5 : d + 1234.5;
            }
            else if (t == typeof(float))
            {
                float v = (float)current;
                mutatedValue = float.IsNaN(v) ? 1234.5f : v + 1234.5f;
            }
            else if (t == typeof(string)) { mutatedValue = (current as string ?? "") + "-mutated"; }
            else if (t.IsEnum)
            {
                foreach (object v in Enum.GetValues(t))
                {
                    if (!Equals(v, current)) { mutatedValue = v; break; }
                }
                if (mutatedValue == null) return false;
            }
            else if (Nullable.GetUnderlyingType(t) != null)
            {
                // A null nullable becomes its underlying default; a set one becomes null.
                mutatedValue = current == null ? Activator.CreateInstance(Nullable.GetUnderlyingType(t)) : null;
            }
            else if (t.IsValueType)
            {
                if (!TryMutateStruct(t, current, out mutatedValue))
                    return false;
            }
            else if (t.IsInterface && t.IsGenericType && t.GetGenericArguments().Length == 1)
            {
                // IReadOnlyList<T> and friends: a one-element List<T>.
                Type list = typeof(List<>).MakeGenericType(t.GetGenericArguments()[0]);
                if (!t.IsAssignableFrom(list)) return false;
                object instance = Activator.CreateInstance(list);
                if (!TryAddElement(instance)) return false;
                mutatedValue = instance;
            }
            else if (current != null && IsCollectionValue(t, current))
            {
                return TryAddElement(current);
            }
            else
            {
                Type concrete = t;
                object instance = MakeInstanceForField(concrete);
                if (instance == null) return false;
                if (IsCollectionValue(t, instance) && !TryAddElement(instance)) return false;
                mutatedValue = instance;
            }

            if (f.IsInitOnly)
                return false;
            f.SetValue(null, mutatedValue);
            return true;
        }

        // A struct with its first settable instance field changed: a number moved, a bool
        // flipped, a reference set to a fresh instance.
        private static bool TryMutateStruct(Type t, object current, out object mutatedValue)
        {
            mutatedValue = null;
            // A fresh box: mutating the baseline's own box would change the value it is compared with.
            object box = current != null
                ? System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(current)
                : Activator.CreateInstance(t);
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsInitOnly) continue;
                Type ft = field.FieldType;
                object value = field.GetValue(box);
                object next;
                if (ft == typeof(bool)) next = !(bool)value;
                else if (ft == typeof(int)) next = (int)value + 7;
                else if (ft == typeof(double)) next = double.IsNaN((double)value) ? 1234.5 : (double)value + 1234.5;
                else if (ft == typeof(float)) next = float.IsNaN((float)value) ? 1234.5f : (float)value + 1234.5f;
                else if (Nullable.GetUnderlyingType(ft) != null)
                    next = value == null ? Activator.CreateInstance(Nullable.GetUnderlyingType(ft)) : null;
                else if (!ft.IsValueType)
                {
                    if (value != null) continue;
                    next = MakeInstanceForField(ft);
                    if (next == null) continue;
                }
                else continue;
                field.SetValue(box, next);
                mutatedValue = box;
                return true;
            }
            return false;
        }

        private static object MakeInstanceForField(Type t)
        {
            if (t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(t);
            if (typeof(Delegate).IsAssignableFrom(t))
                return null;
            return MakeInstance(t);
        }

        private static bool TryAddElement(object collection)
        {
            Type ct = collection.GetType();
            if (!ct.IsGenericType) return false;
            Type[] args = ct.GetGenericArguments();
            if (args.Length == 1)
            {
                MethodInfo add = ct.GetMethod("Add", new[] { args[0] });
                object element = MakeInstance(args[0]);
                if (add == null || element == null) return false;
                add.Invoke(collection, new[] { element });
                return true;
            }
            if (args.Length == 2)
            {
                MethodInfo add = ct.GetMethod("Add", new[] { args[0], args[1] });
                object key = MakeInstance(args[0]);
                object value = args[1].IsValueType ? Activator.CreateInstance(args[1]) : MakeInstance(args[1]);
                if (add == null || key == null) return false;
                add.Invoke(collection, new[] { key, value });
                return true;
            }
            return false;
        }

        // ---- GC1-SAVE-SCOPED-STATE-LEFTOVERS ----

        // catches: a rewind whose reload never settled (EndRewind never ran) leaving
        // RewindContext.IsRewinding set, so the next Cold load of ANOTHER save skips
        // LoadCrewAndGroupState and the ledger load and carries the first career's values over.
        [Fact]
        public void StuckRewind_IsClearedByTheColdReset_WithAWarn()
        {
            RewindContext.BeginRewind(1234.0, default(BudgetSummary), 50000.0, 10.0, 5f);
            Assert.True(RewindContext.IsRewinding);
            logLines.Clear();

            ParsekScenario.ResetSaveScopedSessionState("save-folder-change");

            Assert.False(RewindContext.IsRewinding);
            Assert.Equal(0.0, RewindContext.RewindUT);
            Assert.Contains(logLines, l => l.Contains("[WARN][RewindContext]")
                && l.Contains("stale rewind state")
                && l.Contains("reason=save-folder-change"));
        }

        // catches: a Re-Fly invoke whose load never arrived (RewindInvokeContext.Pending left
        // set) dispatching the Re-Fly post-load path on the next Cold load of another save.
        [Fact]
        public void StaleReFlyInvoke_IsClearedByTheColdReset_WithAWarn()
        {
            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = "sess-stale";
            RewindInvokeContext.RewindPointId = "rp-stale";
            logLines.Clear();

            ParsekScenario.ResetSaveScopedSessionState("main-menu");

            Assert.False(RewindInvokeContext.Pending);
            Assert.Null(RewindInvokeContext.SessionId);
            Assert.Contains(logLines, l => l.Contains("[WARN][Scenario]")
                && l.Contains("stale Re-Fly invoke")
                && l.Contains("rp-stale"));
        }

        // A reset with nothing stale logs no Warn for either.
        [Fact]
        public void ColdReset_WithNothingStale_LogsNoStaleWarn()
        {
            logLines.Clear();
            ParsekScenario.ResetSaveScopedSessionState("boot");
            Assert.DoesNotContain(logLines, l => l.Contains("stale rewind state") || l.Contains("stale Re-Fly invoke"));
        }

        // ---- the reset line and the reason ----

        [Theory]
        [InlineData(0, null, "s1", "boot")]
        [InlineData(0, "s1", "s1", "boot")]
        [InlineData(1, null, "s1", "main-menu")]
        [InlineData(3, "s0", "s1", "folder-change")]
        [InlineData(2, "s1", "s1", "batch")]
        public void ColdLoadReason_NamesTheCause(int coldLoadsBefore, string before, string current, string expected)
        {
            Assert.Equal(expected, ParsekScenario.ResolveColdLoadReason(coldLoadsBefore, before, current));
        }

        [Fact]
        public void Reset_LogsOneSummaryLine_AndCountsColdLoads()
        {
            ParsekScenario.ResetSaveScopedSessionState("main-menu");

            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Save-scoped session state reset: reason=main-menu pendingTreeDropped=false storesReset=13"));
            Assert.Equal(1, ParsekScenario.ColdLoadsThisProcessForTesting);
        }

        // ---- groups ----

        // catches: a reset that clears the event / milestone stores but leaves their
        // once-per-folder guards set, so the cold branch's file read says "already loaded,
        // skipping" and the save's events and milestones are lost (data loss on a main-menu
        // Load of the same save).
        [Fact]
        public void EventsAndMilestones_GuardsReset_TheColdReadReloadsFromTheFile()
        {
            RecordingPaths.SaveRootOverrideForTesting = tempRoot;
            var saved = new GameStateEvent
            {
                ut = 100.0, eventType = GameStateEventType.TechResearched, key = "basicRocketry", detail = "",
            };
            GameStateStore.AddEvent(ref saved);
            Assert.True(GameStateStore.SaveEventFile());
            MilestoneStore.AddMilestoneForTesting(new Milestone { MilestoneId = "m1", Events = new List<GameStateEvent>() });
            Assert.True(MilestoneStore.SaveMilestoneFile());

            // Both stores are now "loaded" for this folder (the guards a main-menu Load used to keep).
            Assert.True(GameStateStore.LoadEventFile());
            Assert.True(MilestoneStore.LoadMilestoneFile());
            var stray = new GameStateEvent
            {
                ut = 200.0, eventType = GameStateEventType.TechResearched, key = "stray", detail = "",
            };
            GameStateStore.AddEvent(ref stray);
            MilestoneStore.AddMilestoneForTesting(new Milestone { MilestoneId = "stray", Events = new List<GameStateEvent>() });

            ParsekScenario.ResetSaveScopedSessionState("main-menu");
            Assert.Equal(0, GameStateStore.EventCount);
            Assert.Equal(0, MilestoneStore.MilestoneCount);

            logLines.Clear();
            Assert.True(GameStateStore.LoadEventFile());
            Assert.True(MilestoneStore.LoadMilestoneFile());

            Assert.Single(GameStateStore.Events);
            Assert.Equal("basicRocketry", GameStateStore.Events[0].key);
            Assert.Equal(1, MilestoneStore.MilestoneCount);
            Assert.DoesNotContain(logLines, l => l.Contains("already loaded, skipping"));
        }

        // catches: the reset running a discard on the pending tree. The tree stashed at a scene
        // exit is the abandoned future, not a player Discard: its sidecars stay (a save may name
        // them) and it is not marked discarded (QL-R1 does not survive a load).
        [Fact]
        public void PendingLimboTree_IsDroppedFromMemoryOnly_FilesStay_NotMarkedDiscarded()
        {
            RecordingPaths.SaveRootOverrideForTesting = tempRoot;
            const string treeId = "tree-limbo";
            const string recId = "rec-limbo";
            var rec = new Recording { RecordingId = recId, TreeId = treeId, VesselName = "Limbo" };
            var tree = new RecordingTree { Id = treeId, TreeName = "Limbo tree", RootRecordingId = recId };
            tree.AddOrReplaceRecording(rec);

            var files = new[]
            {
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(recId)),
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildVesselSnapshotRelativePath(recId)),
            };
            foreach (string file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "sidecar");
            }

            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            var science = new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 1f };
            GameStateRecorder.PendingScienceSubjects.Add(science);

            ParsekScenario.ResetSaveScopedSessionState("main-menu");

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(RecordingStore.WasTreeDiscardedThisSession(treeId));
            foreach (string file in files)
                Assert.True(File.Exists(file), "the reset deleted " + file);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l => l.Contains("pendingTreeDropped=true"));
            Assert.Contains(logLines, l => l.Contains("dropped the pending tree from memory only")
                && l.Contains("id=" + treeId));
            Assert.DoesNotContain(logLines, l => l.Contains("Discarded pending tree"));
        }

        // catches: a reset that misses the per-session discard set or the restore attempt (an
        // F9 must not carry QL-R1 or the #866 attempt into the loaded save).
        [Fact]
        public void DiscardSetAndRestoreAttempt_AreForgotten()
        {
            RecordingStore.NoteTreeDiscardedThisSession("tree-gone", "Gone", 1);
            var attemptTree = new RecordingTree { Id = "tree-attempt", TreeName = "Attempt" };
            RecordingStore.ArmCommittedTreeRestoreAttempt(attemptTree, "test");
            Assert.True(RecordingStore.WasTreeDiscardedThisSession("tree-gone"));
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);

            ParsekScenario.ResetSaveScopedSessionState("main-menu");

            Assert.False(RecordingStore.WasTreeDiscardedThisSession("tree-gone"));
            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
        }

        // catches: a reset that nulls the committed-list notification events (the batch reset
        // does): the new scene's controllers subscribe to them before the cold load runs.
        [Fact]
        public void CommittedListNotificationEvents_StaySubscribed()
        {
            int removing = 0;
            Action<int, Recording> handler = (i, r) => removing++;
            RecordingStore.CommittedRecordingRemoving += handler;
            try
            {
                ParsekScenario.ResetSaveScopedSessionState("main-menu");
                object backing = typeof(RecordingStore)
                    .GetField("CommittedRecordingRemoving", BindingFlags.NonPublic | BindingFlags.Static)
                    .GetValue(null);
                Assert.NotNull(backing);
            }
            finally
            {
                RecordingStore.CommittedRecordingRemoving -= handler;
            }
        }

        // catches: the KSC sequence counter restarting at zero after a load, so a KSC row made
        // after the load sorts BEFORE a loaded row at the same UT (plan 1.1 VERIFY).
        [Fact]
        public void KscSequenceCounter_SeededFromTheLoadedLedger_LaterKscRowSortsAfter()
        {
            var loadedRow = new GameAction { UT = 500.0, Type = GameActionType.FundsSpending, Sequence = 41 };
            Ledger.AddAction(loadedRow);
            ConfigNode ledgerNode = Ledger.BuildSaveNode();

            ParsekScenario.ResetSaveScopedSessionState("main-menu");
            Assert.Equal(0, LedgerOrchestrator.KscSequenceCounterForTesting);

            LedgerOrchestrator.OnLoad(ledgerNode);
            Assert.Equal(41, LedgerOrchestrator.KscSequenceCounterForTesting);

            int next = (int)typeof(LedgerOrchestrator)
                .GetMethod("AllocateKscSequence", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            Assert.Equal(42, next);
            var kscRow = new GameAction { UT = 500.0, Type = GameActionType.FundsSpending, Sequence = next };
            // The ledger's write door stamps the capture sequence, the walk's same-UT tie key
            // (design 3.15 step 2); an unstamped row would sort first as an older save's row.
            CaptureSequence.StampIfMissing(kscRow);
            Assert.True(kscRow.CaptureSequence > Ledger.Actions[0].CaptureSequence);
            var sorted = RecalculationEngine.SortActions(new List<GameAction> { kscRow, Ledger.Actions[0] });
            Assert.Equal(41, sorted[0].Sequence);
            Assert.Same(kscRow, sorted[1]);
        }
    }
}
