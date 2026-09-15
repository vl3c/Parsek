using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>UiAction op=raise</c> / <c>op=dismiss</c>: the ops that put a Parsek
    /// <c>PopupDialog</c> on screen and leave it standing to be photographed, then take it
    /// down.
    ///
    /// <para>The census could not photograph ANY of the 21 modals before these existed -
    /// all six wave-2 lanes' <c>op=dialog</c> steps answered
    /// <c>open=false count=0</c> - so what these cells pin is the two things that make the
    /// pictures trustworthy: the closed set (nothing is raised over synthetic state) and the
    /// press policy (a census lane cannot destroy its own fixture by naming the wrong
    /// button).</para>
    /// </summary>
    public class TestCommandUiRaiseDismissTests
    {
        private static string Value(List<KeyValuePair<string, string>> payload, string key)
        {
            foreach (KeyValuePair<string, string> kv in payload)
                if (kv.Key == key) return kv.Value;
            return null;
        }

        // ----- the ops themselves -----

        [Fact]
        public void BothOps_ParseRoundTrip_NameNoWindow_AndAreTwoPhase()
        {
            Assert.True(TestCommandUiAction.TryParseOp("raise", out UiActionOp raise, out _));
            Assert.Equal(UiActionOp.Raise, raise);
            Assert.True(TestCommandUiAction.TryParseOp("dismiss", out UiActionOp dis, out _));
            Assert.Equal(UiActionOp.Dismiss, dis);

            // No window in the grammar: a PopupDialog is uGUI and no window-table row can
            // name one, which is why both take popup= instead.
            Assert.False(TestCommandUiAction.OpNeedsWindow(raise));
            Assert.False(TestCommandUiAction.OpNeedsWindow(dis));

            // Two-phase for the op=open reason: a uGUI popup exists as a drawn surface only
            // from the next frame, and the settle's name comparison is what separates "the
            // spawn site ran" from "its own guard returned".
            Assert.True(TestCommandUiAction.OpIsTwoPhase(raise));
            Assert.True(TestCommandUiAction.OpIsTwoPhase(dis));

            // But NOT host-showUI gated: a modal stands over a hidden Parsek surface
            // exactly as it stands over a visible one.
            Assert.False(TestCommandUiAction.SettleChecksHostShowUi(raise));
            Assert.False(TestCommandUiAction.SettleChecksHostShowUi(dis));
        }

        // ----- popup= -----

        [Fact]
        public void Popup_IsRequired_AndMissingIsDistinctFromUnknown()
        {
            Assert.False(TestCommandUiDialogRaise.TryResolveDialog(
                null, out _, out string missing));
            Assert.Equal(TestCommandUiDialogRaise.PopupArgMissingReason, missing);

            Assert.False(TestCommandUiDialogRaise.TryResolveDialog(
                "wipeeverything", out _, out string unknown));
            Assert.Equal(TestCommandUiDialogRaise.PopupUnknownReason, unknown);
        }

        [Theory]
        [InlineData("ActionBlocked")]   // case-sensitive, the window= rule
        [InlineData("SEAL")]
        [InlineData("merge")]           // deliberately NOT raisable - see the table comment
        [InlineData("preswitch")]
        [InlineData("")]
        public void Popup_IsCaseSensitiveAndClosed(string raw)
        {
            Assert.False(TestCommandUiDialogRaise.TryResolveDialog(raw, out _, out string r));
            Assert.Equal(TestCommandUiDialogRaise.PopupUnknownReason, r);
        }

        [Fact]
        public void EveryTableRow_ResolvesByItsOwnToken_AndCarriesAParsekPopupName()
        {
            Assert.NotEmpty(TestCommandUiDialogRaise.Dialogs);
            foreach (UiRaisableDialog spec in TestCommandUiDialogRaise.Dialogs)
            {
                Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                    spec.Name, out UiRaisableDialog resolved, out _));
                Assert.Equal(spec.PopupName, resolved.PopupName);

                // The settle confirms the STANDING popup's name against this field, and
                // op=dialog finds Parsek popups by prefix scan - so a row whose name does
                // not carry the prefix could be raised and then reported as "no dialog
                // open". Pinned here as well as by ParsekDialogNamePrefixSourceGateTests,
                // which reads the SPAWN SITES rather than this table.
                Assert.True(TestCommandUiDialog.IsParsekDialogName(spec.PopupName),
                    spec.Name + "'s popup name must start with the Parsek prefix");
                Assert.False(string.IsNullOrEmpty(spec.Title));
                Assert.NotEmpty(spec.Buttons);
            }
        }

        [Fact]
        public void ValidPopupNames_ListsEveryRow_ForTheRejectMessage()
        {
            string listed = TestCommandUiDialogRaise.ValidPopupNames;
            string[] parts = listed.Split(',');
            Assert.Equal(TestCommandUiDialogRaise.Dialogs.Count, parts.Length);
            foreach (UiRaisableDialog spec in TestCommandUiDialogRaise.Dialogs)
                Assert.Contains(spec.Name, parts);
        }

        [Fact]
        public void PopupTokens_AreUnique_AndSoAreTheLivePopupNames()
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (UiRaisableDialog spec in TestCommandUiDialogRaise.Dialogs)
            {
                Assert.True(tokens.Add(spec.Name), "duplicate popup token " + spec.Name);
                // A duplicate live name would make the settle's confirmation ambiguous:
                // two rows would accept each other's popup.
                Assert.True(names.Add(spec.PopupName),
                    "duplicate MultiOptionDialog name " + spec.PopupName);
            }
        }

        // ----- press= -----

        [Fact]
        public void Press_AbsentMeansDismissWithoutPressing()
        {
            // THE DEFAULT, and it is the whole safety property: most of these confirms
            // mutate the save, so a lane that forgot press= must take the dialog down
            // rather than confirm it.
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.WipeRecordingsDialog,
                out UiRaisableDialog spec, out _));
            Assert.True(TestCommandUiDialogRaise.TryResolvePress(
                spec, null, out string button, out string reject));
            Assert.Null(button);
            Assert.Null(reject);
            Assert.True(TestCommandUiDialogRaise.TryResolvePress(
                spec, "", out string empty, out _));
            Assert.Null(empty);
        }

        [Fact]
        public void Press_RefusesEveryMutatingConfirmButton()
        {
            // The one cell that matters most: `Wipe All` clears every committed recording
            // and unreserves every crew reservation in the save the census is photographing.
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.WipeRecordingsDialog,
                out UiRaisableDialog wipe, out _));
            Assert.False(TestCommandUiDialogRaise.TryResolvePress(
                wipe, "Wipe All", out _, out string reject));
            Assert.Equal(TestCommandUiDialogRaise.PressNotAllowedReason, reject);

            // And its cancel IS pressable, so a lane can drive the harmless half.
            Assert.True(TestCommandUiDialogRaise.TryResolvePress(
                wipe, "Cancel", out string cancel, out _));
            Assert.Equal("Cancel", cancel);
        }

        [Fact]
        public void Press_UnknownButtonIsADistinctRejectFromAForbiddenOne()
        {
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.SealDialog, out UiRaisableDialog seal, out _));
            // A typo and a forbidden confirm send an author to different fixes.
            Assert.False(TestCommandUiDialogRaise.TryResolvePress(
                seal, "cancel", out _, out string typo));
            Assert.Equal(TestCommandUiDialogRaise.PressUnknownReason, typo);
            Assert.False(TestCommandUiDialogRaise.TryResolvePress(
                seal, "Seal Permanently", out _, out string forbidden));
            Assert.Equal(TestCommandUiDialogRaise.PressNotAllowedReason, forbidden);
        }

        [Fact]
        public void Press_AllowsAnyButtonOnlyOnTheTwoInformationalPopups()
        {
            foreach (UiRaisableDialog spec in TestCommandUiDialogRaise.Dialogs)
            {
                if (spec.Press != UiDialogPressPolicy.AnyButton) continue;
                // An AnyButton row must have no button that changes anything, which in
                // practice means a single OK. Pinned so a future row cannot be waved
                // through the policy by being added to the wrong bucket.
                Assert.Single(spec.Buttons);
                Assert.Equal("OK", spec.Buttons[0]);
                Assert.True(TestCommandUiDialogRaise.TryResolvePress(
                    spec, "OK", out string ok, out _));
                Assert.Equal("OK", ok);
            }
        }

        [Fact]
        public void EverySafeButtonOnlyRow_NamesASafeButtonThatIsOneOfItsButtons()
        {
            foreach (UiRaisableDialog spec in TestCommandUiDialogRaise.Dialogs)
            {
                if (spec.Press != UiDialogPressPolicy.SafeButtonOnly) continue;
                Assert.False(string.IsNullOrEmpty(spec.SafeButton),
                    spec.Name + " is SafeButtonOnly with no safe button named");
                Assert.Contains(spec.SafeButton, spec.Buttons);
                // And it is NOT the first button: every one of these dialogs puts its
                // mutating confirm first, so a safe button that was also index 0 would mean
                // the row had been mis-transcribed.
                Assert.NotEqual(spec.Buttons[0], spec.SafeButton);
            }
        }

        [Fact]
        public void EveryPressableLabel_IsSpaceFree_BecauseTheWireIsSpaceSeparated()
        {
            // TestCommandProtocol encodes only % and =, so a space inside a value would
            // split the command. Every pressable label is space-free by construction today
            // (OK / Cancel); this cell is what makes that a checked property rather than a
            // coincidence, and it is the thing that would have to change before a dialog
            // whose safe button reads "No, cancel" could be added.
            foreach (string label in TestCommandUiDialogRaise.ValidPressButtons.Split(','))
            {
                Assert.False(string.IsNullOrEmpty(label));
                Assert.DoesNotContain(" ", label);
                Assert.DoesNotContain("=", label);
                Assert.DoesNotContain("%", label);
            }
        }

        // ----- the settle predicate -----

        [Fact]
        public void RaiseConfirmed_DemandsTheExpectedNameAndNotMerelyAParsekPopup()
        {
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.ActionBlockedDialog,
                out UiRaisableDialog spec, out _));
            Assert.True(TestCommandUiDialogRaise.RaiseConfirmed(spec, spec.PopupName));
            // Every spawn site in the table has a guarded early return, so a raise that
            // returned silently while an UNRELATED Parsek modal happened to stand must not
            // read as a success - the capture beside it would photograph the wrong dialog.
            Assert.False(TestCommandUiDialogRaise.RaiseConfirmed(spec, "ParsekMerge"));
            Assert.False(TestCommandUiDialogRaise.RaiseConfirmed(spec, null));
            Assert.False(TestCommandUiDialogRaise.RaiseConfirmed(spec, ""));
        }

        // ----- payloads -----

        [Fact]
        public void RaisePayload_EchoesOpDialogsOwnKeys_SoTheTwoStepsAreComparable()
        {
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.WipeMilestonesDialog,
                out UiRaisableDialog spec, out _));
            var p = TestCommandUiDialogRaise.BuildRaisePayload(
                spec, "Confirm: Wipe Milestones",
                new List<string> { "Wipe All", "Cancel" });
            Assert.Equal("raise", Value(p, "op"));
            Assert.Equal("wipemilestones", Value(p, "popup"));
            Assert.Equal("ParsekWipeMilestonesConfirm", Value(p, "name"));
            Assert.Equal("Confirm: Wipe Milestones", Value(p, "title"));
            Assert.Equal("Wipe All|Cancel", Value(p, "buttons"));
            Assert.Equal("2", Value(p, "nbuttons"));
            Assert.Equal("Cancel", Value(p, "pressable"));
        }

        [Fact]
        public void RaisePayload_FallsBackToTheTableWhenTheLiveReadIsEmpty()
        {
            // A title / button list the reflection could not read must not put a blank on
            // the wire (the describe sentinel rule).
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.SaveFailedDialog,
                out UiRaisableDialog spec, out _));
            var p = TestCommandUiDialogRaise.BuildRaisePayload(spec, null, null);
            Assert.Equal("Save failed", Value(p, "title"));
            Assert.Equal("OK", Value(p, "buttons"));
            Assert.Equal("0", Value(p, "nbuttons"));
        }

        [Fact]
        public void DismissPayload_SaysWhetherAButtonWasPressed()
        {
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.FastForwardDialog,
                out UiRaisableDialog spec, out _));
            var plain = TestCommandUiDialogRaise.BuildDismissPayload(spec, null, false);
            Assert.Equal("dismiss", Value(plain, "op"));
            Assert.Equal("fastforward", Value(plain, "popup"));
            Assert.Equal("-", Value(plain, "pressed"));
            Assert.Equal("false", Value(plain, "open"));

            var pressed = TestCommandUiDialogRaise.BuildDismissPayload(
                spec, "Cancel", false);
            Assert.Equal("Cancel", Value(pressed, "pressed"));
        }

        [Fact]
        public void BothPayloads_KeysAreAllCapturable()
        {
            // hlib.HANDLE_REF_RE captures ${step.<field>} only for [A-Za-z0-9_]+.
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                TestCommandUiDialogRaise.RewindDialog, out UiRaisableDialog spec, out _));
            foreach (var payload in new[]
                     {
                         TestCommandUiDialogRaise.BuildRaisePayload(spec, "t", null),
                         TestCommandUiDialogRaise.BuildDismissPayload(spec, "Cancel", false),
                     })
            {
                foreach (KeyValuePair<string, string> kv in payload)
                    Assert.Matches("^[A-Za-z0-9_]+$", kv.Key);
            }
        }

        // ----- source gate: the table describes the real spawn sites -----

        /// <summary>
        /// The table's <see cref="UiRaisableDialog.PopupName"/> and
        /// <see cref="UiRaisableDialog.Buttons"/> are hand-transcribed from the production
        /// spawn sites, and the settle compares the live popup against the first of them -
        /// so a drifted row turns into a <c>dialog-not-raised</c> ERROR after a whole KSP
        /// boot. This reads <c>Source/Parsek</c> and asserts each row's popup name and every
        /// one of its button labels appear as string literals in the file that spawns it.
        ///
        /// <para>Deliberately a LITERAL search rather than a parse of the spawn call: the
        /// point is to catch a renamed dialog or a re-worded button, and both change the
        /// literal. A file that no longer contains the name fails loudly here instead of in
        /// a flight.</para>
        /// </summary>
        [Theory]
        [InlineData("actionblocked", "CommittedActionDialog.cs")]
        [InlineData("savefailed", "SceneExitInterceptor.cs")]
        [InlineData("wiperecordings", "ParsekUI.cs")]
        [InlineData("wipemilestones", "ParsekUI.cs")]
        [InlineData("rewind", "UI/RecordingsTableUI.cs")]
        [InlineData("fastforward", "UI/RecordingsTableUI.cs")]
        [InlineData("seal", "UnfinishedFlightSealHandler.cs")]
        public void EveryRowsNameAndButtons_AppearInTheFileThatSpawnsIt(
            string token, string relPath)
        {
            Assert.True(TestCommandUiDialogRaise.TryResolveDialog(
                token, out UiRaisableDialog spec, out _));
            string src = ReadParsekSource(relPath);

            // The seal dialog names its popup through a const (DialogName), so accept
            // either the literal or a const whose value is it.
            Assert.True(src.Contains("\"" + spec.PopupName + "\""),
                relPath + " no longer contains the literal \"" + spec.PopupName
                + "\" that op=raise popup=" + token + " confirms against");
            for (int i = 0; i < spec.Buttons.Length; i++)
            {
                Assert.True(src.Contains("\"" + spec.Buttons[i] + "\""),
                    relPath + " no longer contains the button literal \""
                    + spec.Buttons[i] + "\" the " + token + " row transcribes");
            }
        }

        /// <summary>
        /// Anti-vacuity for the cell above: the source directory must really be being read.
        /// </summary>
        [Fact]
        public void TheSourceGateIsNotVacuous()
        {
            string src = ReadParsekSource("CommittedActionDialog.cs");
            Assert.Contains("SpawnPopupDialog", src);
            Assert.DoesNotContain("\"ParsekThisNameDoesNotExist\"", src);
        }

        private static string ReadParsekSource(string relPath)
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/, so five levels up is
            // the repo root (the house convention).
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));
            string full = Path.Combine(root, "Source", "Parsek",
                                       relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "missing source file " + full);
            return File.ReadAllText(full);
        }
    }
}
