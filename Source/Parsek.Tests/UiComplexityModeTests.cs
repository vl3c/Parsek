using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // Pure-decision tests for the Basic / Advanced UI complexity core (design
    // docs/dev/design-ui-basic-advanced.md sections 4, 6.1, 7.3, 13.1). Every test
    // names the regression it catches. No shared static state is touched, so no
    // [Collection("Sequential")] is needed.
    public class UiComplexityModeTests
    {
        private static UiSurface[] AllSurfaces()
        {
            return (UiSurface[])Enum.GetValues(typeof(UiSurface));
        }

        // --- IsVisible (design 6.1) ---

        // Advanced must stay byte-identical to today's UI (philosophy 6) for every
        // surface that has not been deliberately RETIRED. Walked by reflection over the
        // enum so a new surface that defaults to hidden in Advanced fails here rather
        // than silently disappearing for every player; a surface can only leave this
        // walk by joining the explicit retired set pinned below.
        [Fact]
        public void AdvancedShowsEveryNonRetiredSurface()
        {
            foreach (UiSurface surface in AllSurfaces())
            {
                if (UiSurfaceVisibility.IsRetired(surface))
                    continue;
                Assert.True(
                    UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Advanced),
                    $"{surface} must be visible in Advanced mode");
            }
        }

        // Pins the retired set EXACTLY (the Gloops wind-down toward a standalone mod):
        // a retired surface is hidden in BOTH modes, and nothing joins or leaves the
        // set silently. Retirement outranking the mode is the contract that lets the
        // Gloops launcher disappear from Advanced without touching philosophy 6 for
        // every other surface.
        [Fact]
        public void RetiredSurfacesAreHiddenInEveryMode()
        {
            var expectedRetired = new HashSet<UiSurface> { UiSurface.MainButtonGloops };

            var actualRetired = new HashSet<UiSurface>(
                AllSurfaces().Where(UiSurfaceVisibility.IsRetired));
            Assert.True(expectedRetired.SetEquals(actualRetired),
                "Retired set drifted. Expected: " +
                string.Join(", ", expectedRetired.OrderBy(s => s.ToString())) + "; actual: " +
                string.Join(", ", actualRetired.OrderBy(s => s.ToString())));

            foreach (UiSurface surface in actualRetired)
            {
                Assert.False(
                    UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Basic),
                    $"{surface} is retired and must be hidden in Basic");
                Assert.False(
                    UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Advanced),
                    $"{surface} is retired and must be hidden in Advanced");
            }
        }

        // Pins the section 4 hide-set exactly. Fails on silent scope drift in EITHER
        // direction: a surface quietly added to Basic's hidden set, or one quietly
        // dropped from it.
        [Fact]
        public void BasicHidesExactlyTheDocumentedSet()
        {
            var expected = new HashSet<UiSurface>
            {
                UiSurface.MainButtonSpawnControl,
                UiSurface.MainButtonKerbals,
                UiSurface.MainButtonCareer,
                UiSurface.MainButtonGloops,
                UiSurface.TabRecordings,
                UiSurface.MissionsLoopControls,
                UiSurface.SettingsSectionLooping,
                UiSurface.SettingsSectionDiagnostics,
                UiSurface.SettingsSectionSampleDensity,
            };

            var actual = new HashSet<UiSurface>(UiSurfaceVisibility.HiddenSurfaces(UiComplexityMode.Basic));

            Assert.Equal(9, actual.Count);
            Assert.True(expected.SetEquals(actual),
                "Basic hide-set drifted from design section 4. Expected: " +
                string.Join(", ", expected.OrderBy(s => s.ToString())) + "; actual: " +
                string.Join(", ", actual.OrderBy(s => s.ToString())));
        }

        // Advanced hides exactly the retired set and nothing else, so the close handler
        // of design 7.2 still has nothing MODE-DRIVEN to do on a Basic -> Advanced
        // switch (a retired window is unreachable in both modes, not newly hidden by
        // the transition).
        [Fact]
        public void AdvancedHidesOnlyRetiredSurfaces()
        {
            var hidden = new HashSet<UiSurface>(
                UiSurfaceVisibility.HiddenSurfaces(UiComplexityMode.Advanced));
            var retired = new HashSet<UiSurface>(
                AllSurfaces().Where(UiSurfaceVisibility.IsRetired));
            Assert.True(retired.SetEquals(hidden),
                "Advanced must hide exactly the retired surfaces. Retired: " +
                string.Join(", ", retired.OrderBy(s => s.ToString())) + "; hidden: " +
                string.Join(", ", hidden.OrderBy(s => s.ToString())));
        }

        // The philosophy-3 guard: Basic must be sufficient, not merely smaller. If any
        // of these five goes dark, the core loop (fly -> commit -> loop / watch ->
        // rewind -> supply) is no longer completable without switching to Advanced.
        // Surfaces are named as strings here because UiSurface is internal and an
        // xUnit theory method must be public.
        [Theory]
        [InlineData("MainButtonTimeline")]
        [InlineData("MainButtonRecordings")] // the Missions launcher (design 4.1)
        [InlineData("MainButtonLogistics")]
        [InlineData("MainButtonSettings")]
        [InlineData("TabMissions")]
        public void BasicKeepsCoreLoopSurfaces(string surfaceName)
        {
            var surface = (UiSurface)Enum.Parse(typeof(UiSurface), surfaceName);
            Assert.True(
                UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Basic),
                $"{surface} is a core-loop surface and must stay visible in Basic");
        }

        // The documented no-silent-default contract (design 6.1): an unhandled
        // UiSurface value throws in BOTH modes, so adding a surface without a Basic
        // decision reds the suite instead of shipping an accidental show or hide.
        [Fact]
        public void EverySurfaceIsDecided()
        {
            UiSurface[] all = AllSurfaces();
            var undecided = (UiSurface)(-1);
            Assert.DoesNotContain(undecided, all);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => UiSurfaceVisibility.IsVisible(undecided, UiComplexityMode.Basic));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => UiSurfaceVisibility.IsVisible(undecided, UiComplexityMode.Advanced));

            // An out-of-range value ABOVE the declared members must throw too, so a
            // future member appended to the enum is genuinely covered by the walk.
            var pastEnd = (UiSurface)(all.Select(s => (int)s).Max() + 1);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => UiSurfaceVisibility.IsVisible(pastEnd, UiComplexityMode.Basic));

            // Every real value answers without throwing, in both modes.
            foreach (UiSurface surface in all)
            {
                UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Basic);
                UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Advanced);
            }
        }

        // --- ResolveMode (design 7.3) ---

        // With no stored value, the install footprint alone decides: an existing
        // install keeps every window it used, a genuinely new one starts Basic.
        // Expected modes are passed as their persisted ints (0 = Basic, 1 = Advanced)
        // because UiComplexityMode is internal and an xUnit theory method must be public.
        [Theory]
        [InlineData(true, 1)]  // existing install -> Advanced
        [InlineData(false, 0)] // new install -> Basic
        public void ResolveModeUsesFootprintOnlyWhenNoStoredValue(
            bool installHasParsekFootprint, int expectedModeValue)
        {
            Assert.Equal(
                (UiComplexityMode)expectedModeValue,
                UiSurfaceVisibility.ResolveMode(null, installHasParsekFootprint));
        }

        // The stored preference wins unconditionally over the footprint, across all
        // four crossings including stored-Advanced-with-no-footprint and
        // stored-Basic-with-footprint. Genuinely falsifiable because the stored value
        // is a seam INPUT (design 7.3): it fails if footprint logic is ever allowed to
        // override a saved preference.
        [Theory]
        [InlineData(0, true)]  // stored Basic wins over an existing-install footprint
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(1, false)] // stored Advanced wins with no footprint at all
        public void StoredValueAlwaysWinsOverFootprint(int storedValue, bool installHasParsekFootprint)
        {
            Assert.Equal(
                (UiComplexityMode)storedValue,
                UiSurfaceVisibility.ResolveMode(storedValue, installHasParsekFootprint));
        }

        // Fail-open clamp (design 6.2): a corrupt or future stored int resolves to
        // Advanced, because showing everything is the safe wrong answer and hiding
        // windows is not. Still a stored value, so the footprint stays irrelevant.
        [Theory]
        [InlineData(7, true)]
        [InlineData(7, false)]
        [InlineData(-3, true)]
        [InlineData(-3, false)]
        [InlineData(int.MaxValue, false)]
        public void OutOfRangeStoredValueResolvesToAdvanced(int storedValue, bool installHasParsekFootprint)
        {
            Assert.Equal(
                UiComplexityMode.Advanced,
                UiSurfaceVisibility.ResolveMode(storedValue, installHasParsekFootprint));
            Assert.Equal(UiComplexityMode.Advanced, UiSurfaceVisibility.FromStoredInt(storedValue));
        }

        // The persisted enum values are a serialization contract (design 6.1): the
        // stored int must keep meaning the same mode across versions.
        [Fact]
        public void PersistedEnumValuesAreStable()
        {
            Assert.Equal(0, (int)UiComplexityMode.Basic);
            Assert.Equal(1, (int)UiComplexityMode.Advanced);
        }
    }
}
