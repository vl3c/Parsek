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
        };

        /// <summary>
        /// Members a catalogue state cannot reach, each with the reason. An exemption is
        /// a STATEMENT, so it is checked: a member that stops existing (or stops being
        /// unreachable) reds the cell below rather than silently excusing a gap.
        /// </summary>
        private static readonly Dictionary<string, string> Exempt =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // GameActionType has ~40 members and Build() branches on ten. The guard
                // is scoped to that method's BODY, so the rest never appear as branches
                // and need no entry here - which is the point of scoping.
            };

        [Fact]
        public void EveryGuardedEnumsBranchSetIsClaimedByACatalogueState()
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuiMockState state in GuiMockCatalogue.All)
                foreach (string cover in state.Covers)
                    claimed.Add(cover);

            int branchesSeen = 0;
            var misses = new List<string>();
            foreach (GuardedEnum guarded in Guarded)
            {
                List<string> branches = BranchMembers(guarded);
                Assert.True(branches.Count > 0,
                    "no " + guarded.ShortName + " branch found in " + guarded.RelativePath
                    + (guarded.MethodMarker == null ? "" : " / " + guarded.MethodMarker)
                    + " - the scan is broken (moved file, renamed method), not the source. "
                    + "Never-drawn is a FAILURE here, not a skip.");
                branchesSeen += branches.Count;

                foreach (string member in branches)
                {
                    string key = guarded.ShortName + "." + member;
                    if (claimed.Contains(key)) continue;
                    if (Exempt.ContainsKey(key)) continue;
                    misses.Add(key + "  (" + guarded.Why + "; add a state to "
                               + "Source/Parsek/UI/Gallery/ whose Covers names \""
                               + key + "\")");
                }
            }

            // A hardcoded FLOOR so the loop above cannot go vacuous: 4 RosterStatus + 3
            // ChainMemberStatus + 3 KerbalEndState + 9 TerminalState + 10 GameActionType.
            Assert.True(branchesSeen >= 29,
                "the guarded scan found only " + branchesSeen + " branches across "
                + Guarded.Length + " enums; the parse or the source moved");

            Assert.True(misses.Count == 0,
                "catalogue states claim no branch key for:\n  " + string.Join("\n  ", misses));
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
        public void EveryCoverKeyNamesARealMemberOfAGuardedEnum()
        {
            // The mirror direction: a state that claims a key nothing guards is coverage
            // theatre, and a TYPO in a cover key would otherwise leave the real member
            // uncovered while the catalogue looked complete.
            var valid = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuardedEnum guarded in Guarded)
                foreach (string member in Enum.GetNames(guarded.EnumType))
                    valid.Add(guarded.ShortName + "." + member);

            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                foreach (string cover in state.Covers)
                {
                    Assert.True(valid.Contains(cover),
                        "state '" + state.Id + "' claims branch key '" + cover
                        + "', which is not a member of any guarded enum. Either the key "
                        + "is a typo or the enum belongs under the guard.");
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
