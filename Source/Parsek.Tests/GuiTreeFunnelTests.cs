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
        /// game. Checked here instead: for each patch class, every declared parameter that
        /// is not a Harmony special (<c>__instance</c>, <c>__result</c>, <c>__state</c>,
        /// <c>___field</c>) must exist on the target method with an assignable type.
        /// </summary>
        [Fact]
        public void PatchParameterNamesAndTypesMatchTheirTargets()
        {
            Assembly parsek = typeof(GuiTreeFunnels).Assembly;
            Type patchAttribute = typeof(HarmonyLib.HarmonyPatch);
            int classesChecked = 0;
            int parametersChecked = 0;

            foreach (Type type in parsek.GetTypes())
            {
                if (type.Namespace != "Parsek.Patches")
                    continue;
                if (!type.Name.StartsWith("GuiTree", StringComparison.Ordinal))
                    continue;
                if (type.GetCustomAttributes(patchAttribute, false).Length == 0)
                    continue;

                MethodInfo targetResolver = type.GetMethod("TargetMethod",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(targetResolver);
                var target = (MethodBase)targetResolver.Invoke(null, null);
                Assert.NotNull(target);
                classesChecked++;

                var targetParameters = new Dictionary<string, Type>();
                foreach (ParameterInfo p in target.GetParameters())
                    targetParameters[p.Name] = p.ParameterType;

                foreach (string hookName in new[] { "Prefix", "Postfix" })
                {
                    MethodInfo hook = type.GetMethod(hookName,
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (hook == null)
                        continue;
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
            }

            // One class per funnel, so a patch class silently dropped from the file reds
            // here rather than showing up as a missing control kind in a dump.
            Assert.Equal(GuiTreeFunnels.Count, classesChecked);
            Assert.True(parametersChecked >= 40,
                "expected the patch bodies to bind at least 40 named parameters, saw "
                + parametersChecked);
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
