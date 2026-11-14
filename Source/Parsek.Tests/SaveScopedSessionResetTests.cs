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
    /// <see cref="InventoryTypes"/> that is not named in <c>scripts/session-reset-allowlist.txt</c>,
    /// and every other store <c>scripts/arch/lifetimes.toml</c> tags save or load-transition is
    /// reset by its own Cold-path call (<see cref="ColdLoaderCells"/>). The inventory gate lists the
    /// statics by reflection, so a new static fails the suite until someone decides which side it
    /// is on; the generic cells set every reset field, reset and read it back (through a store's
    /// <c>ColdResetState</c> holder); the group cells pin what the cold branch then reloads.
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
            // RecordingStore services (ARCH-RECORDINGSTORE-GOD-OBJECT): statics moved off the store.
            typeof(RecordingSidecarStore),
            typeof(RecordingFileJanitor),
            typeof(SceneEntryVesselScope),
            typeof(CommittedTreeRestoreAttempt),
            typeof(PendingTreeSlot),
            typeof(TreeDiscardService),
            typeof(RewindCarry),
            typeof(RewindLauncher),
            // WARP-REQUEST-SURVIVES-A-FAILED-REWIND: a Warp to time request belongs to the game
            // it was armed in; no Warp to time path crosses a Cold load between arm and consume.
            typeof(WarpToTimeRequest),
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
            WarpToTimeRequest.ResetForTesting();
            ResetColdLoaderStores();
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
                fields.AddRange(StaticFieldsOf(t));
            return fields;
        }

        private static IEnumerable<FieldInfo> StaticFieldsOf(Type t)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral)
                    continue;
                yield return f;
            }
        }

        // ---- slots: a static, or a field of a save-scoped holder ----

        // A save-scoped holder is a static whose type is a class named ColdResetState nested in
        // the store that declares it: the store's save-scoped fields live in one holder object
        // that the cold reset replaces. The cells read through it: each instance field of the
        // CURRENT holder is a slot keyed Store.field, so a field moved into the holder keeps its
        // key, and a field added to the holder is covered without a test edit.
        internal const string HolderTypeName = "ColdResetState";

        private sealed class Slot
        {
            internal string Key;
            internal FieldInfo Field;
            internal FieldInfo Holder;   // null for a plain static

            internal object Target => Holder == null ? null : Holder.GetValue(null);
            internal object Read() => Field.GetValue(Target);
        }

        internal static bool IsSaveScopedHolder(FieldInfo f)
        {
            Type t = f.FieldType;
            return f.IsStatic && t.IsClass && t.DeclaringType == f.DeclaringType
                && t.Name == HolderTypeName;
        }

        private static List<Slot> SlotsOf(IEnumerable<FieldInfo> statics)
        {
            var slots = new List<Slot>();
            foreach (FieldInfo f in statics)
            {
                if (!IsSaveScopedHolder(f))
                {
                    slots.Add(new Slot { Key = FieldKey(f), Field = f });
                    continue;
                }
                foreach (FieldInfo inner in f.FieldType.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    slots.Add(new Slot
                    {
                        Key = f.DeclaringType.Name + "." + DisplayName(inner),
                        Field = inner,
                        Holder = f,
                    });
                }
            }
            return slots;
        }

        private static string DisplayName(FieldInfo f)
        {
            string name = f.Name;
            if (name.StartsWith("<", StringComparison.Ordinal) && name.Contains(">k__BackingField"))
                name = name.Substring(1, name.IndexOf('>') - 1);
            return name;
        }

        [Fact]
        public void Inventory_AllowlistNamesOnlyRealFields()
        {
            var allow = ReadAllowlist();
            var real = new HashSet<string>(SlotsOf(InventoryFields()).Select(sl => sl.Key)
                .Concat(InventoryFields().Select(FieldKey)), StringComparer.Ordinal);
            var stale = allow.Keys.Where(k => !real.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(stale.Count == 0,
                "session-reset gate: scripts/session-reset-allowlist.txt names field(s) that no longer exist: "
                + string.Join(", ", stale));
        }

        // Every static of the inventory classes is either allowlisted or reset by the helper: this
        // cell sets each non-allowlisted one to a value the reset must undo, runs the reset and
        // reads it back. A new static fails here (or in the mutation step, naming the field)
        // until it is reset or allowlisted with a reason. A save-scoped holder is read through:
        // every field of the holder the reset leaves current must be back at its value.
        [Fact]
        public void Inventory_EveryOtherStaticIsResetByTheHelper()
        {
            var allow = ReadAllowlist();
            var statics = InventoryFields().Where(f => !allow.ContainsKey(FieldKey(f))).ToList();
            var slots = SlotsOf(statics).Where(sl => !allow.ContainsKey(sl.Key)).ToList();
            Assert.NotEmpty(slots);

            List<string> notReset = FindSlotsNotReset(slots,
                () => ParsekScenario.ResetSaveScopedSessionState("test-gate"), "ResetSaveScopedSessionState");
            Assert.True(notReset.Count == 0,
                "session-reset gate: ParsekScenario.ResetSaveScopedSessionState leaves these statics as set: "
                + string.Join(", ", notReset)
                + " (reset them in the helper, or allowlist them in scripts/session-reset-allowlist.txt with a reason)");
        }

        // Runs the reset once for the baseline, sets every slot to a value the reset must undo,
        // runs it again and returns the slots that did not come back. A slot the generic
        // mutation cannot set fails the calling cell by name.
        private static List<string> FindSlotsNotReset(List<Slot> slots, Action reset, string resetName)
        {
            reset();
            var baseline = slots.ToDictionary(sl => sl, sl => sl.Read());
            var baselineCounts = slots.ToDictionary(sl => sl, sl => CountOf(sl.Read()));
            var holdersBefore = slots.Where(sl => sl.Holder != null).Select(sl => sl.Holder).Distinct()
                .ToDictionary(h => h, h => h.GetValue(null));

            var mutated = new Dictionary<Slot, object>();
            var cannotMutate = new List<string>();
            foreach (Slot sl in slots)
            {
                if (sl.Holder != null && sl.Target == null)
                {
                    cannotMutate.Add(sl.Key + " (holder " + sl.Holder.Name + " is null)");
                    continue;
                }
                if (!TryMutate(sl.Field, sl.Target, baseline[sl], out object mutatedValue))
                    cannotMutate.Add(sl.Key + " (" + sl.Field.FieldType.Name + ")");
                else
                    mutated[sl] = mutatedValue;
            }
            Assert.True(cannotMutate.Count == 0,
                "session-reset gate (" + resetName + "): the generic cell cannot set these statics; teach TryMutate "
                + "the type or allowlist the field with a reason: " + string.Join(", ", cannotMutate));

            reset();

            var notReset = new List<string>();
            foreach (var holder in holdersBefore)
            {
                if (holder.Key.GetValue(null) == null)
                    notReset.Add(holder.Key.DeclaringType.Name + "." + holder.Key.Name + " (holder left null)");
            }
            foreach (Slot sl in slots)
            {
                object after = sl.Target == null && sl.Holder != null ? null : sl.Read();
                object before = baseline[sl];
                bool ok;
                if (IsCollectionValue(sl.Field.FieldType, before) || IsCollectionValue(sl.Field.FieldType, after))
                {
                    ok = (before == null) == (after == null)
                        && CountOf(after) == baselineCounts[sl];
                }
                else if (sl.Field.FieldType.IsValueType || sl.Field.FieldType == typeof(string))
                {
                    ok = Equals(before, after);
                }
                else if (before == null)
                {
                    ok = after == null;
                }
                else
                {
                    ok = after != null && !(mutated.TryGetValue(sl, out object m) && ReferenceEquals(after, m));
                }
                if (!ok)
                    notReset.Add(sl.Key);
            }
            return notReset;
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

        private static bool TryMutate(FieldInfo f, object target, object current, out object mutatedValue)
        {
            mutatedValue = null;
            Type t = f.FieldType;
            if (t == typeof(bool)) { mutatedValue = !(bool)current; }
            else if (t == typeof(int)) { mutatedValue = (int)current + 7; }
            else if (t == typeof(uint)) { mutatedValue = (uint)current + 7u; }
            else if (t == typeof(long)) { mutatedValue = (long)current + 7L; }
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
            f.SetValue(target, mutatedValue);
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

        // ---- the lifetime map: every save-scoped store has a cold-reset cell ----

        // The stores scripts/arch/lifetimes.toml tags save or load-transition that the helper
        // does not reset: each is reset on a Cold load by its own Cold-path call (a loader that
        // replaces it from the save, a consume or a drop). The cell runs the helper, then that
        // call with a save that holds nothing, exactly as the Cold OnLoad does, and reads every
        // save / load-transition static of the store back. A type the map tags that is in
        // neither this table nor InventoryTypes fails ColdResetCells_CoverEverySaveScopedType.
        private static ConfigNode emptyLedgerNode;

        private static readonly Dictionary<Type, Action> ColdLoaderCells = new Dictionary<Type, Action>
        {
            // ParsekScenario.OnLoad cold branch: VesselNameStore.Load(node).
            [typeof(VesselNameStore)] = () => VesselNameStore.Load(EmptyScenarioNode()),
            // ParsekScenario.OnLoad cold branch: MissionStore.Load(node).
            [typeof(MissionStore)] = () => MissionStore.Load(EmptyScenarioNode()),
            // The helper's ClearAllEscrow, then the cold branch's RouteStore.LoadRoutesFrom(node).
            [typeof(Parsek.Logistics.RouteStore)] = () => Parsek.Logistics.RouteStore.LoadRoutesFrom(EmptyScenarioNode()),
            // RouteStore.LoadRoutesFrom calls RouteEventLog.LoadFrom(node).
            [typeof(Parsek.Logistics.RouteEventLog)] = () => Parsek.Logistics.RouteStore.LoadRoutesFrom(EmptyScenarioNode()),
            // LoadCrewAndGroupState: the group hierarchy and the hidden groups.
            [typeof(GroupHierarchyStore)] = () =>
            {
                ConfigNode node = EmptyScenarioNode();
                GroupHierarchyStore.LoadGroupHierarchy(node);
                GroupHierarchyStore.LoadHiddenGroups(node);
            },
            // LoadCrewAndGroupState: CrewReservationManager.LoadCrewReplacements(node).
            [typeof(CrewReservationManager)] = () => CrewReservationManager.LoadCrewReplacements(EmptyScenarioNode()),
            // The helper's PlaybackScopeTracker.Reset, then LoadPlaybackReplayScope's Save cell.
            [typeof(PlaybackScopeTracker)] = () => PlaybackScopeTracker.InstallFromSave(EmptyScenarioNode(), "load Cold"),
            // The cold branch's LedgerOrchestrator.OnLoad -> Ledger.LoadFromNode -> StockShadowTakeBack.LoadFrom.
            [typeof(StockShadowTakeBack)] = () => LedgerOrchestrator.OnLoad(emptyLedgerNode.CreateCopy()),
            [typeof(Ledger)] = () => LedgerOrchestrator.OnLoad(emptyLedgerNode.CreateCopy()),
            // LoadLedgerHeldWindow's Save cell: InstallAfterLoad(the loaded clock, the saved floor).
            [typeof(LedgerHeldWindow)] = () => LedgerHeldWindow.InstallAfterLoad(
                0.0, LedgerHeldWindow.ReadSaved(EmptyScenarioNode()), "Load (Cold)"),
            // The rewind vessel holds' Save cell: Replace(LoadFrom(node)).
            [typeof(RewindVesselHold)] = () => RewindVesselHold.Replace(
                RewindVesselHold.LoadFrom(EmptyScenarioNode()), "load Cold"),
            // The removed replay flags' Save cell: Replace(LoadFrom(node)).
            [typeof(RemovedReplayFlags)] = () => RemovedReplayFlags.Replace(
                RemovedReplayFlags.LoadFrom(EmptyScenarioNode()), "load Cold"),
            // The helper captures the dropped flight; the cold branch reaps it once the lists are final.
            [typeof(NoSaveSceneExit)] = () => NoSaveSceneExit.ReapDroppedFlightSidecars(false, false, false),
            // Every OnLoad consumes the intent while it classifies, before the helper runs.
            [typeof(SaveFileLoadIntent)] = () => SaveFileLoadIntent.TryConsume(
                0.0, 0, "test-cold", out double _, out int _),
            // The helper clears it (DiscardReFlyLoadIntent.Clear("save-scoped-reset")).
            [typeof(DiscardReFlyLoadIntent)] = () => { },
            // Handoff step A takes the captured handoff on every load and drops it on a Cold one.
            [typeof(InSessionStagedStateHandoff)] = () => InSessionStagedStateHandoff.Take(),
            // OnLoad clears a leftover recovery request before it classifies.
            [typeof(InFlightRecoveryRequest)] = () => InFlightRecoveryRequest.Clear("OnLoad"),
            // RevertDetector is consumed only by the in-session branch: see ColdLoaderKnownGaps.
            [typeof(RevertDetector)] = () => { },
        };

        // Save / load-transition statics a Cold load does not reset today. Each is pinned so
        // the cell stays green on the code as it is and goes red the day the gap closes (drop
        // the entry then). Value: the todo id and why.
        private static readonly Dictionary<string, string> ColdLoaderKnownGaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DiscardReFlyLoadIntent.armedTarget"] = "COLD-RESET-LEFTOVERS: Clear and TryConsume drop armed, the "
                + "session and the UT but keep the target; inert while armed is false (every reader checks it)",
            ["VesselNameStore.lastPostInitState"] = "COLD-RESET-LEFTOVERS: tagged save by the type default, but "
                + "Load keeps it with FlightEpoch on purpose (ClearRevertUndoState(resetEpoch: false))",
            ["RevertDetector.pending"] = "COLD-RESET-LEFTOVERS: consumed only by OnLoad's in-session branch; a "
                + "revert armed with no load of its own would classify a later load of another save",
            ["RevertDetector.revertTargetVesselPids"] = "COLD-RESET-LEFTOVERS: consumed with pending, only by "
                + "OnLoad's in-session branch",
        };

        private static ConfigNode EmptyScenarioNode() => new ConfigNode("SCENARIO");

        private static void ResetColdLoaderStores()
        {
            VesselNameStore.ResetForTesting();
            MissionStore.ResetForTesting();
            Parsek.Logistics.RouteStore.ResetForTesting();
            Parsek.Logistics.RouteEventLog.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            PlaybackScopeTracker.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            RewindVesselHold.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            Ledger.ResetForTesting();
            NoSaveSceneExit.ResetForTesting();
            SaveFileLoadIntent.ResetForTesting();
            DiscardReFlyLoadIntent.ResetForTesting();
            InSessionStagedStateHandoff.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        // A minimal reader of scripts/arch/lifetimes.toml, enough for its [[type]] and
        // [[pattern]] tables: a static's lifetime is its field entry, else the first pattern
        // its name matches, else its type's default (lifetimes.py resolve()).
        private sealed class LifetimeMap
        {
            internal readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Dictionary<string, string>> Fields =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            internal readonly List<KeyValuePair<string, string>> Patterns = new List<KeyValuePair<string, string>>();

            internal string Resolve(string typeFullName, string fieldDisplayName)
            {
                if (Fields.TryGetValue(typeFullName, out var fields)
                    && fields.TryGetValue(fieldDisplayName, out string tagged))
                    return tagged;
                foreach (var p in Patterns)
                {
                    if (p.Key.StartsWith("*", StringComparison.Ordinal)
                        && fieldDisplayName.EndsWith(p.Key.Substring(1), StringComparison.Ordinal))
                        return p.Value;
                }
                return Defaults.TryGetValue(typeFullName, out string d) ? d : null;
            }

            internal IEnumerable<string> ColdClearedTypes()
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in Defaults)
                    if (IsColdCleared(d.Value)) names.Add(d.Key);
                foreach (var f in Fields)
                    if (f.Value.Values.Any(IsColdCleared)) names.Add(f.Key);
                return names.OrderBy(n => n, StringComparer.Ordinal);
            }
        }

        private static bool IsColdCleared(string lifetime) => lifetime == "save" || lifetime == "load-transition";

        private static string LifetimesPath()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "scripts", "arch", "lifetimes.toml"));
        }

        private static string TomlString(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"')
                return value.Substring(1, value.IndexOf('"', 1) - 1);
            return value;
        }

        private static LifetimeMap ReadLifetimeMap()
        {
            string path = LifetimesPath();
            Assert.True(File.Exists(path), "lifetime map not found at " + path);
            var map = new LifetimeMap();
            string section = null;
            string typeName = null;
            string patternMatch = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    section = line;
                    if (line == "[[type]]") typeName = null;
                    if (line == "[[pattern]]") patternMatch = null;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = TomlString(line.Substring(eq + 1));
                if (section == "[[type]]")
                {
                    if (key == "name") typeName = value;
                    else if (key == "default" && typeName != null) map.Defaults[typeName] = value;
                }
                else if (section == "[type.fields]" && typeName != null)
                {
                    if (!map.Fields.TryGetValue(typeName, out var fields))
                        map.Fields[typeName] = fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    int colon = value.IndexOf(':');
                    fields[key] = (colon < 0 ? value : value.Substring(0, colon)).Trim();
                }
                else if (section == "[[pattern]]")
                {
                    if (key == "match") patternMatch = value;
                    else if (key == "lifetime" && patternMatch != null)
                        map.Patterns.Add(new KeyValuePair<string, string>(patternMatch, value));
                }
            }
            Assert.NotEmpty(map.Defaults);
            return map;
        }

        // The statics of a cold-loader store that the map says a Cold load must clear: a
        // reassignable static, or a readonly collection (lifetimes.py's population), whose
        // lifetime resolves to save or load-transition. A save-scoped holder is read through.
        private static List<Slot> ColdClearedSlots(Type t, LifetimeMap map)
        {
            var statics = StaticFieldsOf(t).Where(f =>
                    (!f.IsInitOnly || IsCollectionValue(f.FieldType, f.GetValue(null)))
                    && IsColdCleared(map.Resolve(t.FullName, DisplayName(f))))
                .ToList();
            return SlotsOf(statics);
        }

        // catches: a type the lifetime map tags save or load-transition with no cold-reset
        // cell (a new save-scoped store nobody tied to the Cold load), or a cell for a type the
        // map no longer tags.
        [Fact]
        public void ColdResetCells_CoverEverySaveScopedType()
        {
            LifetimeMap map = ReadLifetimeMap();
            var covered = new HashSet<string>(
                InventoryTypes.Select(t => t.FullName).Concat(ColdLoaderCells.Keys.Select(t => t.FullName)),
                StringComparer.Ordinal);
            var tagged = map.ColdClearedTypes().ToList();
            var uncovered = tagged.Where(n => !covered.Contains(n)).ToList();
            Assert.True(uncovered.Count == 0,
                "cold-reset gate: scripts/arch/lifetimes.toml tags these types save or load-transition, but no "
                + "cell resets them (add the store's Cold-path call to ColdLoaderCells, or its type to "
                + "InventoryTypes when ResetSaveScopedSessionState resets it): " + string.Join(", ", uncovered));
            var stale = ColdLoaderCells.Keys.Select(t => t.FullName).Where(n => !tagged.Contains(n)).ToList();
            Assert.True(stale.Count == 0,
                "cold-reset gate: ColdLoaderCells names types the lifetime map tags no save or load-transition "
                + "static in: " + string.Join(", ", stale));
            foreach (Type t in ColdLoaderCells.Keys)
                Assert.True(ColdClearedSlots(t, map).Count > 0, "cold-reset gate: no save-scoped static found in " + t.FullName);
        }

        public static IEnumerable<object[]> ColdLoaderTypeNames()
        {
            return ColdLoaderCells.Keys.Select(t => new object[] { t.FullName }).OrderBy(o => (string)o[0], StringComparer.Ordinal);
        }

        // Every save / load-transition static of a cold-loader store is back at its value after
        // the Cold load's own path (the helper, then the store's call). The known gaps are the
        // statics it leaves set today, each pinned with its todo id.
        [Theory]
        [MemberData(nameof(ColdLoaderTypeNames))]
        public void ColdLoader_ResetsEverySaveScopedStatic(string typeName)
        {
            Type t = ColdLoaderCells.Keys.Single(k => k.FullName == typeName);
            Action loader = ColdLoaderCells[t];
            LifetimeMap map = ReadLifetimeMap();
            List<Slot> slots = ColdClearedSlots(t, map);
            Assert.NotEmpty(slots);

            ResetAll();
            emptyLedgerNode = Ledger.BuildSaveNode();
            List<string> notReset = FindSlotsNotReset(slots, () =>
            {
                ParsekScenario.ResetSaveScopedSessionState("test-cold-loader");
                loader();
            }, typeName);

            var unexpected = notReset.Where(k => !ColdLoaderKnownGaps.ContainsKey(k)).ToList();
            Assert.True(unexpected.Count == 0,
                "cold-reset gate: a Cold load leaves these " + typeName + " statics as set: "
                + string.Join(", ", unexpected)
                + " (clear them on the store's Cold path, or tag them with the lifetime they really have)");
            var closed = ColdLoaderKnownGaps.Keys
                .Where(k => slots.Any(sl => sl.Key == k) && !notReset.Contains(k)).ToList();
            Assert.True(closed.Count == 0,
                "cold-reset gate: these known gaps are reset now; drop them from ColdLoaderKnownGaps: "
                + string.Join(", ", closed));
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

        // catches: WARP-REQUEST-SURVIVES-A-FAILED-REWIND. A Warp to time request left armed by a
        // rewind that never launched (or a deferred flight warp whose Space Center arrival never
        // came) jumping the clock of the next save loaded in the same process.
        [Fact]
        public void WarpToTimeRequests_AreDroppedByTheColdReset_WithTheReason()
        {
            WarpToTimeRequest.Set(98765.0);
            WarpToTimeRequest.SetDeferredKscWarp(3, 12, 6, 30);
            logLines.Clear();

            ParsekScenario.ResetSaveScopedSessionState("save-file");

            Assert.False(WarpToTimeRequest.HasPending);
            Assert.Equal(0.0, WarpToTimeRequest.TargetUT);
            Assert.False(WarpToTimeRequest.HasDeferredKscWarp);
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Pending warp dropped: targetUT=98765.0 reason=cold-load:save-file"));
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Deferred flight warp dropped: Y3 D12 6:30 reason=cold-load:save-file"));
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
                && l.Contains("Save-scoped session state reset: reason=main-menu pendingTreeDropped=false storesReset=14"));
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

            PendingTreeSlot.StashPendingTree(tree, PendingTreeState.Limbo);
            var science = new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 1f };
            GameStateRecorder.PendingScienceSubjects.Add(science);

            ParsekScenario.ResetSaveScopedSessionState("main-menu");

            Assert.False(PendingTreeSlot.HasPendingTree);
            Assert.False(TreeDiscardService.WasTreeDiscardedThisSession(treeId));
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
            TreeDiscardService.NoteTreeDiscardedThisSession("tree-gone", "Gone", 1);
            var attemptTree = new RecordingTree { Id = "tree-attempt", TreeName = "Attempt" };
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(attemptTree, "test");
            Assert.True(TreeDiscardService.WasTreeDiscardedThisSession("tree-gone"));
            Assert.True(CommittedTreeRestoreAttempt.HasCommittedTreeRestoreAttempt);

            ParsekScenario.ResetSaveScopedSessionState("main-menu");

            Assert.False(TreeDiscardService.WasTreeDiscardedThisSession("tree-gone"));
            Assert.False(CommittedTreeRestoreAttempt.HasCommittedTreeRestoreAttempt);
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
