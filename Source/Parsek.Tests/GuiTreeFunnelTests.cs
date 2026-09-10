using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Mechanical proof that every IMGUI funnel the GUI-tree recorder patches actually
    /// EXISTS with the signature the patches assume, resolved by reflection against the
    /// UnityEngine.IMGUIModule the mod builds against.
    ///
    /// <para><b>Why this is worth a test.</b> The whole capture is Harmony interceptions of
    /// private and internal UnityEngine methods. Those are not API: a Unity or KSP bump
    /// can rename or re-sign any of them, and the failure mode is a silent one - Harmony
    /// logs "failed to apply patch" at startup and the dump simply comes back missing a
    /// control kind, hours later and inside a game nobody is watching. This cell turns
    /// that into a red build in the suite, and it is the only part of the interception
    /// layer that can be checked without launching KSP.</para>
    ///
    /// <para>It also pins the paired requirement that a Harmony prefix cannot express:
    /// each patch's parameter names must match the target method's, or Harmony throws at
    /// patch time.</para>
    /// </summary>
    public class GuiTreeFunnelTests
    {
        // Cases pass the funnel as its int value: the theory method has to be public
        // for xUnit and GuiFunnel is internal to Parsek.
        private static IEnumerable<object[]> AllFunnels()
        {
            foreach (GuiFunnel f in Enum.GetValues(typeof(GuiFunnel)))
                yield return new object[] { (int)f };
        }

        public static IEnumerable<object[]> FunnelCases = AllFunnels();

        [Theory]
        [MemberData(nameof(FunnelCases))]
        public void EveryFunnelResolvesAgainstTheShippedIMGUIModule(int funnelIndex)
        {
            var funnel = (GuiFunnel)funnelIndex;
            MethodInfo target = GuiTreeFunnels.Target(funnel);
            Assert.NotNull(target);
            Assert.True(target.IsStatic,
                GuiTreeFunnels.Name(funnel) + " must be static; a Harmony patch on an "
                + "instance method needs an __instance parameter the patches do not declare");
        }

        [Theory]
        [MemberData(nameof(FunnelCases))]
        public void NoFunnelIsAnUnpatchableInternalCall(int funnelIndex)
        {
            var funnel = (GuiFunnel)funnelIndex;
            MethodInfo target = GuiTreeFunnels.Target(funnel);
            Assert.NotNull(target);
            // Harmony cannot patch an ICall / extern: there is no IL body to rewrite.
            // The whole reason the recorder targets these particular methods is that they
            // are the deepest MANAGED layer above the externs.
            Assert.False((target.GetMethodImplementationFlags()
                & MethodImplAttributes.InternalCall) != 0,
                GuiTreeFunnels.Name(funnel) + " resolved to an InternalCall and cannot be patched");
            Assert.False((target.Attributes & MethodAttributes.PinvokeImpl) != 0,
                GuiTreeFunnels.Name(funnel) + " resolved to a PInvoke and cannot be patched");
        }

        [Theory]
        [MemberData(nameof(FunnelCases))]
        public void EveryFunnelHasADistinctPinnedWireName(int funnelIndex)
        {
            var funnel = (GuiFunnel)funnelIndex;
            Assert.NotEqual("unknown", GuiTreeFunnels.Name(funnel));
        }

        [Fact]
        public void WireNamesAreUniqueSoTheFunnelReportIsUnambiguous()
        {
            var seen = new HashSet<string>();
            foreach (GuiFunnel f in Enum.GetValues(typeof(GuiFunnel)))
                Assert.True(seen.Add(GuiTreeFunnels.Name(f)), "duplicate funnel name: " + GuiTreeFunnels.Name(f));
        }

        [Fact]
        public void FunnelCountMatchesTheEnumSoHitsIsFullyAddressable()
        {
            Assert.Equal(GuiTreeFunnels.Count, Enum.GetValues(typeof(GuiFunnel)).Length);
            Assert.Equal(GuiTreeFunnels.Count, GuiTreeFunnels.Hits.Length);
        }

        /// <summary>
        /// Every patch class in <c>Patches/GuiTreeRecorderPatches.cs</c> declares its
        /// prefix / postfix parameters by NAME, and Harmony matches those names against
        /// the target method's. A rename on either side throws at patch time inside the
        /// game. Checked here instead: for each patch class in the applier's own table,
        /// every declared parameter that is not a Harmony special (<c>__instance</c>,
        /// <c>__result</c>, <c>__state</c>, <c>___field</c>) must exist on the target
        /// method with an assignable type.
        ///
        /// <para>Discovery is <c>GuiTreeRecorderPatches.All</c>, not the
        /// <c>[HarmonyPatch]</c> attribute: these classes deliberately carry no attribute
        /// (see <see cref="NoGuiTreePatchClassIsDiscoverableByTheAssemblySweep"/>), and
        /// that table is now the only thing that applies them.</para>
        /// </summary>
        [Fact]
        public void PatchParameterNamesAndTypesMatchTheirTargets()
        {
            int classesChecked = 0;
            int parametersChecked = 0;
            var funnelsSeen = new HashSet<GuiFunnel>();

            foreach (var row in Parsek.Patches.GuiTreeRecorderPatches.All)
            {
                GuiFunnel funnel = row.Item1;
                Type type = row.Item2;
                Assert.True(funnelsSeen.Add(funnel),
                    "funnel " + GuiTreeFunnels.Name(funnel) + " is listed twice in "
                    + "GuiTreeRecorderPatches.All");

                MethodInfo targetResolver = type.GetMethod("TargetMethod",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(targetResolver);
                var target = (MethodBase)targetResolver.Invoke(null, null);
                Assert.NotNull(target);
                // The class's own TargetMethod and the table's funnel must name the SAME
                // method, or the applier patches one thing while the report describes
                // another.
                Assert.Equal((MethodBase)GuiTreeFunnels.Target(funnel), target);
                classesChecked++;

                var targetParameters = new Dictionary<string, Type>();
                foreach (ParameterInfo p in target.GetParameters())
                    targetParameters[p.Name] = p.ParameterType;

                bool hasHook = false;
                foreach (string hookName in new[] { "Prefix", "Postfix" })
                {
                    MethodInfo hook = type.GetMethod(hookName,
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (hook == null)
                        continue;
                    hasHook = true;
                    foreach (ParameterInfo p in hook.GetParameters())
                    {
                        if (p.Name.StartsWith("__", StringComparison.Ordinal))
                            continue;
                        Assert.True(targetParameters.ContainsKey(p.Name),
                            type.Name + "." + hookName + " declares parameter '" + p.Name
                            + "' which " + target.DeclaringType.Name + "." + target.Name
                            + " does not have; Harmony would throw at patch time");
                        Assert.True(p.ParameterType == targetParameters[p.Name],
                            type.Name + "." + hookName + " declares '" + p.Name + "' as "
                            + p.ParameterType.Name + " but the target declares "
                            + targetParameters[p.Name].Name);
                        parametersChecked++;
                    }
                }
                Assert.True(hasHook, type.Name + " declares neither a Prefix nor a Postfix");
            }

            // One class per funnel, so a patch class silently dropped from the table reds
            // here rather than showing up as a missing control kind in a dump.
            Assert.Equal(GuiTreeFunnels.Count, classesChecked);
            Assert.True(parametersChecked >= 35,
                "expected the patch bodies to bind at least 35 named parameters, saw "
                + parametersChecked);
        }

        /// <summary>
        /// THE opt-in gate. <c>ParsekHarmony.Awake</c> discovers patch classes by
        /// <c>t.GetCustomAttributes(typeof(HarmonyPatch), false).Length &gt; 0</c> and
        /// applies every one it finds, permanently. A <c>[HarmonyPatch]</c> attribute
        /// anywhere in the GUI-tree patch file would therefore put a Harmony detour on
        /// <c>GUI.DoLabel</c> and friends for the whole session - a cost paid by every
        /// IMGUI consumer in the process, on every control of every event pass, for a
        /// recorder that is armed for single frames minutes apart. This cell re-runs the
        /// sweep's OWN predicate over the assembly and requires it to find none of them.
        /// </summary>
        [Fact]
        public void NoGuiTreePatchClassIsDiscoverableByTheAssemblySweep()
        {
            Assembly parsek = typeof(GuiTreeFunnels).Assembly;
            Type patchAttribute = typeof(HarmonyLib.HarmonyPatch);
            var offenders = new List<string>();

            foreach (Type type in parsek.GetTypes())
            {
                if (!type.Name.StartsWith("GuiTree", StringComparison.Ordinal))
                    continue;
                // Exactly ParsekHarmony.Awake's predicate.
                if (type.GetCustomAttributes(patchAttribute, false).Length > 0)
                    offenders.Add(type.FullName);
            }

            Assert.True(offenders.Count == 0,
                "these GUI-tree types carry [HarmonyPatch], so ParsekHarmony's sweep would "
                + "apply them permanently at Awake instead of leaving them to "
                + "GuiTreeRecorderPatches.Apply(): " + string.Join(", ", offenders.ToArray()));
        }

        /// <summary>
        /// The applier's table must cover every funnel and nothing else, because the
        /// arm-time <c>patched</c> report is derived from the funnel enum while the
        /// patching is driven from the table. A funnel present in one and absent from the
        /// other reads as "signature drift" in a dump and is nothing of the sort.
        /// </summary>
        [Fact]
        public void TheApplierTableCoversExactlyTheFunnelEnum()
        {
            var tabled = new HashSet<GuiFunnel>();
            foreach (var row in Parsek.Patches.GuiTreeRecorderPatches.All)
                tabled.Add(row.Item1);

            foreach (GuiFunnel f in Enum.GetValues(typeof(GuiFunnel)))
            {
                Assert.True(tabled.Contains(f),
                    "funnel " + GuiTreeFunnels.Name(f) + " has no patch class in "
                    + "GuiTreeRecorderPatches.All, so nothing will ever intercept it");
            }
            Assert.Equal(GuiTreeFunnels.Count, tabled.Count);
            Assert.Equal(GuiTreeFunnels.Count, GuiTreeFunnels.PatchedAtArm.Length);
        }

        /// <summary>
        /// The inside-OnGUI guard's member, resolved against the shipped
        /// <c>UnityEngine.IMGUIModule</c> with the EXACT declared signature the recorder
        /// binds a <c>Func&lt;int&gt;</c> from: <c>internal static extern int
        /// GUIUtility.guiDepth { get; }</c>. Same reason as the funnel cells above - it is
        /// not API, a Unity bump can rename it, and the failure mode is silent (the guard
        /// falls back and says so in one Warn nobody is reading).
        ///
        /// <para>This is the member that REPLACED <c>Event.current != null</c>, whose
        /// getter is <c>return s_Current;</c> with no depth gating and which is therefore
        /// never null again once the process has drawn a frame - so the old guard refused
        /// every arm. <c>guiDepth</c> is what <c>GUIUtility.CheckOnGUI</c> itself tests
        /// (<c>guiDepth &lt;= 0</c> throws "You can only call GUI functions from inside
        /// OnGUI").</para>
        ///
        /// <para>RESOLUTION is all a headless host can check. The getter is an ICall, so
        /// CALLING it here raises <c>SecurityException</c> on the Windows CLR (and a
        /// <c>MissingMethodException</c> under mono) - which is the fallback path, pinned
        /// purely by
        /// <see cref="GuiDepthDecidesInsideAGuiPassAndAnUnreadableDepthFallsBackToOutside"/>
        /// rather than exercised here.</para>
        /// </summary>
        [Fact]
        public void TheGuiDepthGuardMemberResolvesWithItsDeclaredSignature()
        {
            MethodInfo getter = GuiTreeFunnels.GuiDepthGetter();
            Assert.NotNull(getter);
            Assert.Equal(typeof(UnityEngine.GUIUtility), getter.DeclaringType);
            Assert.Equal("get_guiDepth", getter.Name);
            Assert.True(getter.IsStatic,
                "GUIUtility.guiDepth must be static; the recorder invokes it with a null "
                + "target");
            Assert.Equal(typeof(int), getter.ReturnType);
            Assert.Empty(getter.GetParameters());
        }

        /// <summary>
        /// It is an ICall: invokable by reflection, never patchable, and never callable
        /// from a headless host. That last part is the whole reason the guard needs a
        /// STATED fallback rather than a bare try/catch, and it is also why the recorder
        /// calls this one through <c>MethodInfo.Invoke</c> instead of a bound delegate -
        /// <c>Delegate.CreateDelegate</c> over an ECall is refused outside the declaring
        /// module ("ECall methods must be packaged into a system module").
        /// </summary>
        [Fact]
        public void TheGuiDepthGuardMemberIsAnInternalCall()
        {
            MethodInfo getter = GuiTreeFunnels.GuiDepthGetter();
            Assert.NotNull(getter);
            Assert.True((getter.GetMethodImplementationFlags()
                & MethodImplAttributes.InternalCall) != 0,
                "GUIUtility.guiDepth's getter is expected to be an InternalCall; if it "
                + "gained a managed body the fallback reasoning needs re-reading");
        }

        /// <summary>
        /// The two layout-group funnels are the ones that replaced four inline-prone
        /// patches, so their identity is pinned by name here: a future edit that quietly
        /// points them back at <c>GUILayout.BeginHorizontal</c> / <c>EndHorizontal</c>
        /// (8 bytes of IL, well inside Mono's inline limit) would restore the very failure
        /// mode the swap removed, and every other cell in this file would still pass.
        /// </summary>
        [Fact]
        public void TheLayoutGroupFunnelsAreTheLargeGUILayoutUtilityPair()
        {
            MethodInfo begin = GuiTreeFunnels.Target(GuiFunnel.BeginLayoutGroup);
            MethodInfo end = GuiTreeFunnels.Target(GuiFunnel.EndLayoutGroup);
            Assert.NotNull(begin);
            Assert.NotNull(end);
            Assert.Equal(typeof(UnityEngine.GUILayoutUtility), begin.DeclaringType);
            Assert.Equal(typeof(UnityEngine.GUILayoutUtility), end.DeclaringType);
            Assert.Equal("BeginLayoutGroup", begin.Name);
            Assert.Equal("EndLayoutGroup", end.Name);
            // BeginLayoutGroup's third parameter is the layout TYPE, which is the only
            // thing that tells a real BeginHorizontal / BeginVertical from the carrier
            // GUILayout.BeginScrollView opens for its GUIScrollGroup.
            Assert.Equal(3, begin.GetParameters().Length);
            Assert.Equal(typeof(Type), begin.GetParameters()[2].ParameterType);
            Assert.Empty(end.GetParameters());

            // And the four funnels it replaced are gone, so nothing patches an 8-byte End.
            foreach (GuiFunnel f in Enum.GetValues(typeof(GuiFunnel)))
            {
                string name = GuiTreeFunnels.Name(f);
                Assert.False(name == "GUILayout.BeginHorizontal" || name == "GUILayout.EndHorizontal"
                    || name == "GUILayout.BeginVertical" || name == "GUILayout.EndVertical",
                    name + " is patched again; those Ends are 8 bytes of IL and Mono "
                    + "inlines them regardless of Harmony");
            }
        }
    }

    /// <summary>Label sanitising for the dump filename.</summary>
    public class GuiTreeRecorderLabelTests
    {
        [Theory]
        [InlineData("guitree-probe", "guitree-probe")]
        [InlineData("H99_run_2026-09-10", "H99_run_2026-09-10")]
        [InlineData("with space", "with_space")]
        [InlineData("../../evil", "evil")]
        [InlineData("a/b\\c", "a_b_c")]
        [InlineData("", "guitree")]
        [InlineData(null, "guitree")]
        [InlineData("...", "guitree")]
        [InlineData("____", "guitree")]
        public void LabelsBecomeSafeFilenames(string input, string expected)
        {
            Assert.Equal(expected, GuiTreeRecorder.SanitizeLabel(input));
        }

        [Fact]
        public void OverlongLabelsAreTruncated()
        {
            string result = GuiTreeRecorder.SanitizeLabel(new string('a', 400));
            Assert.Equal(96, result.Length);
        }

        [Theory]
        [InlineData(true, "inside-gui-pass")]
        [InlineData(false, null)]
        public void ArmingIsRefusedFromInsideAGuiPass(bool insideGuiPass, string expected)
        {
            // Arming installs Harmony patches on the very IMGUI methods an in-progress
            // pass is executing, and a pass already half-drawn would give a truncated
            // capture. The live path reads GUIUtility.guiDepth; the DECISION is pure so it
            // can be pinned without a Unity GUI pass.
            Assert.Equal(expected, GuiTreeRecorder.ClassifyArmRefusal(insideGuiPass));
        }

        /// <summary>
        /// The guard predicate and its FALLBACK, both pure.
        ///
        /// <para><c>0</c> is the real "outside OnGUI" reading (the pump's LateUpdate, an
        /// Update, a coroutine, the command seam). Anything positive is a live pass -
        /// <c>GUIUtility.CheckOnGUI</c> uses the same threshold. And the negative sentinel,
        /// which the recorder returns when the probe did not resolve or the ICall threw,
        /// counts as NOT inside: an unreadable depth must let an arm proceed, because
        /// refusing on it is exactly the always-refuse behaviour the
        /// <c>Event.current != null</c> guard had, and both call sites are outside the GUI
        /// pass by construction anyway.</para>
        /// </summary>
        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        [InlineData(-7, false)]
        public void GuiDepthDecidesInsideAGuiPassAndAnUnreadableDepthFallsBackToOutside(
            int reading, bool expected)
        {
            Assert.Equal(expected, GuiTreeRecorder.ClassifyInsideGuiPass(reading));
        }

        [Fact]
        public void TheUnavailableGuiDepthSentinelIsOnTheFallbackSideOfThePredicate()
        {
            Assert.True(GuiTreeRecorder.GuiDepthUnavailable < 0);
            Assert.False(GuiTreeRecorder.ClassifyInsideGuiPass(
                GuiTreeRecorder.GuiDepthUnavailable));
        }

        [Fact]
        public void TheOutputSuffixAndDirectoryAreThePinnedHarvestContract()
        {
            // The harness harvests the KSP root's Screenshots/ directory by mtime, and the
            // offline viewer globs *.gui.json. Both are contracts, not preferences.
            Assert.Equal("Screenshots", GuiTreeRecorder.OutputDirectoryName);
            Assert.Equal(".gui.json", GuiTreeRecorder.OutputSuffix);
        }
    }

    /// <summary>
    /// The armed-with-no-Repaint give-up. Nothing else disarms an arm whose Repaint never
    /// arrives, and the recorder's entire cost story is that the Harmony detours come OFF
    /// again - so without a bound they would sit on all 17 IMGUI funnels for the rest of
    /// the process.
    /// </summary>
    public class GuiTreeArmTimeoutTests
    {
        [Theory]
        [InlineData(0, 300, false)]
        [InlineData(299, 300, false)]
        [InlineData(300, 300, true)]
        [InlineData(1000, 300, true)]
        public void TheGiveUpTripsAtTheBudgetAndNotBefore(int framesSinceArm, int budget,
            bool expected)
        {
            Assert.Equal(expected, GuiTreeRecorder.ClassifyArmTimeout(framesSinceArm, budget));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-4242)]
        public void AnUnreadableFrameClockIsNotATimeout(int framesSinceArm)
        {
            // Time.frameCount is a Unity ICall, so a headless host cannot read it. Giving
            // up on an unreadable clock would disarm every capture on such a host before
            // it recorded anything - the same always-refuse failure the
            // Event.current != null guard had.
            Assert.False(GuiTreeRecorder.ClassifyArmTimeout(framesSinceArm,
                GuiTreeRecorder.ArmTimeoutFrames));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ANonPositiveBudgetDisablesTheGiveUpOutright(int budget)
        {
            Assert.False(GuiTreeRecorder.ClassifyArmTimeout(1000000, budget));
        }

        [Fact]
        public void TheBudgetIsWellPastTheLiveCellsOwnWaitAfterArming()
        {
            // GuiTreeDumpImguiTest waits up to 300 frames for the capture to be written.
            // A give-up inside that wait would disarm a capture the caller is legitimately
            // waiting for, and turn a healthy run into "the recorder never completed a
            // capture".
            Assert.True(GuiTreeRecorder.ArmTimeoutFrames > 300,
                "ArmTimeoutFrames is " + GuiTreeRecorder.ArmTimeoutFrames
                + ", which is not clear of the live cell's 300-frame wait");
        }
    }

    /// <summary>
    /// The OTHER way an arm can leave the interceptions installed with nothing left to
    /// remove them: it THROWS after <c>GuiTreeRecorderPatches.Apply()</c> has run.
    ///
    /// <para>The give-up above cannot help there, because it runs only while
    /// <c>ArmedFlag</c> is set and the throw window is precisely the region between
    /// <c>Apply()</c> and that assignment - Apply's own tail after it set
    /// <c>Applied</c>, and the funnel readback loop. The recorder therefore disarms
    /// itself on that path (<c>ArmForNextRepaint</c>'s catch, which rethrows), which
    /// covers BOTH callers, and the seam verb repeats the call at its own exit. These
    /// cells pin the state the disarm has to reach and the one reason token both sites
    /// use.</para>
    /// </summary>
    [Collection("Sequential")]
    public class GuiTreeArmThrewDisarmTests : IDisposable
    {
        public GuiTreeArmThrewDisarmTests()
        {
            GuiTreeRecorder.ResetForTesting();
        }

        public void Dispose()
        {
            GuiTreeRecorder.ResetForTesting();
        }

        [Fact]
        public void DisarmingAThrownArmLeavesNothingForThePumpToOwn()
        {
            // The state a throw after Apply() leaves behind, in the harder of its two
            // shapes: the flag already raised, so the recorder counts as having work.
            GuiTreeRecorder.ArmedFlag = true;
            Assert.True(GuiTreeRecorder.HasPendingWork);

            GuiTreeRecorder.Disarm(GuiTreeRecorder.ArmThrewDisarmReason);

            Assert.False(GuiTreeRecorder.ArmedFlag);
            Assert.False(GuiTreeRecorder.HasPendingWork);
            Assert.Equal(GuiTreeRecorder.ArmThrewDisarmReason,
                GuiTreeRecorder.LastDisarmReason);
        }

        [Fact]
        public void TheSeamsRepeatOfTheDisarmIsANoOp()
        {
            // The recorder disarms itself and rethrows; the seam's catch then disarms
            // again. That second call must be inert, which is what lets both sites keep
            // the guarantee without either having to know the other ran.
            GuiTreeRecorder.ArmedFlag = true;
            GuiTreeRecorder.Disarm(GuiTreeRecorder.ArmThrewDisarmReason);
            GuiTreeRecorder.Disarm(GuiTreeRecorder.ArmThrewDisarmReason);

            Assert.False(GuiTreeRecorder.ArmedFlag);
            Assert.False(GuiTreeRecorder.HasPendingWork);
            Assert.Equal(GuiTreeRecorder.ArmThrewDisarmReason,
                GuiTreeRecorder.LastDisarmReason);
        }

        [Fact]
        public void TheReasonTokenIsOneGrepStableSpelling()
        {
            // KSP.log is the instrument here, and the recorder's Error line and the seam's
            // own Error line are read together. Two spellings of one event would split a
            // search that has to find both.
            Assert.Equal("arm-threw", GuiTreeRecorder.ArmThrewDisarmReason);
            Assert.NotEqual("armed-no-repaint", GuiTreeRecorder.ArmThrewDisarmReason);
        }
    }

    /// <summary>
    /// The applier's two decisions. Both directions ARE the defect: Harmony 2.2.1's
    /// <c>PatchInfo.Add</c> does not deduplicate, so a funnel patched twice records every
    /// control twice, and a <c>Remove()</c> whose <c>UnpatchAll</c> threw must NOT report
    /// the process as unpatched.
    /// </summary>
    public class GuiTreePatchApplierDecisionTests
    {
        [Fact]
        public void AFunnelWeAlreadyOwnIsSkippedRatherThanPatchedTwice()
        {
            Assert.Equal(Parsek.Patches.GuiTreeRecorderPatches.FunnelPatchAction.SkipAlreadyOurs,
                Parsek.Patches.GuiTreeRecorderPatches.ClassifyFunnelPatchAction(true, true));
        }

        [Fact]
        public void AFunnelWeDoNotOwnIsPatched()
        {
            Assert.Equal(Parsek.Patches.GuiTreeRecorderPatches.FunnelPatchAction.Patch,
                Parsek.Patches.GuiTreeRecorderPatches.ClassifyFunnelPatchAction(true, false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnUnresolvedTargetIsNeverPatchedWhateverTheOwnershipReadingSays(
            bool alreadyOwnedByUs)
        {
            Assert.Equal(Parsek.Patches.GuiTreeRecorderPatches.FunnelPatchAction.SkipMissingTarget,
                Parsek.Patches.GuiTreeRecorderPatches.ClassifyFunnelPatchAction(
                    false, alreadyOwnedByUs));
        }

        [Fact]
        public void AThrowingUnpatchLeavesTheInterceptionsReportedAsStillInstalled()
        {
            // UnpatchAll may have removed some detours and left others installed.
            // Reporting "not applied" there let the next arm patch every funnel a second
            // time, and every control in the next capture was recorded twice.
            Assert.True(Parsek.Patches.GuiTreeRecorderPatches.RemainsAppliedAfterUnpatch(true));
            Assert.False(Parsek.Patches.GuiTreeRecorderPatches.RemainsAppliedAfterUnpatch(false));
        }
    }

    /// <summary>
    /// The clip-depth probe's binding: delegate-first, Invoke-fallback.
    ///
    /// <para><c>UnityEngine.GUIClip.Internal_GetCount</c> is an ECall, and
    /// <c>Delegate.CreateDelegate</c> over an ECall is refused outside the declaring
    /// module on the Windows CLR ("ECall methods must be packaged into a system module",
    /// which the xUnit host raises for the sibling <c>guiDepth</c> probe); mono may or may
    /// not accept it. A refusal must cost two allocations per recorded control, not the
    /// clip depths of the whole capture.</para>
    /// </summary>
    public class GuiTreeClipProbeBindingTests
    {
        private static class Managed
        {
            internal static int Five()
            {
                return 5;
            }

            // CreateDelegate over this as a Func<int> is refused by the CLR on every host:
            // the signature does not match. MethodInfo.Invoke has no such restriction.
            internal static int Echo(int value)
            {
                return value;
            }
        }

        private static MethodInfo Method(string name)
        {
            return typeof(Managed).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }

        [Fact]
        public void AManagedProbeBindsThroughTheCheapDelegatePath()
        {
            string binding;
            Func<int> probe = GuiTreeRecorder.BindIntProbe(Method("Five"), out binding);

            Assert.Equal(GuiTreeRecorder.ClipProbeBindingDelegate, binding);
            Assert.NotNull(probe);
            Assert.Equal(5, probe());
        }

        [Fact]
        public void TheInvokeWrapperIsAWorkingProbeAndNotJustANonNull()
        {
            Func<int> probe = GuiTreeRecorder.BindIntProbeViaInvoke(Method("Five"));
            Assert.NotNull(probe);
            Assert.Equal(5, probe());
        }

        [Fact]
        public void ARefusedDelegateBindingFallsBackToInvokeRatherThanToNothing()
        {
            // The delegate binder reports a refusal as null instead of throwing...
            Assert.Null(GuiTreeRecorder.BindIntProbeViaDelegate(Method("Echo")));

            // ...and the composed binder then takes the Invoke path rather than giving up.
            string binding;
            Func<int> probe = GuiTreeRecorder.BindIntProbe(Method("Echo"), out binding);
            Assert.Equal(GuiTreeRecorder.ClipProbeBindingInvoke, binding);
            Assert.NotNull(probe);
        }

        [Fact]
        public void AnUnresolvedMemberBindsNothingAndSaysSo()
        {
            string binding;
            Assert.Null(GuiTreeRecorder.BindIntProbe(null, out binding));
            Assert.Equal(GuiTreeRecorder.ClipProbeBindingNone, binding);
            Assert.Null(GuiTreeRecorder.BindIntProbeViaDelegate(null));
            Assert.Null(GuiTreeRecorder.BindIntProbeViaInvoke(null));
        }

        /// <summary>
        /// The real target, on this host. WHICH path binds is host-dependent, which is the
        /// whole reason for the fallback - but something must bind, or every event in a
        /// capture reports <c>clipDepth: -1</c> and the assembler loses its authority for
        /// clip containers.
        /// </summary>
        [Fact]
        public void TheRealClipCountMemberBindsBySomePathOnThisHost()
        {
            Type clip = typeof(UnityEngine.GUI).Assembly.GetType("UnityEngine.GUIClip");
            Assert.NotNull(clip);
            MethodInfo count = clip.GetMethod("Internal_GetCount",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(count);

            string binding;
            Func<int> probe = GuiTreeRecorder.BindIntProbe(count, out binding);

            Assert.NotNull(probe);
            Assert.NotEqual(GuiTreeRecorder.ClipProbeBindingNone, binding);
            // CALLING it needs a Unity runtime, so the binding is all a headless host can
            // settle; the live cell's PASS line prints which path it got.
        }
    }
}
