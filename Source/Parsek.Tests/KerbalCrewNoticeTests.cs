using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The two crew consequences that used to be silent (the 2026-09-22 Kerbals-window
    /// review, recommendation 6): a reserved kerbal swapped out of the active craft gets
    /// ONE screen message per swap call that actually swapped someone, and a refused
    /// dismissal raises the same Action Blocked dialog its four sibling blocks raise.
    /// </summary>
    public class KerbalCrewNoticeTests
    {
        private static List<KeyValuePair<string, string>> Swaps(params string[] pairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
            return list;
        }

        // catches: a message for a no-op. The house rule is a ScreenMessage for an EVENT
        // that changed something, never for a call that swapped nobody.
        [Fact]
        public void SwapMessage_NoSwapMeansNoMessage()
        {
            Assert.Null(CrewReservationManager.FormatReservedCrewSwapMessage(null));
            Assert.Null(CrewReservationManager.FormatReservedCrewSwapMessage(Swaps()));
        }

        [Fact]
        public void SwapMessage_OneSwapNamesBothKerbals()
        {
            Assert.Equal(
                "Jebediah Kerman is reserved by a committed flight; Debwig Kerman takes the seat.",
                CrewReservationManager.FormatReservedCrewSwapMessage(
                    Swaps("Jebediah Kerman", "Debwig Kerman")));
        }

        [Fact]
        public void SwapMessage_SeveralSwapsAreCountedInOneMessage()
        {
            Assert.Equal(
                "3 kerbals are reserved by committed flights; their stand-ins take the seats.",
                CrewReservationManager.FormatReservedCrewSwapMessage(Swaps(
                    "Bill Kerman", "Jane Kerman",
                    "Bob Kerman", "Sizon Kerman",
                    "Valentina Kerman", "Kathdan Kerman")));
        }

        // The swap site wires the message the way the rule says: built from the Pass-1
        // swaps only, shown once, and only when the formatter returned text.
        [Fact]
        public void SwapMessage_IsShownOnceFromTheSwapSiteAndOnlyForRealSwaps()
        {
            string src = TooltipEchoBudgetTests.ReadParsekSource("CrewReservationManager.cs")
                .Replace("\r\n", "\n");
            int start = src.IndexOf("public static int SwapReservedCrewInFlight()",
                StringComparison.Ordinal);
            Assert.True(start >= 0, "SwapReservedCrewInFlight moved or was renamed.");
            int end = src.IndexOf("internal const float ReservedCrewSwapMessageSeconds",
                start, StringComparison.Ordinal);
            Assert.True(end > start, "the swap-message duration constant moved.");
            string body = src.Substring(start, end - start);

            Assert.Contains(
                "seatSwaps.Add(new KeyValuePair<string, string>(original.name, replacement.name));",
                body);
            Assert.Contains("FormatReservedCrewSwapMessage(seatSwaps)", body);
            Assert.Contains("if (swapMessage != null)", body);
            Assert.Equal(1, CountOf(body, "ParsekLog.ScreenMessage("));
        }

        [Theory]
        [InlineData((int)KerbalReservationKind.ReservedActive,
            "This kerbal is reserved by a committed flight on your timeline.")]
        [InlineData((int)KerbalReservationKind.ReservedRetired,
            "This retired stand-in flew a committed flight on your timeline.")]
        [InlineData((int)KerbalReservationKind.NotManaged,
            "This kerbal is a stand-in covering a reserved kerbal's seat.")]
        public void DismissalBlock_ReasonUsesTheKerbalsWindowVocabulary(int kind, string expected)
        {
            Assert.Equal(expected,
                KerbalDismissalPatch.DescribeDismissalBlock((KerbalReservationKind)kind));
        }

        // catches: the refused dismissal going back to being the one silent refusal.
        [Fact]
        public void DismissalBlock_RaisesTheExistingActionBlockedDialog()
        {
            string src = TooltipEchoBudgetTests.ReadParsekSource("Patches/KerbalDismissalPatch.cs");
            Assert.Contains("CommittedActionDialog.ShowBlocked(", src);
            Assert.Contains("DescribeDismissalBlock(kerbals.GetReservationKind(crew.name))", src);
            // No new dialog type: the patch never spawns its own popup.
            Assert.DoesNotContain("PopupDialog", src);
            Assert.DoesNotContain("MultiOptionDialog", src);
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }
    }
}
