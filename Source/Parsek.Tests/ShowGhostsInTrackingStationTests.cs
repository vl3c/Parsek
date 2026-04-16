using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the #388 Tracking Station ghost visibility toggle — verifies
    /// the <see cref="ParsekSettings.showGhostsInTrackingStation"/> field is
    /// correctly declared, defaults to visible, and carries the KSP
    /// auto-persistence attribute.
    /// </summary>
    public class ShowGhostsInTrackingStationTests
    {
        [Fact]
        public void Field_DefaultsToTrue()
        {
            var settings = new ParsekSettings();
            Assert.True(settings.showGhostsInTrackingStation,
                "Default should preserve pre-#388 behavior (ghosts visible in TS)");
        }

        [Fact]
        public void Field_HasCustomParameterUiAttribute()
        {
            FieldInfo field = typeof(ParsekSettings)
                .GetField(nameof(ParsekSettings.showGhostsInTrackingStation));

            Assert.NotNull(field);
            var attr = field.GetCustomAttribute<GameParameters.CustomParameterUI>();
            Assert.NotNull(attr);
            // Presence of the attribute is enough — KSP's GameParameters
            // auto-persistence driver iterates fields carrying this attribute
            // and reads/writes them by reflection in the base OnSave/OnLoad.
        }

        [Fact]
        public void Field_IsBoolean()
        {
            FieldInfo field = typeof(ParsekSettings)
                .GetField(nameof(ParsekSettings.showGhostsInTrackingStation));
            Assert.NotNull(field);
            Assert.Equal(typeof(bool), field.FieldType);
        }

        // KSP's GameParameters framework persists public fields with
        // [CustomParameterUI] by reflection in its base OnSave/OnLoad. We can't
        // exercise the framework directly in xUnit (needs HighLogic), but we
        // can pin the two invariants it requires: the field is public, and
        // the attribute is present. If either fails the setting will silently
        // lose its value across save/load.
        [Fact]
        public void Field_IsPublicInstance()
        {
            FieldInfo field = typeof(ParsekSettings)
                .GetField(nameof(ParsekSettings.showGhostsInTrackingStation),
                    BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(field);
        }

        [Fact]
        public void Field_CanBeToggled()
        {
            var settings = new ParsekSettings();
            settings.showGhostsInTrackingStation = false;
            Assert.False(settings.showGhostsInTrackingStation);
            settings.showGhostsInTrackingStation = true;
            Assert.True(settings.showGhostsInTrackingStation);
        }
    }
}
