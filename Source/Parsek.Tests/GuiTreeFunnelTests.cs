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
            // capture. The live path reads Event.current; the DECISION is pure so it can
            // be pinned without a Unity GUI pass.
            Assert.Equal(expected, GuiTreeRecorder.ClassifyArmRefusal(insideGuiPass));
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
}
