using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// THE completeness guard (design-gui-state-gallery.md section 11): every enum value a
    /// supported window's draw branches switch on must be claimed by at least one catalogue
    /// state, and the guard must red in the same build that adds a branch.
    ///
    /// <para><b>It is derived, not listed.</b> The branch set for each enum is read out of
    /// the COMMENT-STRIPPED, LITERAL-MASKED source of the file that switches on it, so a
    /// new <c>case RosterStatus.X:</c> in the draw method fails this cell locally rather
    /// than shipping as a state nobody photographed. Comments are stripped first for the
    /// reason every source gate in this repo strips them: a scope claim in prose would be
    /// read as a branch and the guard would pass GREEN against a source that says the
    /// opposite.</para>
    ///
    /// <para><b>There is no Roslyn in this assembly</b> (<c>SourceScanText.cs:16</c> says
    /// so, and a repo-wide grep for <c>Microsoft.CodeAnalysis</c> returns only that
    /// comment), so the house substitute is <see cref="SourceScanText"/>: a
    /// length-preserving comment stripper and literal masker, already consumed by 27 test
    /// classes.</para>
    ///
    /// <para><b>Three properties, following <c>MissionsWindowLoopGateTests</c>:</b>
    /// never-drawn is a FAILURE not a skip; unparseable structure is a FAILURE not a pass;
    /// and the scan carries mutation-verified anti-vacuity decoys proving it reds when the
    /// branch exists only in a comment.</para>
    /// </summary>
    public class GuiMockCompletenessTests
    {
        /// <summary>
        /// One guarded enum: the type, the short name a branch spells it by, and the
        /// source scope whose branches define what must be covered.
        ///
        /// <para><b>Why a SCOPE and not just a file.</b> The Kerbals draw switches on its
        /// three enums in place, so the whole file is the scope. <c>TerminalState</c> is
        /// different: the Structure window draws a terminal STATUS cell but branches on
        /// nothing - the vocabulary is produced one layer up by
        /// <c>MissionCompositionBuilder.TerminalName</c>, so THAT method's body is the
        /// scope. Scoping to the method is what keeps the guard about the words the window
        /// can show rather than about every mention of the enum in a 900-line file.</para>
        /// </summary>
        private sealed class GuardedEnum
        {
            internal Type EnumType;
            internal string ShortName;
            internal string RelativePath;
            /// <summary>The method whose body is the scope, or null for the whole
            /// file.</summary>
            internal string MethodMarker;
            /// <summary>Why this enum is guarded at all, in one line.</summary>
            internal string Why;
        }

        private static readonly GuardedEnum[] Guarded =
        {
            new GuardedEnum
            {
                EnumType = typeof(KerbalsPresentation.RosterStatus),
                ShortName = "RosterStatus",
                RelativePath = "UI/KerbalsWindowUI.cs",
                Why = "StyleForRosterStatus tints a roster row per status; an unphotographed "
                      + "status is a colour nobody has seen",
            },
            new GuardedEnum
            {
                EnumType = typeof(KerbalsWindowUI.ChainMemberStatus),
                ShortName = "ChainMemberStatus",
                RelativePath = "UI/KerbalsWindowUI.cs",
                Why = "the expanded chain lines carry a per-member tag and style",
            },
            new GuardedEnum
            {
                EnumType = typeof(KerbalEndState),
                ShortName = "KerbalEndState",
                RelativePath = "UI/KerbalsWindowUI.cs",
                Why = "the Flights tab's outcome word and its style come off this enum",
            },
            new GuardedEnum
            {
                EnumType = typeof(TerminalState),
                ShortName = "TerminalState",
                RelativePath = "MissionComposition.cs",
                MethodMarker = "internal static string TerminalName(TerminalState? t)",
                Why = "the Structure window's terminal Event / Status cell draws exactly "
                      + "this vocabulary, and six of its words have no capture",
            },
            new GuardedEnum
            {
                EnumType = typeof(GameActionType),
                ShortName = "GameActionType",
                RelativePath = "UI/CareerStateWindowUI.cs",
                MethodMarker = "internal static CareerStateViewModel Build(",
                Why = "the VM walk's branches ARE the Career window's row variants",
            },
            new GuardedEnum
            {
                EnumType = typeof(CareerStateWindowUI.TimelineEndKind),
                ShortName = "TimelineEndKind",
                RelativePath = "UI/CareerStateWindowUI.cs",
                MethodMarker = "internal static string FormatTimelineEnd(",
                Why = "the Career Timeline-end cell words each recorded outcome differently, "
                      + "and a failure is drawn in the alert colour",
            },
        };

        /// <summary>
        /// Members NO catalogue state can reach, each with the reason it draws nothing.
        ///
        /// <para>An exemption is a STATEMENT about the product, so it is checked: a member
        /// that stops existing - or stops being unreachable - reds
        /// <see cref="EveryExemptionNamesARealMemberOfAGuardedEnum"/> rather than silently
        /// excusing a gap. The list is short on purpose; an exemption is the answer of
        /// last resort, after "add a state" and "the enum does not belong under the
        /// guard".</para>
        /// </summary>
        private static readonly Dictionary<string, string> Exempt =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // The chain builder emits Retired / Active / Displaced and nothing else
                // (KerbalsPresentation.BuildChainMembers is an exhaustive if/else over
                // those three), so Unknown is a declared-but-unreachable member. The
                // window HAS a "(?)" arm for it, which is why it stays in the enum.
                { "ChainMemberStatus.Unknown",
                  "BuildChainMembers emits only Retired / Active / Displaced, so no "
                  + "recording can produce an Unknown chain member" },

                // None is the absence of an outcome: the Timeline-end cell is empty (and
                // the column is not drawn at all while every row of a tab is None).
                { "TimelineEndKind.None",
                  "an empty Timeline-end cell; every row the recorded timeline leaves "
                  + "untouched already draws it" },

                // Every GameActionType the Career VM walk does NOT branch on. The walk
                // switches on ten; the rest are other subsystems' rows (recordings,
                // kerbals, routes, science, funds, reputation) that this window never
                // renders. Listed as ONE reason rather than forty entries by the
                // scope-derived check below - see NonCareerGameActionTypes.
            };

        /// <summary>
        /// The <c>GameActionType</c> members the Career VM walk deliberately does not
        /// branch on, derived from its own comment-stripped BODY rather than listed.
        ///
        /// <para>That derivation is the honest form: the enum has around forty members and
        /// the window renders ten of them, so a hand-kept exemption list would be
        /// thirty rows of "this window does not draw route rows" - and would go stale
        /// silently the moment the walk grew an arm. The reflection guard below therefore
        /// asks only about the members the walk MENTIONS, and this method is what makes
        /// that set derived instead of pinned.</para>
        /// </summary>
        private static HashSet<string> CareerWalkBranchMembers()
        {
            GuardedEnum gameActions = Guarded.Single(g => g.ShortName == "GameActionType");
            return new HashSet<string>(BranchMembers(gameActions), StringComparer.Ordinal);
        }

        /// <summary>
        /// The BOOL-DRIVEN branches, each with a predicate proving the catalogue reaches
        /// it. Reflection cannot see these - several of the most consequential cells in
        /// these windows are decided by a bool rather than by an enum - so each is a NAMED
        /// branch key a state must claim, and the predicate is what stops a claimed key
        /// that nothing produces from reading as coverage.
        /// </summary>
        private static Dictionary<string, Func<bool>> BoolBranches()
            => new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
            {
                { "CareerBanner.Divergent",
                  () => AnyCareer(vm => vm.HasDivergence) },
                { "ContractRow.IsPendingAccept",
                  () => AnyCareer(vm => vm.Contracts.ProjectedRows.Any(
                      r => r.IsPendingAccept)) },
                { "ContractRow.IsClosingByTimelineEnd",
                  () => AnyCareer(vm => vm.Contracts.CurrentRows.Any(
                      r => r.IsClosingByTimelineEnd)) },
                { "StrategyRow.IsPendingActivate",
                  () => AnyCareer(vm => vm.Strategies.ProjectedRows.Any(
                      r => r.IsPendingActivate)) },
                { "StrategyRow.IsClosingByTimelineEnd",
                  () => AnyCareer(vm => vm.Strategies.CurrentRows.Any(
                      r => r.IsClosingByTimelineEnd)) },
                { "FacilityRow.HasUpcomingChange",
                  () => AnyCareer(vm => vm.Facilities.Rows.Any(
                      r => r.HasUpcomingChange && r.CurrentLevel != r.ProjectedLevel)) },
                { "FacilityRow.CurrentDestroyed",
                  () => AnyCareer(vm => vm.Facilities.Rows.Any(
                      r => r.CurrentDestroyed && r.ProjectedDestroyed)) },
                { "FacilityRow.RepairPending",
                  () => AnyCareer(vm => vm.Facilities.Rows.Any(
                      r => r.CurrentDestroyed && !r.ProjectedDestroyed)) },
                { "MilestoneRow.IsPendingCredit",
                  () => AnyCareer(vm => vm.Milestones.Rows.Any(r => r.IsPendingCredit)) },
                { "MilestoneRow.ZeroReward",
                  () => AnyCareer(vm => vm.Milestones.Rows.Any(
                      r => r.FundsAwarded == 0f && r.RepAwarded == 0f
                           && r.ScienceAwarded == 0f)) },
                { "RosterRow.StatusTooltip",
                  () => AnyKerbals(vm => AllRosterRows(vm).Any(
                      r => !string.IsNullOrEmpty(r.StatusTooltipText))) },
                { "RosterRow.Chain",
                  () => AnyKerbals(vm => AllRosterRows(vm).Any(
                      r => r.Chain != null && r.Chain.Count > 1)) },
                { "FlightRow.MultiSegment",
                  () => AnyKerbals(vm => vm.Flights != null && vm.Flights.Any(
                      g => g.Rows != null && g.Rows.Any(r => r.SegmentCount > 1))) },
                { "FlightRow.CrewNote",
                  () => AnyKerbals(vm => vm.Flights != null && vm.Flights.Any(
                      g => g.Rows != null && g.Rows.Any(
                          r => r.CrewNoteText != KerbalsPresentation.EmptyCell))) },
                { "StructureStep.CollapsedRun",
                  () => AnyStructure(steps => steps.Any(
                      st => st.Label != null && st.Label.Contains(" x"))) },
                { "StructureStep.RouteOrigin",
                  () => AnyStructure(steps => steps.Any(
                      st => st.Kind == StructureStepKind.Origin)) },
            };

        /// <summary>The bool-branch keys, so the cover-key validity cell accepts them
        /// beside the enum members without a second copy of the list.</summary>
        internal static IEnumerable<string> DeclaredBoolBranchKeys()
            => BoolBranches().Keys;

        [Fact]
        public void EveryGuardedEnumMemberIsClaimedOrExemptedOrOutOfTheWindowsReach()
        {
            // REFLECTION over Enum.GetValues, which is what design 11.1 specifies and what
            // the first version did NOT do: it collected `EnumName.Member` MENTIONS from
            // the draw source with a regex, so a NEW enum member with no `case` arm stayed
            // green - the guard could not see a member nobody had written about yet. It
            // also required only 4 of 6 RosterStatus members for that reason.
            //
            // The source scan is still here, in the narrow role it is actually good for:
            // deciding which GameActionType members the Career walk renders at all (that
            // enum has around forty members and the window draws ten). For every OTHER
            // guarded enum the whole member set must be claimed or exempted.
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuiMockState state in GuiMockCatalogue.All)
                foreach (string cover in state.Covers)
                    claimed.Add(cover);

            HashSet<string> careerWalk = CareerWalkBranchMembers();
            Assert.True(careerWalk.Count >= 10,
                "the Career walk scan found only " + careerWalk.Count + " branches; the "
                + "parse or the method moved");

            int membersChecked = 0;
            var misses = new List<string>();
            foreach (GuardedEnum guarded in Guarded)
            {
                string[] members = Enum.GetNames(guarded.EnumType);
                Assert.True(members.Length > 0,
                    guarded.ShortName + " has no members; reflection is broken");

                foreach (string member in members)
                {
                    string key = guarded.ShortName + "." + member;

                    // GameActionType is scoped to what the window's own walk renders; the
                    // rest of that enum belongs to other subsystems entirely.
                    if (guarded.ShortName == "GameActionType" && !careerWalk.Contains(member))
                        continue;

                    membersChecked++;
                    if (claimed.Contains(key)) continue;
                    if (Exempt.ContainsKey(key)) continue;
                    misses.Add(key + "  (" + guarded.Why + "; add a state to "
                               + "Source/Parsek/UI/Gallery/ whose Covers names \""
                               + key + "\", or an Exempt entry saying why it draws "
                               + "nothing)");
                }
            }

            // A hardcoded FLOOR so the loop cannot go vacuous: 6 RosterStatus + 4
            // ChainMemberStatus + 4 KerbalEndState + 9 TerminalState + 10 rendered
            // GameActionType = 33.
            Assert.True(membersChecked >= 33,
                "the guard checked only " + membersChecked + " members across "
                + Guarded.Length + " enums; reflection or the scoping broke");

            Assert.True(misses.Count == 0,
                "catalogue states claim no branch key for:\n  "
                + string.Join("\n  ", misses));
        }

        [Fact]
        public void TheBoolDrivenBranchesAreClaimedByNamedKeysToo()
        {
            // The OTHER half of design 11.1, and the half the first version had no reach
            // into at all: several of these windows' most consequential cells are decided
            // by a BOOL rather than by an enum - the divergence banner, (pending),
            // (closing), (destroyed), (upcoming), the pending-accept split. Reflection
            // cannot see those, so each is a NAMED branch key a state must claim, checked
            // here against the real formatter that renders it.
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuiMockState state in GuiMockCatalogue.All)
                foreach (string cover in state.Covers)
                    claimed.Add(cover);

            Dictionary<string, Func<bool>> boolBranches = BoolBranches();
            var unproduced = new List<string>();
            foreach (var pair in boolBranches)
            {
                if (!pair.Value())
                {
                    unproduced.Add(pair.Key + " is claimed by the guard and NO catalogue "
                                   + "state produces it");
                }
            }
            Assert.True(unproduced.Count == 0,
                string.Join("\n  ", unproduced));

            // And every one of them is NAMED by at least one state, so a reader of a
            // state's Covers can see which bool-driven cell it exists for.
            var unclaimed = new List<string>();
            foreach (string key in boolBranches.Keys)
                if (!claimed.Contains(key)) unclaimed.Add(key);
            Assert.True(unclaimed.Count == 0,
                "these bool-driven branches are produced but no state CLAIMS them, so "
                + "nothing ties the picture to the branch:\n  "
                + string.Join("\n  ", unclaimed));
        }

        private static bool AnyCareer(
            Func<CareerStateWindowUI.CareerStateViewModel, bool> match)
        {
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.CareerWindow))
            {
                if (match(state.Build().Career.Value)) return true;
            }
            return false;
        }

        private static bool AnyKerbals(Func<KerbalsWindowUI.KerbalsViewModel, bool> match)
        {
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.KerbalsWindow))
            {
                if (match(state.Build().Kerbals.Value)) return true;
            }
            return false;
        }

        private static bool AnyStructure(Func<List<StructureStep>, bool> match)
        {
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.StructureWindow))
            {
                if (match(state.Build().Structure.Steps)) return true;
            }
            return false;
        }

        private static IEnumerable<KerbalsPresentation.RosterRow> AllRosterRows(
            KerbalsWindowUI.KerbalsViewModel vm)
        {
            KerbalsPresentation.RosterRowSet set = vm.Roster;
            if (set.Involved != null)
                for (int i = 0; i < set.Involved.Count; i++) yield return set.Involved[i];
            if (set.Plain != null)
                for (int i = 0; i < set.Plain.Count; i++) yield return set.Plain[i];
        }

        [Fact]
        public void EveryExemptionNamesARealMemberOfAGuardedEnum()
        {
            // An exemption is a statement about the product, so it is checked. A renamed
            // or deleted member turns its exemption into a silent excuse otherwise.
            foreach (string key in Exempt.Keys)
            {
                string[] parts = key.Split('.');
                Assert.Equal(2, parts.Length);
                GuardedEnum guarded = Guarded.FirstOrDefault(g => g.ShortName == parts[0]);
                Assert.True(guarded != null,
                    "exemption '" + key + "' names no guarded enum");
                Assert.True(Enum.GetNames(guarded.EnumType).Contains(parts[1]),
                    "exemption '" + key + "' names no member of " + guarded.ShortName);
                Assert.False(string.IsNullOrWhiteSpace(Exempt[key]),
                    "exemption '" + key + "' carries no reason");
            }
        }

        [Fact]
        public void EveryCoverKeyNamesARealEnumMemberOrADeclaredBoolBranch()
        {
            // The mirror direction: a state that claims a key nothing guards is coverage
            // theatre, and a TYPO in a cover key would otherwise leave the real member
            // uncovered while the catalogue looked complete.
            var valid = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuardedEnum guarded in Guarded)
                foreach (string member in Enum.GetNames(guarded.EnumType))
                    valid.Add(guarded.ShortName + "." + member);
            foreach (string key in DeclaredBoolBranchKeys()) valid.Add(key);

            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                foreach (string cover in state.Covers)
                {
                    Assert.True(valid.Contains(cover),
                        "state '" + state.Id + "' claims branch key '" + cover
                        + "', which is neither a member of a guarded enum nor a declared "
                        + "bool-driven branch. Either the key is a typo, or the branch "
                        + "belongs under the guard.");
                }
            }
        }

        [Fact]
        public void EveryGuardedEnumIsOneTheSupportedWindowsActuallyBranchOn()
        {
            // The source gate over the PINNED LIST: every `case <Type>.<Member>:` in the
            // three supported windows' draw files must belong to a guarded enum or be
            // named as deliberately out of scope. Without it, a new enum branch in a
            // window would be invisible to the guard above - which only looks at the
            // enums it was told about.
            var guardedNames = new HashSet<string>(
                Guarded.Select(g => g.ShortName), StringComparer.Ordinal);

            // Out of scope, with the reason. `StructureStepKind` is the interesting one:
            // the Structure window draws no branch on it at all (the Kind field is not
            // rendered), so covering it would be a claim about a picture that does not
            // vary.
            var outOfScope = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "StructureStepKind", "the Structure window renders no cell from Kind and "
                                       + "branches on nothing; Stop is reserved for an "
                                       + "unbuilt multi-stop form" },
            };

            var caseRe = new Regex(
                @"\bcase\s+([A-Za-z_][A-Za-z0-9_.]*)\.([A-Za-z_][A-Za-z0-9_]*)\s*:",
                RegexOptions.CultureInvariant);

            var unguarded = new List<string>();
            foreach (string rel in new[]
                     {
                         "UI/KerbalsWindowUI.cs",
                         "UI/CareerStateWindowUI.cs",
                         "UI/StructureListWindowUI.cs",
                     })
            {
                string prepared = PreparedSource(rel);
                foreach (Match m in caseRe.Matches(prepared))
                {
                    string qualified = m.Groups[1].Value;
                    string shortName = qualified.Contains(".")
                        ? qualified.Substring(qualified.LastIndexOf('.') + 1)
                        : qualified;
                    if (guardedNames.Contains(shortName)) continue;
                    if (outOfScope.ContainsKey(shortName)) continue;
                    unguarded.Add(rel + ": case " + qualified + "." + m.Groups[2].Value);
                }
            }

            Assert.True(unguarded.Count == 0,
                "a supported window branches on an enum the completeness guard does not "
                + "know about. Add it to Guarded (with catalogue states) or to outOfScope "
                + "(with a reason):\n  " + string.Join("\n  ", unguarded.Distinct()));
        }

        // ----- anti-vacuity -----

        [Fact]
        public void TheBranchScanDoesNotReadComments()
        {
            // Over a SYNTHETIC source, because the real files DO name these members in
            // prose: KerbalsWindowUI's own comments discuss Lost and Retired rows, and
            // MissionComposition's header explains what Disassembled means. A scan that
            // read comments would report those as branches and pass against a source
            // that has no such case.
            string synthetic = string.Join("\n", new[]
            {
                "// case RosterStatus.Retired: this is a COMMENT, not a branch.",
                "/// <summary>Mentions RosterStatus.Reserved in prose.</summary>",
                "switch (s)",
                "{",
                "    case RosterStatus.Lost: return deadStyle;",
                "    default: return null;",
                "}",
            });
            List<string> found = MembersIn(
                SourceScanText.StripCommentsAndMaskLiterals(synthetic),
                "RosterStatus", typeof(KerbalsPresentation.RosterStatus));
            Assert.Equal(new[] { "Lost" }, found);
        }

        [Fact]
        public void TheBranchScanDoesNotReadStringLiterals()
        {
            // The same trap one layer down: a log line naming a member is not a branch.
            string synthetic = string.Join("\n", new[]
            {
                "ParsekLog.Info(\"UI\", \"case KerbalEndState.Recovered fired\");",
                "case KerbalEndState.Dead: return deadStyle;",
            });
            List<string> found = MembersIn(
                SourceScanText.StripCommentsAndMaskLiterals(synthetic),
                "KerbalEndState", typeof(KerbalEndState));
            Assert.Equal(new[] { "Dead" }, found);
        }

        [Fact]
        public void TheMethodScopeReallyNarrowsTheScan()
        {
            // Proves the scoping is load-bearing rather than decorative: the
            // GameActionType guard reads Build()'s body, and Build() branches on ten of
            // the enum's members while the whole file names more.
            GuardedEnum gameActions = Guarded.Single(g => g.ShortName == "GameActionType");
            List<string> scoped = BranchMembers(gameActions);
            Assert.Equal(10, scoped.Count);

            List<string> wholeFile = MembersIn(
                PreparedSource(gameActions.RelativePath), "GameActionType",
                typeof(GameActionType));
            Assert.True(wholeFile.Count >= scoped.Count);
            foreach (string member in scoped) Assert.Contains(member, wholeFile);
        }

        [Fact]
        public void TheSourceTreeIsActuallyReadable()
        {
            // The floor under every scan above: a moved repo root would make each of them
            // pass on an empty string.
            foreach (GuardedEnum guarded in Guarded)
            {
                string prepared = PreparedSource(guarded.RelativePath);
                Assert.True(prepared.Length > 1000,
                    "prepared source for " + guarded.RelativePath + " is "
                    + prepared.Length + " chars; the path moved");
            }
        }

        // ----- the scan -----

        private static List<string> BranchMembers(GuardedEnum guarded)
        {
            string prepared = PreparedSource(guarded.RelativePath);
            if (guarded.MethodMarker != null)
                prepared = MethodBody(prepared, guarded.MethodMarker, guarded.RelativePath);
            return MembersIn(prepared, guarded.ShortName, guarded.EnumType);
        }

        /// <summary>
        /// Every member of <paramref name="enumType"/> referenced as
        /// <c>&lt;ShortName&gt;.&lt;Member&gt;</c> in prepared source, sorted. Intersected
        /// with the real member set, so a property or nested type spelled the same way
        /// cannot inflate the branch set.
        /// </summary>
        private static List<string> MembersIn(string prepared, string shortName, Type enumType)
        {
            var members = new HashSet<string>(Enum.GetNames(enumType), StringComparer.Ordinal);
            var re = new Regex(@"\b" + Regex.Escape(shortName) + @"\.([A-Za-z_][A-Za-z0-9_]*)\b",
                               RegexOptions.CultureInvariant);
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in re.Matches(prepared))
            {
                string member = m.Groups[1].Value;
                if (members.Contains(member)) found.Add(member);
            }
            return found.ToList();
        }

        /// <summary>The brace-matched body of one method, located by its exact signature
        /// text in prepared source. Unparseable structure is a FAILURE, never a pass:
        /// <see cref="SourceScanText.BraceMatchedBlock"/> throws on an imbalance.</summary>
        private static string MethodBody(string prepared, string marker, string rel)
        {
            int at = prepared.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0,
                "method marker not found in " + rel + " (the signature moved, so this "
                + "guard is vacuous): " + marker);
            int open = prepared.IndexOf('{', at + marker.Length);
            Assert.True(open > at, "no body brace after the marker in " + rel);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static string PreparedSource(string relativePath)
        {
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek",
                                       relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "source moved, this guard is vacuous: " + path);
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
