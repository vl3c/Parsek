using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for InputFocusGuard.IsTextFieldFocused, the guard that suppresses
    /// raw Input.GetKeyDown shortcuts (watch mode `[`, `]`, V, W) while an IMGUI
    /// text field has keyboard focus. Drives the stubbable seam without
    /// touching real Unity statics.
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
        public void IsTextFieldFocused_KeyboardControlZero_ReturnsFalse()
        {
            InputFocusGuard.KeyboardControlProviderForTesting = () => 0;

            Assert.False(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void IsTextFieldFocused_KeyboardControlNonZero_ReturnsTrue()
        {
            // IMGUI assigns a non-zero control ID to the currently-focused
            // TextField. Any non-zero value should trigger the guard.
            InputFocusGuard.KeyboardControlProviderForTesting = () => 42;

            Assert.True(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void IsTextFieldFocused_KeyboardControlNegative_ReturnsTrue()
        {
            // GUIUtility.keyboardControl is an int; document that the guard
            // treats "any non-zero" as focused, not just positive values.
            InputFocusGuard.KeyboardControlProviderForTesting = () => -1;

            Assert.True(InputFocusGuard.IsTextFieldFocused());
        }

        [Fact]
        public void ResetTestOverrides_ClearsProvider()
        {
            InputFocusGuard.KeyboardControlProviderForTesting = () => 1;

            InputFocusGuard.ResetTestOverrides();

            Assert.Null(InputFocusGuard.KeyboardControlProviderForTesting);
        }
    }
}
