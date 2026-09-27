using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live check for <see cref="KerbalAutoHireApplicantPatch"/> (todo
    /// KSP-SETTINGS-FOLLOWUPS-2026-09-27 item 4). With <c>Difficulty.AutoHireCrews</c> on, stock
    /// <c>KerbalRoster.DefaultCrewForVessel</c> fills each seat the available crew cannot with
    /// <c>GetNextApplicant()</c> (the first Applicant in roster order), <c>HireApplicant</c>s him
    /// and seats him without checking the outcome. After a rewind to before a committed hire,
    /// that first applicant is the one the committed timeline hires later:
    /// <see cref="KerbalHirePatch"/> refused his hire, yet stock seated him unhired, once per
    /// short seat.
    ///
    /// <para>The cell builds that state in-process and drives the REAL stock call: the first
    /// applicant in roster order gets a committed future CrewHired ledger row (the rewound
    /// state: the hire is committed and still ahead of the clock), auto-hire is switched on,
    /// every Available crew kerbal but one is set Assigned so a crewed command pod is short
    /// by capacity - 1 seats, and <c>DefaultCrewForVessel</c> runs on a one-pod craft node.
    /// Every roster, funds, ledger and difficulty change is reverted in the finally; the
    /// crew and funds events are suppressed so nothing reaches the ledger (campaign
    /// isolation is the backstop only).</para>
    /// </summary>
    public partial class FlightIntegrationTests
    {
        private const string AutoHireTag = "AutoHireReservation";

        [InGameTest(Category = "AutoHireReservation", Scene = GameScenes.SPACECENTER,
            Description = "Auto-hire after a rewound committed hire: stock DefaultCrewForVessel on a pod short of crew skips the applicant a committed future hires, hires and seats other kerbals, seats nobody twice, and never reaches the KerbalHirePatch refusal.")]
        public void AutoHireSkipsCommittedFutureHireApplicant()
        {
            Game game = HighLogic.CurrentGame;
            if (game == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                return;
            }
            if (game.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip("stock auto-hire charges funds and checks the crew limit; career only (mode=" + game.Mode + ")");
                return;
            }
            KerbalRoster roster = game.CrewRoster;
            if (roster == null || Funding.Instance == null || GameVariables.Instance == null)
            {
                InGameAssert.Skip("CrewRoster, Funding or GameVariables is not up");
                return;
            }

            AvailablePart podInfo = ResolveAutoHirePod();
            if (podInfo == null)
            {
                InGameAssert.Fail("No stock crewed command pod with 2+ seats (mk1-3pod) is loaded");
                return;
            }
            int capacity = podInfo.partPrefab.CrewCapacity;

            // Stock checks the crew limit once, before the shortfall loop.
            int activeBefore = roster.GetActiveCrewCount();
            int crewLimit = GameVariables.Instance.GetActiveCrewLimit(
                ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex));
            if (activeBefore >= crewLimit)
            {
                InGameAssert.Skip("the host is at the Astronaut Complex crew limit (" + activeBefore + "/" + crewLimit
                    + "), so stock auto-hire would not run; the lane's host must have room for one hire");
                return;
            }

            var snapshot = new List<AutoHireRosterEntry>();
            for (int i = 0; i < roster.Count; i++)
            {
                ProtoCrewMember pcm = roster[i];
                if (pcm != null)
                    snapshot.Add(new AutoHireRosterEntry { Member = pcm, Name = pcm.name, Type = pcm.type, Status = pcm.rosterStatus });
            }

            bool priorAutoHire = game.Parameters.Difficulty.AutoHireCrews;
            double fundsBefore = Funding.Instance.Funds;
            string fixtureRecordingId = null;
            Recording fixtureRecording = null;
            ProtoCrewMember createdApplicant = null;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;

            using (SuppressionGuard.Crew())
            using (SuppressionGuard.Resources())
            {
                try
                {
                    // The reserved applicant is the FIRST applicant in roster order, the one
                    // stock's GetNextApplicant would take; one is created only when the host has none.
                    ProtoCrewMember reserved = FirstApplicantInRosterOrder(roster);
                    if (reserved == null)
                    {
                        createdApplicant = CreateApplicantForOverlayTest(roster,
                            "PrskAutoHire" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + " Kerman");
                        reserved = createdApplicant;
                    }
                    InGameAssert.IsTrue(ReferenceEquals(reserved, FirstApplicantInRosterOrder(roster)),
                        "The reserved applicant must be the first applicant in roster order (stock's pick)");
                    string reservedName = reserved.name;

                    fixtureRecordingId = "autohire-reservation-" + System.Guid.NewGuid().ToString("N");
                    fixtureRecording = AddCommittedOverlayFixture(fixtureRecordingId, GameStateEventType.CrewHired,
                        reservedName, "Auto-hire reservation test");
                    InGameAssert.IsTrue(KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked(reservedName),
                        "The committed future CrewHired row must reserve '" + reservedName + "' (the rewound state)");

                    // Keep exactly one Available crew kerbal so the pod is short by capacity - 1.
                    int kept = 0;
                    string keptName = null;
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        ProtoCrewMember pcm = snapshot[i].Member;
                        if (pcm.type != ProtoCrewMember.KerbalType.Crew
                            || pcm.rosterStatus != ProtoCrewMember.RosterStatus.Available)
                            continue;
                        if (kept == 0 && !pcm.inactive)
                        {
                            kept = 1;
                            keptName = pcm.name;
                            continue;
                        }
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                    }
                    int shortfall = capacity - kept;

                    // Fund every hire the shortfall loop will make (stock skips a hire it cannot afford).
                    double needed = 1.0;
                    for (int k = 0; k < shortfall; k++)
                        needed += GameVariables.Instance.GetRecruitHireCost(activeBefore + k);
                    if (Funding.Instance.Funds < needed)
                        Funding.Instance.AddFunds(needed - Funding.Instance.Funds, TransactionReasons.None);

                    game.Parameters.Difficulty.AutoHireCrews = true;

                    var craftNode = new ConfigNode("SHIP");
                    craftNode.AddNode("PART").AddValue("part", podInfo.name + "_4294000001");

                    ParsekLog.TestObserverForTesting = line =>
                    {
                        captured.Add(line);
                        priorObserver?.Invoke(line);
                    };
                    VesselCrewManifest manifest = roster.DefaultCrewForVessel(craftNode, null, true, false);
                    ParsekLog.TestObserverForTesting = priorObserver;

                    InGameAssert.IsNotNull(manifest, "DefaultCrewForVessel returned no manifest");

                    var seated = new List<string>();
                    for (int p = 0; p < manifest.PartManifests.Count; p++)
                    {
                        ProtoCrewMember[] crew = manifest.PartManifests[p].GetPartCrew();
                        for (int c = 0; c < crew.Length; c++)
                            if (crew[c] != null)
                                seated.Add(crew[c].name);
                    }

                    int pickLines = 0;
                    int refusalLines = 0;
                    for (int i = 0; i < captured.Count; i++)
                    {
                        string line = captured[i] ?? "";
                        if (line.Contains("[KerbalHirePatch] auto-hire applicant pick: skipped="))
                            pickLines++;
                        if (line.Contains("blocking hire for name=" + reservedName))
                            refusalLines++;
                    }

                    var hired = new List<string>();
                    for (int i = 0; i < roster.Count; i++)
                    {
                        ProtoCrewMember pcm = roster[i];
                        if (pcm == null || pcm.type != ProtoCrewMember.KerbalType.Crew) continue;
                        AutoHireRosterEntry before = FindAutoHireEntry(snapshot, pcm);
                        if (before == null || before.Type == ProtoCrewMember.KerbalType.Applicant)
                            hired.Add(pcm.name);
                    }

                    string seatedText = string.Join(",", seated.ToArray());
                    InGameAssert.AreEqual(shortfall, pickLines,
                        "Each short seat must reach GetNextApplicant and skip the reserved applicant (seated=" + seatedText + ")");
                    InGameAssert.AreEqual(0, refusalLines,
                        "KerbalHirePatch must never refuse '" + reservedName + "': auto-hire must not pick him");
                    InGameAssert.IsFalse(seated.Contains(reservedName),
                        "The reserved applicant '" + reservedName + "' must not be seated (seated=" + seatedText + ")");
                    InGameAssert.IsTrue(reserved.type == ProtoCrewMember.KerbalType.Applicant,
                        "The reserved applicant must stay an Applicant (type=" + reserved.type + ")");
                    InGameAssert.AreEqual(seated.Count, new HashSet<string>(seated).Count,
                        "No kerbal may be seated twice (seated=" + seatedText + ")");
                    InGameAssert.AreEqual(capacity, seated.Count,
                        "Every seat must be filled by the kept crew plus the hires (seated=" + seatedText + ")");
                    InGameAssert.AreEqual(shortfall, hired.Count,
                        "Auto-hire must hire one other kerbal per short seat (hired=" + string.Join(",", hired.ToArray()) + ")");
                    for (int i = 0; i < hired.Count; i++)
                        InGameAssert.IsTrue(seated.Contains(hired[i]),
                            "Hired kerbal '" + hired[i] + "' must be seated (seated=" + seatedText + ")");

                    ParsekLog.Info(AutoHireTag, string.Format(CultureInfo.InvariantCulture,
                        "auto-hire live check passed: reserved='{0}' pod={1} capacity={2} kept={3} shortfall={4} " +
                        "pickLines={5} hired={6} seated={7} refusals=0",
                        reservedName, podInfo.name, capacity, keptName ?? "(none)", shortfall,
                        pickLines, hired.Count, seated.Count));
                }
                finally
                {
                    ParsekLog.TestObserverForTesting = priorObserver;
                    game.Parameters.Difficulty.AutoHireCrews = priorAutoHire;
                    RestoreAutoHireRoster(roster, snapshot);
                    if (Funding.Instance != null && Funding.Instance.Funds != fundsBefore)
                        Funding.Instance.AddFunds(fundsBefore - Funding.Instance.Funds, TransactionReasons.None);
                    RemoveCommittedOverlayFixture(fixtureRecordingId, fixtureRecording);
                    CommittedFutureIndexCache.Invalidate("auto-hire live check teardown");
                }
            }
        }

        private sealed class AutoHireRosterEntry
        {
            internal ProtoCrewMember Member;
            internal string Name;
            internal ProtoCrewMember.KerbalType Type;
            internal ProtoCrewMember.RosterStatus Status;
        }

        private static AutoHireRosterEntry FindAutoHireEntry(List<AutoHireRosterEntry> snapshot, ProtoCrewMember pcm)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (ReferenceEquals(snapshot[i].Member, pcm))
                    return snapshot[i];
            return null;
        }

        private static ProtoCrewMember FirstApplicantInRosterOrder(KerbalRoster roster)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                ProtoCrewMember pcm = roster[i];
                if (pcm != null && pcm.type == ProtoCrewMember.KerbalType.Applicant)
                    return pcm;
            }
            return null;
        }

        private static AvailablePart ResolveAutoHirePod()
        {
            AvailablePart pod = PartLoader.getPartInfoByName("mk1-3pod");
            if (IsAutoHirePod(pod)) return pod;
            List<AvailablePart> parts = PartLoader.LoadedPartsList;
            if (parts == null) return null;
            for (int i = 0; i < parts.Count; i++)
                if (IsAutoHirePod(parts[i]))
                    return parts[i];
            return null;
        }

        // DefaultCrewForVessel auto-hires only for a control-source part with seats that is
        // not a KerbalSeat; 2+ seats so the shortfall loop runs at least twice.
        private static bool IsAutoHirePod(AvailablePart part)
        {
            return part != null && part.partPrefab != null
                && part.partPrefab.isControlSource > Vessel.ControlLevel.NONE
                && part.partPrefab.CrewCapacity >= 2
                && part.partPrefab.FindModuleImplementing<KerbalSeat>() == null;
        }

        // Reverts every type / status the cell or the stock call changed and removes every
        // kerbal the roster did not hold before (auto-hire's generated applicants and the
        // cell's own applicant, both created after the snapshot). Runs under the caller's
        // crew-event suppression.
        private static void RestoreAutoHireRoster(KerbalRoster roster, List<AutoHireRosterEntry> snapshot)
        {
            if (roster == null) return;
            int reverted = 0;
            var extra = new List<ProtoCrewMember>();
            for (int i = 0; i < roster.Count; i++)
            {
                ProtoCrewMember pcm = roster[i];
                if (pcm == null) continue;
                AutoHireRosterEntry before = FindAutoHireEntry(snapshot, pcm);
                if (before == null)
                {
                    extra.Add(pcm);
                    continue;
                }
                if (pcm.type != before.Type) { pcm.type = before.Type; reverted++; }
                if (pcm.rosterStatus != before.Status) { pcm.rosterStatus = before.Status; reverted++; }
            }
            for (int i = 0; i < extra.Count; i++)
                RemoveKerbalForOverlayTest(roster, extra[i]);
            ParsekLog.Info(AutoHireTag, string.Format(CultureInfo.InvariantCulture,
                "auto-hire live check teardown: reverted={0} removed={1}", reverted, extra.Count));
        }
    }
}
