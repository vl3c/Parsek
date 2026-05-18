using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for InputFocusGuard.IsTextFieldFocused, the guard that suppresses
    /// raw Input.GetKeyDown shortcuts (watch mode `[`, `]`, V, W) while a UI
    /// text field has keyboard focus. Drives the two stubbable seams
    /// (IMGUI GUIUtility.keyboardControl, uGUI EventSystem) without touching
    /// real Unity statics.
    /// </summary>
    [Collection("Sequential")]
    public class InputFocusGuardTests : IDisposable
    {
        public InputFocusGuardTests()
        {
            InputFocusGuard.ResetTestOverrides();
        }

        public void Dispose()
        {
            InputFocusGuard.ResetTestOverrides();
        }

        [Fact]
        public void IsTextFieldFocused_NeitherSeamFocused_ReturnsFalse()
        {
            InputFocusGuard.KeyboardControlProviderForTesting = () => 0;
            InputFocusGuard.EventSystemFocusedProviderForTesting = () => false;

            Assert.False(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void IsTextFieldFocused_KeyboardControlNonZero_ReturnsTrue()
        {
            // IMGUI assigns a non-zero control ID to the currently-focused
            // TextField. Any non-zero value should trigger the guard.
            InputFocusGuard.KeyboardControlProviderForTesting = () => 42;
            InputFocusGuard.EventSystemFocusedProviderForTesting = () => false;

            Assert.True(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void IsTextFieldFocused_EventSystemFocused_ReturnsTrue()
        {
            InputFocusGuard.KeyboardControlProviderForTesting = () => 0;
            InputFocusGuard.EventSystemFocusedProviderForTesting = () => true;

            Assert.True(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void IsTextFieldFocused_BothSeamsFocused_ReturnsTrue()
        {
            InputFocusGuard.KeyboardControlProviderForTesting = () => 99;
            InputFocusGuard.EventSystemFocusedProviderForTesting = () => true;

            Assert.True(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void IsTextFieldFocused_KeyboardControlShortCircuits_DoesNotCallEventSystemProvider()
        {
            // The IMGUI check runs first; once it returns true, the uGUI seam
            // must not be invoked. Verifies the short-circuit so a future
            // refactor cannot accidentally evaluate both seams unconditionally.
            bool eventSystemProviderCalled = false;
            InputFocusGuard.KeyboardControlProviderForTesting = () => 1;
            InputFocusGuard.EventSystemFocusedProviderForTesting = () =>
            {
                eventSystemProviderCalled = true;
                return false;
            };

            Assert.True(InputFocusGuard.IsTextFieldFocused());
            Assert.False(eventSystemProviderCalled);
        }

        [Fact]
        public void ResetTestOverrides_ClearsBothProviders()
        {
            InputFocusGuard.KeyboardControlProviderForTesting = () => 1;
            InputFocusGuard.EventSystemFocusedProviderForTesting = () => true;

            InputFocusGuard.ResetTestOverrides();

            Assert.Null(InputFocusGuard.KeyboardControlProviderForTesting);
            Assert.Null(InputFocusGuard.EventSystemFocusedProviderForTesting);
        }
    }
}
