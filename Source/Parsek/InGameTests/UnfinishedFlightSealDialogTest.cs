using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Parsek.InGameTests
{
    public class UnfinishedFlightSealDialogTest
    {
        private static readonly FieldInfo PopupDialogToDisplayField =
            typeof(PopupDialog).GetField("dialogToDisplay",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogNameField =
            typeof(MultiOptionDialog).GetField("name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogTitleField =
            typeof(MultiOptionDialog).GetField("title",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogOptionsField =
            typeof(MultiOptionDialog).GetField("Options",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo DialogGuiButtonTextField =
            typeof(DialogGUIButton).GetField("OptionText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo DialogGuiButtonOptionSelectedMethod =
            typeof(DialogGUIButton).GetMethod("OptionSelected",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [InGameTest(Category = "Rewind", Scene = GameScenes.SPACECENTER,
            Description = "Unfinished Flight Seal popup cancel button clears the input lock without sealing")]
        public IEnumerator SealPopupCancelClearsLockWithoutSealing()
        {
            yield return RunSealDialogButtonFlow("Cancel", expectedSealed: false);
        }

        [InGameTest(Category = "Rewind", Scene = GameScenes.SPACECENTER,
            Description = "Unfinished Flight Seal popup confirm button seals the slot and clears the input lock")]
        public IEnumerator SealPopupConfirmSealsSlotAndClearsLock()
        {
            yield return RunSealDialogButtonFlow("Seal Permanently", expectedSealed: true);
        }

        private static IEnumerator RunSealDialogButtonFlow(string buttonText, bool expectedSealed)
        {
            if (!AnyReflectionBindingResolved())
            {
                InGameAssert.Fail("seal dialog reflection helpers all failed to bind");
                yield break;
            }
            if (!AllReflectionBindingsResolved())
            {
                InGameAssert.Skip(
                    "seal dialog reflection helpers are unavailable: " +
                    MissingReflectionBindings());
                yield break;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                InGameAssert.Skip("No ParsekScenario instance");
                yield break;
            }

            var originalRps = scenario.RewindPoints;
            var originalSupersedes = scenario.RecordingSupersedes;
            var originalTombstones = scenario.LedgerTombstones;
            var originalMarker = scenario.ActiveReFlySessionMarker;
            var originalJournal = scenario.ActiveMergeJournal;

            var rec = new Recording
            {
                RecordingId = "ig_seal_rec",
                VesselName = "IG Seal Probe",
                MergeState = MergeState.CommittedProvisional,
                ParentBranchPointId = "ig_seal_bp",
                TerminalStateValue = TerminalState.Orbiting,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 123.4,
            };
            var focusSlot = new ChildSlot
            {
                SlotIndex = 0,
                OriginChildRecordingId = "ig_seal_focus",
                Controllable = true,
            };
            var targetSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = rec.RecordingId,
                Controllable = true,
            };
            var rp = new RewindPoint
            {
                RewindPointId = "ig_seal_rp",
                BranchPointId = "ig_seal_bp",
                SessionProvisional = true,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot> { focusSlot, targetSlot },
            };

            try
            {
                PopupDialog.DismissPopup(UnfinishedFlightSealHandler.DialogName);
                scenario.RewindPoints = new List<RewindPoint> { rp };
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
                scenario.LedgerTombstones = new List<LedgerTombstone>();

                UnfinishedFlightSealHandler.ShowConfirmation(rec);
                yield return WaitForPopupDialog(UnfinishedFlightSealHandler.DialogName, 2f);
                UnfinishedFlightSealHandler.ShowConfirmation(rec);
                yield return WaitForPopupDialog(UnfinishedFlightSealHandler.DialogName, 2f);
                yield return null;

                InGameAssert.IsTrue(UnfinishedFlightSealHandler.DialogLockActiveForTesting,
                    "Seal dialog should set its input lock while open");
                InGameAssert.AreEqual(1, CountPopupDialog(UnfinishedFlightSealHandler.DialogName),
                    "Opening Seal confirmation while one is already open should replace it");
                PopupDialog popup = FindPopupDialog(UnfinishedFlightSealHandler.DialogName);
                InGameAssert.IsNotNull(popup, "Seal confirmation popup should exist");
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
                InGameAssert.IsNotNull(dialog, "Seal popup should expose a MultiOptionDialog");
                string title = MultiOptionDialogTitleField.GetValue(dialog) as string;
                InGameAssert.AreEqual("Seal Unfinished Flight?", title,
                    "Seal popup title should match production copy");

                DialogGUIButton button = FindButton(dialog, buttonText);
                InGameAssert.IsNotNull(button, $"Expected seal popup button '{buttonText}'");
                DialogGuiButtonOptionSelectedMethod.Invoke(button, null);

                yield return WaitForPopupDialogToClose(UnfinishedFlightSealHandler.DialogName, 2f);
                yield return null;

                InGameAssert.IsFalse(UnfinishedFlightSealHandler.DialogLockActiveForTesting,
                    "Seal dialog should clear its input lock after the button callback");
                InGameAssert.AreEqual(expectedSealed, targetSlot.Sealed,
                    $"Button '{buttonText}' should leave sealed={expectedSealed}");
                if (expectedSealed)
                    InGameAssert.IsFalse(string.IsNullOrEmpty(targetSlot.SealedRealTime),
                        "Confirming Seal should stamp SealedRealTime");
                else
                    InGameAssert.IsTrue(string.IsNullOrEmpty(targetSlot.SealedRealTime),
                        "Cancelling Seal should leave SealedRealTime blank");
            }
            finally
            {
                PopupDialog.DismissPopup(UnfinishedFlightSealHandler.DialogName);
                UnfinishedFlightSealHandler.ClearLock();
                scenario.RewindPoints = originalRps;
                scenario.RecordingSupersedes = originalSupersedes;
                scenario.LedgerTombstones = originalTombstones;
                scenario.ActiveReFlySessionMarker = originalMarker;
                scenario.ActiveMergeJournal = originalJournal;
            }
        }

        private static bool AnyReflectionBindingResolved()
        {
            return PopupDialogToDisplayField != null
                || MultiOptionDialogNameField != null
                || MultiOptionDialogTitleField != null
                || MultiOptionDialogOptionsField != null
                || DialogGuiButtonTextField != null
                || DialogGuiButtonOptionSelectedMethod != null;
        }

        private static bool AllReflectionBindingsResolved()
        {
            return PopupDialogToDisplayField != null
                && MultiOptionDialogNameField != null
                && MultiOptionDialogTitleField != null
                && MultiOptionDialogOptionsField != null
                && DialogGuiButtonTextField != null
                && DialogGuiButtonOptionSelectedMethod != null;
        }

        private static string MissingReflectionBindings()
        {
            var missing = new List<string>();
            if (PopupDialogToDisplayField == null)
                missing.Add("PopupDialog.dialogToDisplay");
            if (MultiOptionDialogNameField == null)
                missing.Add("MultiOptionDialog.name");
            if (MultiOptionDialogTitleField == null)
                missing.Add("MultiOptionDialog.title");
            if (MultiOptionDialogOptionsField == null)
                missing.Add("MultiOptionDialog.Options");
            if (DialogGuiButtonTextField == null)
                missing.Add("DialogGUIButton.OptionText");
            if (DialogGuiButtonOptionSelectedMethod == null)
                missing.Add("DialogGUIButton.OptionSelected");
            return string.Join(",", missing.ToArray());
        }

        private static PopupDialog FindPopupDialog(string dialogName)
        {
            if (string.IsNullOrEmpty(dialogName))
                return null;

            PopupDialog[] popups = UnityEngine.Object.FindObjectsOfType<PopupDialog>();
            for (int i = 0; i < popups.Length; i++)
            {
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popups[i]) as MultiOptionDialog;
                if (dialog == null)
                    continue;

                string currentName = MultiOptionDialogNameField.GetValue(dialog) as string;
                if (currentName == dialogName)
                    return popups[i];
            }

            return null;
        }

        private static int CountPopupDialog(string dialogName)
        {
            if (string.IsNullOrEmpty(dialogName))
                return 0;

            int count = 0;
            PopupDialog[] popups = UnityEngine.Object.FindObjectsOfType<PopupDialog>();
            for (int i = 0; i < popups.Length; i++)
            {
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popups[i]) as MultiOptionDialog;
                if (dialog == null)
                    continue;

                string currentName = MultiOptionDialogNameField.GetValue(dialog) as string;
                if (currentName == dialogName)
                    count++;
            }

            return count;
        }

        private static DialogGUIButton FindButton(MultiOptionDialog dialog, string buttonText)
        {
            if (dialog == null || string.IsNullOrEmpty(buttonText))
                return null;

            DialogGUIBase[] options = MultiOptionDialogOptionsField.GetValue(dialog) as DialogGUIBase[];
            if (options == null)
                return null;

            for (int i = 0; i < options.Length; i++)
            {
                DialogGUIButton button = options[i] as DialogGUIButton;
                if (button == null)
                    continue;

                string currentText = DialogGuiButtonTextField.GetValue(button) as string;
                if (string.Equals(currentText, buttonText, StringComparison.Ordinal))
                    return button;
            }

            return null;
        }

        private static IEnumerator WaitForPopupDialog(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) != null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialog timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }

        private static IEnumerator WaitForPopupDialogToClose(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) == null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialogToClose timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }
    }
}
