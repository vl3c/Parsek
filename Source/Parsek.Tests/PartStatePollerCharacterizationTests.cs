using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization goldens for the part-state poll loops that the flight recorder
    /// (<c>FlightRecorder.Check*State(Vessel)</c>) and the background recorder
    /// (<c>BackgroundRecorder.Check*State(Vessel, BackgroundVesselState, Recording, double)</c>)
    /// both run. Each family drives the SAME scripted sequence of headless part stubs through
    /// both recorders and writes one transcript per side: the events appended, the log lines
    /// written, the tracking containers after each poll, and any exception a native-only member
    /// raised. The two transcripts differ only where the recorders differ (log tag, wording,
    /// rate-limit key, departure context), which is the contract a shared poll loop must keep.
    ///
    /// Stubs: parts, modules and the module list are uninitialized objects whose Unity cached
    /// pointer is set (see <see cref="BackgroundRecorderCharacterizationSupport"/>), so managed
    /// field reads and Unity's == work while any native member still throws.
    /// </summary>
    [Collection("Sequential")]
    public class PartStatePollerCharacterizationTests : IDisposable
    {
        private const double StartUT = 1000.0;
        private const uint VesselPid = 7u;
        // The live vessel's pid differs from the BG state's pid so a transcript shows which
        // of the two each recorder's log context reads.
        private const uint LiveVesselPid = 77u;

        private readonly List<string> logLines = new List<string>();
        private readonly Planetarium previousPlanetarium;

        public PartStatePollerCharacterizationTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 5000.0;
            previousPlanetarium = InstallPlanetarium(StartUT);
        }

        public void Dispose()
        {
            Planetarium.fetch = previousPlanetarium;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================ stubs

        internal sealed class FieldStubModule : PartModule
        {
            [KSPField] public string StateName;
            [KSPField] public bool isDeployed;
        }

        private static void SetField(object target, Type declaring, string name, object value)
        {
            FieldInfo f = declaring.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(f != null, declaring.Name + "." + name + " not found");
            f.SetValue(target, value);
        }

        private static Part MakePart(uint pid, string name, params PartModule[] modules)
        {
            Part p = Alive<Part>();
            p.persistentId = pid;
            p.partInfo = Uninit<AvailablePart>();
            p.partInfo.name = name;
            PartModuleList list = Uninit<PartModuleList>();
            SetField(list, typeof(PartModuleList), "modules", new List<PartModule>(modules));
            SetField(list, typeof(PartModuleList), "part", p);
            SetField(p, typeof(Part), "modules", list);
            return p;
        }

        private static T Module<T>(string moduleName = null) where T : PartModule
        {
            T m = Alive<T>();
            m.moduleName = moduleName ?? typeof(T).Name;
            return m;
        }

        private static FieldStubModule FieldStub(string moduleName, bool withFields)
        {
            FieldStubModule m = Module<FieldStubModule>(moduleName);
            if (withFields)
                SetField(m, typeof(PartModule), "fields", new BaseFieldList(m));
            return m;
        }

        private static Vessel MakeVessel(uint pid, params Part[] parts)
        {
            Vessel v = Vessel(pid, alive: true, body: null);
            v.parts = new List<Part>(parts);
            return v;
        }

        // ================================================================ harness

        /// <summary>One recorder side: how to run the family's poll and where its state lives.</summary>
        private sealed class Side
        {
            internal string Name;
            internal Action<Vessel, double> Poll;
            internal Func<List<PartEvent>> Events;
            internal object StateOwner;
        }

        private static Side FlightSide(string method)
        {
            var rec = new FlightRecorder();
            MethodInfo mi = typeof(FlightRecorder).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Vessel) }, null);
            Assert.True(mi != null, "FlightRecorder." + method + "(Vessel) not found");
            return new Side
            {
                Name = "FG",
                Poll = (v, ut) =>
                {
                    SetPlanetariumUT(ut);
                    mi.Invoke(rec, new object[] { v });
                },
                Events = () => rec.PartEvents,
                StateOwner = rec,
            };
        }

        private static readonly Type BgStateType = typeof(BackgroundRecorder).GetNestedType(
            "BackgroundVesselState", BindingFlags.NonPublic);

        private static object NewBgState(uint vesselPid, string recordingId)
        {
            object state = Activator.CreateInstance(BgStateType, nonPublic: true);
            BgStateType.GetField("vesselPid").SetValue(state, vesselPid);
            BgStateType.GetField("recordingId").SetValue(state, recordingId);
            return state;
        }

        private static BackgroundRecorder NewBg()
        {
            var tree = new RecordingTree
            {
                Id = "tree_poll",
                TreeName = "Poll Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_root",
            };
            return new BackgroundRecorder(tree);
        }

        private static Side BackgroundSide(string method, uint vesselPid = VesselPid)
        {
            BackgroundRecorder bg = NewBg();
            object state = NewBgState(vesselPid, "rec_bg_" + vesselPid.ToString(CultureInfo.InvariantCulture));
            var treeRec = new Recording { RecordingId = "rec_bg_" + vesselPid.ToString(CultureInfo.InvariantCulture) };
            MethodInfo mi = typeof(BackgroundRecorder).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Vessel), BgStateType, typeof(Recording), typeof(double) }, null);
            Assert.True(mi != null, "BackgroundRecorder." + method + "(...) not found");
            return new Side
            {
                Name = "BG",
                Poll = (v, ut) => mi.Invoke(bg, new object[] { v, state, treeRec, ut }),
                Events = () => treeRec.PartEvents,
                StateOwner = state,
            };
        }

        /// <summary>
        /// Runs a scripted scenario against one side and returns its transcript. Each step
        /// mutates the stubs, polls once, then records the new events, the new log lines and
        /// the named containers.
        /// </summary>
        private string RunSide(Side side, Func<Vessel> build,
            IList<(string label, Action<Vessel> mutate)> steps, string[] containers)
        {
            var sb = new StringBuilder();
            sb.Append("## ").Append(side.Name).Append('\n');
            logLines.Clear();

            // Null guards: no vessel, and a vessel whose part list is null.
            AppendPoll(sb, side, "null-vessel", null, StartUT, containers: new string[0]);
            Vessel noParts = Vessel(LiveVesselPid, alive: true, body: null);
            noParts.parts = null;
            AppendPoll(sb, side, "null-parts", noParts, StartUT, containers: new string[0]);

            Vessel v = build();
            double ut = StartUT;
            for (int i = 0; i < steps.Count; i++)
            {
                ut += 10.0;
                steps[i].mutate?.Invoke(v);
                AppendPoll(sb, side, steps[i].label, v, ut, containers);
            }
            return sb.ToString();
        }

        private void AppendPoll(StringBuilder sb, Side side, string label, Vessel v, double ut,
            string[] containers)
        {
            int before = side.Events().Count;
            int logBefore = logLines.Count;
            string outcome = "ok";
            try
            {
                side.Poll(v, ut);
            }
            catch (TargetInvocationException tie)
            {
                outcome = "threw " + NormalizeException(tie.InnerException);
            }
            sb.Append("step ").Append(label).Append(" ut=").Append(D(ut)).Append(": ").Append(outcome).Append('\n');
            List<PartEvent> events = side.Events();
            for (int i = before; i < events.Count; i++)
            {
                PartEvent e = events[i];
                sb.Append("  ev ").Append(e.eventType)
                    .Append(" pid=").Append(e.partPersistentId.ToString(CultureInfo.InvariantCulture))
                    .Append(" name=").Append(e.partName ?? "null")
                    .Append(" ut=").Append(D(e.ut))
                    .Append(" val=").Append(e.value.ToString("R", CultureInfo.InvariantCulture))
                    .Append(" midx=").Append(e.moduleIndex.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            for (int i = logBefore; i < logLines.Count; i++)
                sb.Append("  log ").Append(NormalizeLogLine(logLines[i])).Append('\n');
            foreach (string c in containers)
                sb.Append("  ").Append(c).Append('=').Append(RenderContainer(side.StateOwner, c)).Append('\n');
        }

        // Runtime exception messages are platform text (mono vs .NET, localized on Windows), so a
        // logged "Type: message" keeps the exception type and drops the message.
        private static readonly System.Text.RegularExpressions.Regex ExceptionMessage =
            new System.Text.RegularExpressions.Regex(@"(\w+Exception): [^)]*\)");

        private static string NormalizeLogLine(string line)
        {
            return ExceptionMessage.Replace(line, "$1: <message>)");
        }

        private static string NormalizeException(Exception ex)
        {
            if (ex == null) return "null";
            // A Unity ECall surfaces as MissingMethodException on mono and SecurityException on
            // Windows .NET; both mean "reached native code", so the golden names that instead.
            if (ex is MissingMethodException || ex is System.Security.SecurityException
                || ex is MethodAccessException)
                return "native-call";
            return ex.GetType().Name;
        }

        private static string RenderContainer(object owner, string name)
        {
            FieldInfo f = owner.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) return "(no field)";
            object value = f.GetValue(owner);
            if (value == null) return "null";
            var items = new List<string>();
            if (value is IDictionary dict)
            {
                foreach (DictionaryEntry de in dict)
                    items.Add(Render(de.Key) + ":" + Render(de.Value));
            }
            else if (value is IEnumerable seq)
            {
                foreach (object o in seq)
                    items.Add(Render(o));
            }
            items.Sort(StringComparer.Ordinal);
            return "[" + string.Join(",", items) + "]";
        }

        private static string Render(object o)
        {
            switch (o)
            {
                case null: return "null";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return D(d);
                case string[] arr: return "{" + string.Join("|", arr) + "}";
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return o.ToString();
            }
        }

        private void RunFamily(string goldenName, string method, Func<Vessel> build,
            IList<(string label, Action<Vessel> mutate)> steps, params string[] containers)
        {
            string fg = RunSide(FlightSide(method), build, steps, containers);
            string bg = RunSide(BackgroundSide(method), build, steps, containers);
            AssertGolden("part-state-poller-" + goldenName + ".golden.txt",
                "# " + method + "\n" + fg + bg);
        }

        private static (string, Action<Vessel>) Step(string label, Action<Vessel> mutate)
            => (label, mutate);

        private static T Mod<T>(Vessel v, int part, int module = 0) where T : PartModule
        {
            PartModuleList list = v.parts[part].Modules;
            return (T)list[module];
        }

        // ================================================================ families

        [Fact]
        public void Gear()
        {
            RunFamily("gear", "CheckGearState",
                () =>
                {
                    var w1 = Module<ModuleWheels.ModuleWheelDeployment>();
                    w1.stateString = "Retracted";
                    var w2a = Module<ModuleWheels.ModuleWheelDeployment>();
                    w2a.stateString = "Deploying";
                    var w2b = Module<ModuleWheels.ModuleWheelDeployment>();
                    w2b.stateString = "Deployed";
                    var w3a = Module<ModuleWheels.ModuleWheelDeployment>();
                    w3a.stateString = "Retracted";
                    var w3b = Module<ModuleWheels.ModuleWheelDeployment>();
                    w3b.stateString = "Deployed";
                    return MakeVessel(LiveVesselPid,
                        MakePart(101, "gearA", w1),
                        null,
                        MakePart(102, "gearTransitFirst", w2a, w2b),
                        MakePart(103, "gearFirstWins", w3a, w3b),
                        MakePart(104, "noGear", Module<ModuleJettison>()));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-deployed", v => Mod<ModuleWheels.ModuleWheelDeployment>(v, 0).stateString = "Deployed"),
                    Step("A-deployed-again", null),
                    Step("A-retracting", v => Mod<ModuleWheels.ModuleWheelDeployment>(v, 0).stateString = "Retracting"),
                    Step("A-retracted", v => Mod<ModuleWheels.ModuleWheelDeployment>(v, 0).stateString = "Retracted"),
                },
                "deployedGear");
        }

        [Fact]
        public void Parachute()
        {
            RunFamily("parachute", "CheckParachuteState",
                () =>
                {
                    var c1 = Module<ModuleParachute>();
                    c1.deploymentState = ModuleParachute.deploymentStates.STOWED;
                    var c2 = Module<ModuleParachute>();
                    c2.deploymentState = ModuleParachute.deploymentStates.CUT;
                    return MakeVessel(LiveVesselPid,
                        MakePart(201, "chuteA", c1),
                        MakePart(202, "chuteAlreadyCut", c2),
                        null,
                        MakePart(203, "noChute"));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-semi", v => Mod<ModuleParachute>(v, 0).deploymentState = ModuleParachute.deploymentStates.SEMIDEPLOYED),
                    Step("A-deployed", v => Mod<ModuleParachute>(v, 0).deploymentState = ModuleParachute.deploymentStates.DEPLOYED),
                    Step("A-cut", v => Mod<ModuleParachute>(v, 0).deploymentState = ModuleParachute.deploymentStates.CUT),
                    Step("A-repacked", v => Mod<ModuleParachute>(v, 0).deploymentState = ModuleParachute.deploymentStates.STOWED),
                    Step("A-active", v => Mod<ModuleParachute>(v, 0).deploymentState = ModuleParachute.deploymentStates.ACTIVE),
                },
                "parachuteStates");
        }

        [Fact]
        public void Jettison()
        {
            RunFamily("jettison", "CheckJettisonState",
                () =>
                {
                    var j1 = Module<ModuleJettison>();
                    j1.jettisonName = "";
                    var j2 = Module<ModuleJettison>();
                    j2.jettisonName = "shroudA, shroudB";
                    var j3a = Module<ModuleJettison>();
                    j3a.jettisonName = " ";
                    var j3b = Module<ModuleJettison>();
                    j3b.jettisonName = "";
                    j3b.isJettisoned = true;
                    var j4 = Module<ModuleJettison>();
                    j4.jettisonName = " shroud1 ,, shroud2 ";
                    return MakeVessel(LiveVesselPid,
                        MakePart(301, "jettisonPlain", j1),
                        MakePart(302, "jettisonVariant", Module<ModulePartVariants>(), j2),
                        null,
                        MakePart(303, "jettisonSecondModule", j3a, j3b),
                        MakePart(304, "noJettison", Module<ModuleParachute>()),
                        MakePart(305, "jettisonProbesTransform", j4));
                },
                new[]
                {
                    Step("initial", null),
                    Step("plain-jettisoned", v => Mod<ModuleJettison>(v, 0).isJettisoned = true),
                    Step("plain-again", null),
                    Step("plain-reset-flag", v => Mod<ModuleJettison>(v, 0).isJettisoned = false),
                },
                "jettisonedShrouds", "jettisonNameRawCache", "parsedJettisonNamesCache");
        }

        [Fact]
        public void Jettison_NameCacheKeysAndRefresh()
        {
            // Two jettison modules on one part: the first names only separators (parsed to an
            // empty list, so no transform probe), the second a real name whose probe throws
            // headless after the cache is written. Pins the per-module cache key (module index
            // among the part's jettison modules) and the re-parse when the raw name changes.
            RunFamily("jettison-cache", "CheckJettisonState",
                () =>
                {
                    var jA = Module<ModuleJettison>();
                    jA.jettisonName = " , ";
                    var jB = Module<ModuleJettison>();
                    jB.jettisonName = "shroudB";
                    return MakeVessel(LiveVesselPid, MakePart(311, "jettisonTwoNamed", jA, jB));
                },
                new[]
                {
                    Step("initial", null),
                    Step("B-renamed", v => Mod<ModuleJettison>(v, 0, 1).jettisonName = "shroudC"),
                    Step("B-same", null),
                },
                "jettisonedShrouds", "jettisonNameRawCache", "parsedJettisonNamesCache");
        }

        [Fact]
        public void Deployable_ReversedMidTransition()
        {
            // An extended panel told to retract and then extended again before it closes:
            // the transitional states write nothing, so the only events are the two endpoints.
            RunFamily("deployable-reversal", "CheckDeployableState",
                () =>
                {
                    var d = Module<ModuleDeployableSolarPanel>();
                    d.animationName = "panelAnim";
                    d.deployState = ModuleDeployablePart.DeployState.EXTENDED;
                    return MakeVessel(LiveVesselPid, MakePart(431, "panelReversed", d));
                },
                new[]
                {
                    Step("initial-extended", null),
                    Step("retracting", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.RETRACTING),
                    Step("extending", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.EXTENDING),
                    Step("extended", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.EXTENDED),
                    Step("retracted", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.RETRACTED),
                },
                "extendedDeployables");
        }

        [Fact]
        public void Deployable_BrokenPoselessIsNotCountedPoseless()
        {
            // A broken deployable leaves the poll at the broken edge, before the pose-animation
            // gate, so a broken pose-less panel never counts toward the pose-less summary line.
            RunFamily("deployable-broken-poseless", "CheckDeployableState",
                () =>
                {
                    var d = Module<ModuleDeployableSolarPanel>();
                    d.animationName = "";
                    d.deployState = ModuleDeployablePart.DeployState.BROKEN;
                    return MakeVessel(LiveVesselPid, MakePart(421, "panelPoselessBroken", d));
                },
                new[]
                {
                    Step("initial-broken", null),
                    Step("broken-again", null),
                },
                "brokenDeployables", "extendedDeployables");
        }

        [Fact]
        public void Deployable()
        {
            RunFamily("deployable", "CheckDeployableState",
                () =>
                {
                    var d1 = Module<ModuleDeployableSolarPanel>();
                    d1.animationName = "panelAnim";
                    d1.deployState = ModuleDeployablePart.DeployState.RETRACTED;
                    var d2 = Module<ModuleDeployableSolarPanel>();
                    d2.animationName = "";
                    d2.deployState = ModuleDeployablePart.DeployState.EXTENDED;
                    return MakeVessel(LiveVesselPid,
                        MakePart(401, "panelA", d1),
                        MakePart(402, "panelPoseless", d2),
                        null,
                        MakePart(403, "noPanel"));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-extending", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.EXTENDING),
                    Step("A-extended", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.EXTENDED),
                    Step("A-broken", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.BROKEN),
                    Step("A-broken-again", null),
                    Step("A-repaired", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.RETRACTED),
                    Step("A-retracting", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.RETRACTING),
                    Step("A-extended-again", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.EXTENDED),
                    Step("A-retracted", v => Mod<ModuleDeployableSolarPanel>(v, 0).deployState = ModuleDeployablePart.DeployState.RETRACTED),
                },
                "extendedDeployables", "brokenDeployables");
        }

        /// <summary>
        /// The pose-less gate's rate limit: the flight recorder uses one key for every
        /// recorder, the background recorder one key per background vessel pid.
        /// </summary>
        [Fact]
        public void Deployable_PoselessRateLimitKeys()
        {
            Func<Vessel> build = () =>
            {
                var d = Module<ModuleDeployableSolarPanel>();
                d.animationName = null;
                d.deployState = ModuleDeployablePart.DeployState.RETRACTED;
                return MakeVessel(LiveVesselPid, MakePart(410, "panelPoseless", d), MakePart(411, "panelPoseless2", Module<ModuleDeployableSolarPanel>()));
            };
            var sb = new StringBuilder("# CheckDeployableState pose-less rate-limit keys\n");
            var empty = new string[0];
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Side fgA = FlightSide("CheckDeployableState");
            Side fgB = FlightSide("CheckDeployableState");
            sb.Append("## FG\n");
            AppendPoll(sb, fgA, "recorder-A", build(), StartUT, empty);
            AppendPoll(sb, fgA, "recorder-A-again", build(), StartUT + 1, empty);
            AppendPoll(sb, fgB, "recorder-B", build(), StartUT + 2, empty);

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Side bgA = BackgroundSide("CheckDeployableState", 7u);
            Side bgB = BackgroundSide("CheckDeployableState", 8u);
            sb.Append("## BG\n");
            AppendPoll(sb, bgA, "vessel-7", build(), StartUT, empty);
            AppendPoll(sb, bgA, "vessel-7-again", build(), StartUT + 1, empty);
            AppendPoll(sb, bgB, "vessel-8", build(), StartUT + 2, empty);
            AssertGolden("part-state-poller-deployable-ratelimit.golden.txt", sb.ToString());
        }

        [Fact]
        public void Converter()
        {
            RunFamily("converter", "CheckConverterState",
                () =>
                {
                    var c1 = Module<ModuleResourceConverter>();
                    var c2 = Module<ModuleResourceConverter>();
                    var c3a = Module<ModuleResourceConverter>();
                    var c3b = Module<ModuleResourceConverter>();
                    c3b.IsActivated = true;
                    return MakeVessel(LiveVesselPid,
                        MakePart(501, "isruA", c1),
                        MakePart(502, "drillLeaving", c2),
                        null,
                        MakePart(503, "isruSecondModule", c3a, c3b),
                        MakePart(504, "noConverter", Module<ModuleJettison>()));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-and-drill-on", v =>
                    {
                        Mod<ModuleResourceConverter>(v, 0).IsActivated = true;
                        Mod<ModuleResourceConverter>(v, 1).IsActivated = true;
                    }),
                    Step("drill-departs", v => v.parts.RemoveAt(1)),
                    Step("A-off", v => Mod<ModuleResourceConverter>(v, 0).IsActivated = false),
                },
                "activeConverterParts");
        }

        [Fact]
        public void WheelDamage()
        {
            RunFamily("wheel-damage", "CheckWheelDamageState",
                () =>
                {
                    var d1 = Module<ModuleWheels.ModuleWheelDamage>();
                    var d2a = Module<ModuleWheels.ModuleWheelDamage>();
                    var d2b = Module<ModuleWheels.ModuleWheelDamage>();
                    d2b.isDamaged = true;
                    return MakeVessel(LiveVesselPid,
                        MakePart(601, "wheelA", d1),
                        null,
                        MakePart(602, "wheelSecondModule", d2a, d2b),
                        MakePart(603, "noWheel"));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-broken", v => Mod<ModuleWheels.ModuleWheelDamage>(v, 0).isDamaged = true),
                    Step("A-broken-again", null),
                    Step("A-repaired", v => Mod<ModuleWheels.ModuleWheelDamage>(v, 0).isDamaged = false),
                },
                "damagedWheelParts");
        }

        [Fact]
        public void CargoBay()
        {
            RunFamily("cargo-bay", "CheckCargoBayState",
                () =>
                {
                    var b1 = Module<ModuleCargoBay>();
                    b1.DeployModuleIndex = -1;
                    var b2 = Module<ModuleCargoBay>();
                    b2.DeployModuleIndex = 5;
                    var b3 = Module<ModuleCargoBay>();
                    b3.DeployModuleIndex = 1;
                    var b4 = Module<ModuleCargoBay>();
                    b4.DeployModuleIndex = 1;
                    b4.closedPosition = 0.5f;
                    var a4 = Module<ModuleAnimateGeneric>();
                    a4.animTime = 0.3f;
                    var b5 = Module<ModuleCargoBay>();
                    b5.DeployModuleIndex = 1;
                    b5.closedPosition = 0f;
                    var a5 = Module<ModuleAnimateGeneric>();
                    a5.animTime = 0f;
                    var b6 = Module<ModuleCargoBay>();
                    b6.DeployModuleIndex = 1;
                    b6.closedPosition = 1f;
                    var a6 = Module<ModuleAnimateGeneric>();
                    a6.animTime = 1f;
                    a6.allowDeployLimit = true;
                    a6.deployPercent = 45f;
                    a6.revClampPercent = true;
                    var b7 = Module<ModuleCargoBay>();
                    b7.DeployModuleIndex = 1;
                    return MakeVessel(LiveVesselPid,
                        MakePart(701, "bayBadIndex", b1),
                        MakePart(702, "bayIndexPastEnd", b2),
                        MakePart(703, "bayNotAnim", b3, Module<ModuleJettison>()),
                        MakePart(704, "bayMidClosed", b4, a4),
                        null,
                        MakePart(705, "bayA", b5, a5),
                        MakePart(706, "bayLimited", b6, a6),
                        MakePart(707, "bayNullAnim", b7, null),
                        MakePart(708, "noBay"));
                },
                new[]
                {
                    Step("initial", null),
                    Step("again-diagnostics-once", null),
                    Step("A-open", v => Mod<ModuleAnimateGeneric>(v, 5, 1).animTime = 1f),
                    Step("A-mid-limited-at-stop", v =>
                    {
                        Mod<ModuleAnimateGeneric>(v, 5, 1).animTime = 0.5f;
                        Mod<ModuleAnimateGeneric>(v, 6, 1).animTime = 0.55f;
                    }),
                    Step("A-closed", v => Mod<ModuleAnimateGeneric>(v, 5, 1).animTime = 0f),
                },
                "openCargoBays", "loggedCargoBayDeployIndexIssues", "loggedCargoBayAnimationIssues",
                "loggedCargoBayClosedPositionIssues");
        }

        [Fact]
        public void Fairing()
        {
            RunFamily("fairing", "CheckFairingState",
                () =>
                {
                    var f1 = Module<ModuleProceduralFairing>();
                    var f2 = Module<ModuleProceduralFairing>();
                    KerbalFSM fsm = Uninit<KerbalFSM>();
                    SetField(f2, typeof(ModuleProceduralFairing), "fsm", fsm);
                    var f3 = Module<ModuleProceduralFairing>();
                    KerbalFSM fsm3 = Uninit<KerbalFSM>();
                    SetField(fsm3, typeof(KerbalFSM), "currentState", Uninit<KFSMState>());
                    SetField(f3, typeof(ModuleProceduralFairing), "fsm", fsm3);
                    return MakeVessel(LiveVesselPid,
                        MakePart(801, "fairingNoFsm", f1),
                        MakePart(802, "fairingFlightIdle", f2),
                        null,
                        MakePart(803, "fairingDeployed", f3),
                        MakePart(804, "noFairing"));
                },
                new[]
                {
                    Step("initial", null),
                    Step("again", null),
                    Step("B-deployed", v =>
                        SetField(GetFsm(Mod<ModuleProceduralFairing>(v, 1)), typeof(KerbalFSM), "currentState",
                            Uninit<KFSMState>())),
                },
                "deployedFairings", "loggedFairingReadFailures");
        }

        private static KerbalFSM GetFsm(ModuleProceduralFairing fairing)
        {
            return (KerbalFSM)typeof(ModuleProceduralFairing).GetField("fsm",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(fairing);
        }

        private static IList<(string, Action<Vessel>)> DeployedFlagSteps()
        {
            return new[]
            {
                Step("initial", null),
                Step("A-deployed", v => ((FieldStubModule)v.parts[0].Modules[1]).isDeployed = true),
                Step("A-deployed-again", null),
                Step("A-retracted", v => ((FieldStubModule)v.parts[0].Modules[1]).isDeployed = false),
            };
        }

        private static Func<Vessel> ModuleNameFamily(string moduleName)
        {
            return () =>
            {
                var a = FieldStub(moduleName, withFields: true);
                var miss = FieldStub(moduleName, withFields: false);
                var first = FieldStub(moduleName, withFields: true);
                first.isDeployed = true;
                first.StateName = "Extended";
                var second = FieldStub(moduleName, withFields: true);
                second.isDeployed = true;
                second.StateName = "Extended";
                return MakeVessel(LiveVesselPid,
                    MakePart(901, "partA", null, a),
                    MakePart(902, "partMiss", miss),
                    null,
                    MakePart(903, "partTwoModules", first, second),
                    MakePart(904, "partOtherName", FieldStub("SomethingElse", withFields: true)));
            };
        }

        [Fact]
        public void Ladder()
        {
            RunFamily("ladder", "CheckLadderState",
                ModuleNameFamily("RetractableLadder"),
                new[]
                {
                    Step("initial", v => ((FieldStubModule)v.parts[0].Modules[1]).StateName = "Retracted"),
                    Step("A-extended", v => ((FieldStubModule)v.parts[0].Modules[1]).StateName = "Extended"),
                    Step("A-extended-again", null),
                    Step("A-extending", v => ((FieldStubModule)v.parts[0].Modules[1]).StateName = "Extending"),
                    Step("A-retracted", v => ((FieldStubModule)v.parts[0].Modules[1]).StateName = "Retracted"),
                },
                "deployedLadders", "loggedLadderClassificationMisses");
        }

        [Fact]
        public void AnimationGroup()
        {
            RunFamily("animation-group", "CheckAnimationGroupState",
                ModuleNameFamily("ModuleAnimationGroup"),
                DeployedFlagSteps(),
                "deployedAnimationGroups", "loggedAnimationGroupClassificationMisses");
        }

        [Fact]
        public void AnimationGroup_MissKeyCarriesTheModuleIndex()
        {
            // A classification miss on a part's SECOND module: the once-per-module log key is
            // (pid, module index), so a later module on the same part is keyed apart.
            RunFamily("animation-group-miss-key", "CheckAnimationGroupState",
                () => MakeVessel(LiveVesselPid,
                    MakePart(951, "groupMissSecond", null, FieldStub("ModuleAnimationGroup", withFields: false))),
                new[] { Step("initial", null), Step("again", null) },
                "deployedAnimationGroups", "loggedAnimationGroupClassificationMisses");
        }

        [Fact]
        public void AeroSurface()
        {
            RunFamily("aero-surface", "CheckAeroSurfaceState",
                ModuleNameFamily("ModuleAeroSurface"),
                DeployedFlagSteps(),
                "deployedAeroSurfaceModules", "loggedAeroSurfaceClassificationMisses");
        }

        [Fact]
        public void ControlSurface()
        {
            RunFamily("control-surface", "CheckControlSurfaceState",
                ModuleNameFamily("ModuleControlSurface"),
                DeployedFlagSteps(),
                "deployedControlSurfaceModules", "loggedControlSurfaceClassificationMisses");
        }

        [Fact]
        public void RobotArmScanner()
        {
            RunFamily("robot-arm-scanner", "CheckRobotArmScannerState",
                ModuleNameFamily("ModuleRobotArmScanner"),
                DeployedFlagSteps(),
                "deployedRobotArmScannerModules", "loggedRobotArmScannerClassificationMisses");
        }

        [Fact]
        public void AnimateHeat()
        {
            RunFamily("animate-heat", "CheckAnimateHeatState",
                () =>
                {
                    var h1 = Module<ModuleAnimateHeat>();
                    h1.inputState = 0f;
                    var miss = FieldStub("ModuleAnimateHeat", withFields: false);
                    var h3 = Module<ModuleAnimateHeat>();
                    h3.inputState = 0.9f;
                    return MakeVessel(LiveVesselPid,
                        MakePart(1001, "heatA", null, h1),
                        MakePart(1002, "heatMiss", miss),
                        null,
                        MakePart(1003, "heatHot", h3),
                        MakePart(1004, "heatOtherName", Module<ModuleAnimateHeat>("ModuleAnimateHeatX")));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-medium", v => ((ModuleAnimateHeat)v.parts[0].Modules[1]).inputState = 0.5f),
                    Step("A-hot", v => ((ModuleAnimateHeat)v.parts[0].Modules[1]).inputState = 0.85f),
                    Step("A-gap-stays-hot", v => ((ModuleAnimateHeat)v.parts[0].Modules[1]).inputState = 0.77f),
                    Step("A-cold", v => ((ModuleAnimateHeat)v.parts[0].Modules[1]).inputState = 0.1f),
                },
                "animateHeatLevels", "loggedAnimateHeatClassificationMisses");
        }

        [Fact]
        public void AnimateHeat_MissKeyCarriesTheModuleIndex()
        {
            RunFamily("animate-heat-miss-key", "CheckAnimateHeatState",
                () => MakeVessel(LiveVesselPid,
                    MakePart(1051, "heatMissSecond", null, FieldStub("ModuleAnimateHeat", withFields: false))),
                new[] { Step("initial", null), Step("again", null) },
                "animateHeatLevels", "loggedAnimateHeatClassificationMisses");
        }

        [Fact]
        public void AnimateGeneric()
        {
            RunFamily("animate-generic", "CheckAnimateGenericState",
                () =>
                {
                    var g1 = Module<ModuleAnimateGeneric>();
                    g1.animationName = "doors";
                    g1.animTime = 0f;
                    var g2 = Module<ModuleAnimateGeneric>();
                    g2.animationName = "";
                    g2.animTime = 1f;
                    var g3 = Module<ModuleAnimateGeneric>();
                    g3.animationName = "bay";
                    g3.animTime = 1f;
                    var g4 = Module<ModuleAnimateGeneric>();
                    g4.animationName = "grp";
                    g4.animTime = 1f;
                    var g5 = Module<ModuleAnimateGeneric>();
                    g5.animationName = "gear";
                    g5.animTime = 1f;
                    var g6 = Module<ModuleAnimateGeneric>();
                    g6.animationName = "solar";
                    g6.animTime = 1f;
                    var g7a = Module<ModuleAnimateGeneric>();
                    g7a.animationName = "light";
                    g7a.animTime = 1f;
                    var g7b = Module<ModuleAnimateGeneric>();
                    g7b.animationName = "light2";
                    g7b.animTime = 0f;
                    var g8 = Module<ModuleAnimateGeneric>();
                    g8.animationName = "heat";
                    g8.animTime = 1f;
                    return MakeVessel(LiveVesselPid,
                        MakePart(1101, "genericA", null, g1),
                        MakePart(1102, "genericNoAnimName", g2),
                        MakePart(1103, "genericOnCargoBay", Module<ModuleCargoBay>(), g3),
                        MakePart(1104, "genericOnAnimGroup", g4, FieldStub("ModuleAnimationGroup", withFields: false)),
                        MakePart(1105, "genericOnGear", Module<ModuleWheels.ModuleWheelDeployment>(), g5),
                        MakePart(1106, "genericOnPanel", g6, Module<ModuleDeployableSolarPanel>()),
                        null,
                        MakePart(1107, "genericTwoModules", g7a, g7b),
                        MakePart(1108, "genericOnAnimateHeat", g8, Module<ModuleAnimateHeat>()));
                },
                new[]
                {
                    Step("initial", null),
                    Step("A-open", v => ((ModuleAnimateGeneric)v.parts[0].Modules[1]).animTime = 1f),
                    Step("A-mid-unclassified", v => ((ModuleAnimateGeneric)v.parts[0].Modules[1]).animTime = 0.5f),
                    Step("A-mid-again", null),
                    Step("A-closed", v => ((ModuleAnimateGeneric)v.parts[0].Modules[1]).animTime = 0f),
                },
                "deployedAnimateGenericModules", "loggedAnimateGenericClassificationMisses");
        }
    }
}
