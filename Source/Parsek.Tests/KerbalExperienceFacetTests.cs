using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the P9a kerbal-experience facet.
    ///
    /// <para>
    /// The premise, verified against the decompiled KSP 1.12.5 <c>Assembly-CSharp</c>: XP is
    /// DERIVED, not stored. <c>ProtoCrewMember.UpdateExperience()</c> recomputes
    /// <c>experience</c> from <c>KerbalRoster.CalculateExperience(careerLog)</c>, which walks
    /// <c>FlightLog.GetFlights()</c> — entries grouped by their <c>flight</c> number, scored
    /// per group with cross-group dedup. So the recorded unit is the CAREER-LOG ENTRY (with
    /// its flight number), never a number, and the re-assert is a set-union append.
    /// </para>
    /// </summary>
    public class KerbalExperienceFacetTests
    {
        // ================================================================
        // Wire format
        // ================================================================

        [Fact]
        public void CareerEntry_RoundtripsThroughTheWireFormat()
        {
            var entries = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),
                new KerbalCareerLogEntry(1, "Flyby", "Mun"),
                new KerbalCareerLogEntry(1, "Recover", ""),
            };

            var parsed = KerbalCareerLogEntry.ParseSet(KerbalCareerLogEntry.FormatSet(entries));

            Assert.Equal(entries, parsed);
        }

        [Fact]
        public void CareerEntry_EscapesTheSeparatorsItsOwnFormatUses()
        {
            // Entry targets are body names Parsek does not control, and a planet pack could
            // introduce a comma or a semicolon. An unescaped one would corrupt not just this
            // entry but the whole `detail` field it rides in. Fails if the escaping is dropped.
            var entries = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(2, "Land", "Weird,Body|Name;X"),
                new KerbalCareerLogEntry(2, "Orbit", "Back\\slash"),
            };

            string encoded = KerbalCareerLogEntry.FormatSet(entries);
            var parsed = KerbalCareerLogEntry.ParseSet(encoded);

            Assert.Equal(entries, parsed);
        }

        [Fact]
        public void CareerEntry_ParseSkipsMalformedSegmentsWithoutLosingTheRest()
        {
            // One corrupt segment must not cost the kerbal every other entry in the row.
            var parsed = KerbalCareerLogEntry.ParseSet("0,Launch,Kerbin|garbage|notanint,Orbit,Kerbin|1,Flyby,Mun");

            Assert.Equal(2, parsed.Count);
            Assert.Contains(new KerbalCareerLogEntry(0, "Launch", "Kerbin"), parsed);
            Assert.Contains(new KerbalCareerLogEntry(1, "Flyby", "Mun"), parsed);
        }

        [Fact]
        public void CareerEntry_EmptyAndNullParseToEmpty()
        {
            Assert.Empty(KerbalCareerLogEntry.ParseSet(null));
            Assert.Empty(KerbalCareerLogEntry.ParseSet(""));
            Assert.Equal("", KerbalCareerLogEntry.FormatSet(null));
            Assert.Equal("", KerbalCareerLogEntry.FormatSet(new List<KerbalCareerLogEntry>()));
        }

        [Fact]
        public void CareerEntry_FlightNumberIsPartOfIdentity()
        {
            // Load-bearing: stock groups entries by flight and scores per group, so the same
            // (type, target) under a different flight is a DIFFERENT entry. Fails if equality
            // ever collapses to (type, target) — the union would then silently drop a
            // second flight's repeat of an experience the kerbal genuinely re-earned.
            var a = new KerbalCareerLogEntry(0, "Orbit", "Kerbin");
            var b = new KerbalCareerLogEntry(1, "Orbit", "Kerbin");

            Assert.NotEqual(a, b);

            var set = new KerbalCareerEntries();
            set.UnionWith(new List<KerbalCareerLogEntry> { a, b });
            Assert.Equal(2, set.Count);
        }

        // ================================================================
        // Accumulator: monotone set-union
        // ================================================================

        [Fact]
        public void Accumulator_UnionIsIdempotent()
        {
            var set = new KerbalCareerEntries();
            var entries = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),
            };

            Assert.Equal(2, set.UnionWith(entries));
            Assert.Equal(0, set.UnionWith(entries));
            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void Accumulator_SkipsTypelessEntries()
        {
            var set = new KerbalCareerEntries();
            set.UnionWith(new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "", "Kerbin"),
                new KerbalCareerLogEntry(0, null, "Kerbin"),
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),
            });

            Assert.Equal(1, set.Count);
        }

        [Fact]
        public void Accumulator_OrderedListIsDeterministic()
        {
            var a = new KerbalCareerEntries();
            a.UnionWith(new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(1, "Orbit", "Mun"),
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Flyby", "Mun"),
            });
            var b = new KerbalCareerEntries();
            b.UnionWith(new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "Flyby", "Mun"),
                new KerbalCareerLogEntry(1, "Orbit", "Mun"),
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
            });

            Assert.Equal(a.ToOrderedList(), b.ToOrderedList());
            Assert.Equal(0, a.ToOrderedList()[0].Flight);
        }

        // ================================================================
        // Converter
        // ================================================================

        private static GameStateEvent XpEvent(string kerbal, string entries, string trait = "Pilot")
        {
            return new GameStateEvent
            {
                ut = 1000.0,
                eventType = GameStateEventType.ExperienceGained,
                key = kerbal,
                detail = $"flight=0;entries={entries}" + (trait != null ? $";trait={trait}" : ""),
            };
        }

        [Fact]
        public void Converter_MapsExperienceGainedToKerbalExperience()
        {
            string encoded = KerbalCareerLogEntry.FormatSet(new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),
            });

            var action = GameStateEventConverter.ConvertEvent(XpEvent("Jebediah Kerman", encoded), "rec-1");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.KerbalExperience, action.Type);
            Assert.Equal("Jebediah Kerman", action.KerbalName);
            Assert.Equal("Pilot", action.KerbalRole);
            Assert.Equal("rec-1", action.RecordingId);
            Assert.Equal(encoded, action.KerbalCareerEntries);
        }

        [Fact]
        public void Converter_DropsRowsWithNoEntriesOrNoKerbal()
        {
            // An XP action with an empty entry set has nothing to re-assert; emitting one
            // would put a permanently inert row in every recovery's ledger slice.
            Assert.Null(GameStateEventConverter.ConvertEvent(XpEvent("Jeb", ""), "rec-1"));
            Assert.Null(GameStateEventConverter.ConvertEvent(XpEvent("", "0,Orbit,Kerbin"), "rec-1"));
        }

        // ================================================================
        // Codec
        // ================================================================

        [Fact]
        public void Codec_RoundtripsAKerbalExperienceAction()
        {
            string encoded = KerbalCareerLogEntry.FormatSet(new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(3, "Land", "Mun"),
                new KerbalCareerLogEntry(3, "Recover", ""),
            });
            var action = new GameAction
            {
                UT = 4242.0,
                Type = GameActionType.KerbalExperience,
                RecordingId = "rec-9",
                KerbalName = "Valentina Kerman",
                KerbalRole = "Pilot",
                KerbalCareerEntries = encoded,
            };

            var parent = new ConfigNode("ROOT");
            action.SerializeInto(parent);
            var restored = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

            Assert.Equal(GameActionType.KerbalExperience, restored.Type);
            Assert.Equal("Valentina Kerman", restored.KerbalName);
            Assert.Equal("Pilot", restored.KerbalRole);
            Assert.Equal(encoded, restored.KerbalCareerEntries);
            Assert.Equal(
                KerbalCareerLogEntry.ParseSet(encoded),
                KerbalCareerLogEntry.ParseSet(restored.KerbalCareerEntries));
        }

        // ================================================================
        // Tombstone eligibility
        // ================================================================

        [Fact]
        public void KerbalExperience_IsSupersedeTombstoneEligible()
        {
            // Explicitly listed rather than left to the default, which PRESERVES unknown
            // types. Without this, a superseded branch's XP row survives the merge and the
            // monotone re-assert puts that branch's experience back on the next recalc —
            // re-earning XP for a flight the merge deleted. Fails if the case is removed.
            var action = new GameAction
            {
                ActionId = "act-1",
                RecordingId = "rec-1",
                Type = GameActionType.KerbalExperience,
                KerbalName = "Jeb",
            };

            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(action));
        }

        [Fact]
        public void KerbalExperience_NullScopedIsStillNeverTombstoned()
        {
            var action = new GameAction
            {
                ActionId = "act-1",
                RecordingId = null,
                Type = GameActionType.KerbalExperience,
            };

            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(action));
        }

        // ================================================================
        // Module accumulation
        // ================================================================

        private static GameAction XpAction(string kerbal, params KerbalCareerLogEntry[] entries)
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                UT = 100.0,
                Type = GameActionType.KerbalExperience,
                RecordingId = "rec-1",
                KerbalName = kerbal,
                KerbalCareerEntries = KerbalCareerLogEntry.FormatSet(new List<KerbalCareerLogEntry>(entries)),
            };
        }

        [Fact]
        public void Module_AccumulatesPerKerbalAndUnionsAcrossActions()
        {
            var module = new KerbalsModule();
            module.ProcessAction(XpAction("Jeb",
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin")));
            module.ProcessAction(XpAction("Jeb",
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),   // duplicate
                new KerbalCareerLogEntry(1, "Flyby", "Mun")));
            module.ProcessAction(XpAction("Bill",
                new KerbalCareerLogEntry(0, "Launch", "Kerbin")));

            var jeb = module.GetCareerEntriesForTesting("Jeb");
            Assert.NotNull(jeb);
            Assert.Equal(3, jeb.Count);
            Assert.Equal(1, module.GetCareerEntriesForTesting("Bill").Count);
            Assert.Null(module.GetCareerEntriesForTesting("Bob"));
        }

        [Fact]
        public void Module_ResetClearsTheAccumulator()
        {
            // Every walk rebuilds from the current ELS. Fails if Reset misses the dict — a
            // tombstoned XP row's entries would survive into the next walk and be re-asserted
            // forever, which is the exact failure the tombstone eligibility exists to prevent.
            var module = new KerbalsModule();
            module.ProcessAction(XpAction("Jeb", new KerbalCareerLogEntry(0, "Orbit", "Kerbin")));
            Assert.NotNull(module.GetCareerEntriesForTesting("Jeb"));

            module.Reset();

            Assert.Null(module.GetCareerEntriesForTesting("Jeb"));
        }

        [Fact]
        public void Module_IgnoresRowsWithNoEntries()
        {
            var module = new KerbalsModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.KerbalExperience,
                KerbalName = "Jeb",
                KerbalCareerEntries = "",
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.KerbalExperience,
                KerbalName = null,
                KerbalCareerEntries = "0,Orbit,Kerbin",
            });

            Assert.Null(module.GetCareerEntriesForTesting("Jeb"));
        }

        // ================================================================
        // The monotone re-assert decision core
        // ================================================================

        private static KerbalCareerEntries Ledger(params KerbalCareerLogEntry[] entries)
        {
            var set = new KerbalCareerEntries();
            set.UnionWith(new List<KerbalCareerLogEntry>(entries));
            return set;
        }

        [Fact]
        public void Reassert_ReportsOnlyTheEntriesTheRosterIsMissing()
        {
            var ledger = Ledger(
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),
                new KerbalCareerLogEntry(1, "Flyby", "Mun"));
            var roster = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
            };

            var missing = KerbalsModule.ResolveMissingCareerEntries(ledger, roster);

            Assert.Equal(2, missing.Count);
            Assert.Contains(new KerbalCareerLogEntry(0, "Orbit", "Kerbin"), missing);
            Assert.Contains(new KerbalCareerLogEntry(1, "Flyby", "Mun"), missing);
        }

        [Fact]
        public void Reassert_NeverReportsEntriesTheRosterHasAndTheLedgerDoesNot()
        {
            // THE monotone property. A kerbal's career log legitimately carries entries the
            // ledger never saw — pre-Parsek flights, a stand-in's own career, mod-written
            // entries — and the quicksave load has ALREADY removed the XP of superseded
            // flights. Reporting the reverse direction would let the patcher take away XP the
            // player still owns. Fails the instant this becomes a symmetric diff.
            var ledger = Ledger(new KerbalCareerLogEntry(0, "Orbit", "Kerbin"));
            var roster = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"),
                new KerbalCareerLogEntry(9, "Land", "Duna"),      // roster-only
                new KerbalCareerLogEntry(9, "PlantFlag", "Duna"), // roster-only
            };

            Assert.Empty(KerbalsModule.ResolveMissingCareerEntries(ledger, roster));
        }

        [Fact]
        public void Reassert_EmptyRosterLogYieldsEveryLedgerEntry()
        {
            // The post-rewind case: the quicksave restored a roster whose career log predates
            // the surviving flight entirely.
            var ledger = Ledger(
                new KerbalCareerLogEntry(2, "Orbit", "Mun"),
                new KerbalCareerLogEntry(2, "Land", "Mun"));

            Assert.Equal(2, KerbalsModule.ResolveMissingCareerEntries(
                ledger, new List<KerbalCareerLogEntry>()).Count);
            Assert.Equal(2, KerbalsModule.ResolveMissingCareerEntries(ledger, null).Count);
        }

        [Fact]
        public void Reassert_NoLedgerEntriesYieldsNothing()
        {
            Assert.Empty(KerbalsModule.ResolveMissingCareerEntries(
                null, new List<KerbalCareerLogEntry> { new KerbalCareerLogEntry(0, "Orbit", "Kerbin") }));
            Assert.Empty(KerbalsModule.ResolveMissingCareerEntries(
                new KerbalCareerEntries(), new List<KerbalCareerLogEntry>()));
        }

        [Fact]
        public void Reassert_ResultIsDeterministicallyOrdered()
        {
            var ledger = Ledger(
                new KerbalCareerLogEntry(1, "Orbit", "Mun"),
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Flyby", "Mun"));

            var missing = KerbalsModule.ResolveMissingCareerEntries(ledger, null);

            Assert.Equal(0, missing[0].Flight);
            Assert.Equal(0, missing[1].Flight);
            Assert.Equal(1, missing[2].Flight);
            Assert.Equal("Flyby", missing[0].Type);
        }

        [Fact]
        public void Reassert_NeverAppendsIntoAFlightGroupTheRosterEndedWithADeath()
        {
            // Decompile-verified hazard: KerbalRoster.ExperienceAddFlight RESETS the whole XP
            // accumulator when a flight group's LAST entry is Die, and FlightLog.GetFlights
            // groups by contiguous runs of the same flight number. A rewind rolls the flight
            // counter back, so a post-rewind death can archive a Die-terminated group carrying
            // the same number an erased flight used. Appending there would land AFTER the Die
            // entry, un-terminate the group, and cancel the death reset - scoring the dead
            // flight's deeds all over again. Fails if the death-flight guard is dropped.
            var ledger = Ledger(
                new KerbalCareerLogEntry(4, "Orbit", "Mun"),
                new KerbalCareerLogEntry(4, "Land", "Mun"),
                new KerbalCareerLogEntry(5, "Orbit", "Kerbin"));
            var roster = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(4, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(4, "Die", ""),
            };

            var missing = KerbalsModule.ResolveMissingCareerEntries(ledger, roster);

            Assert.Single(missing);
            Assert.Equal(5, missing[0].Flight);
            Assert.DoesNotContain(missing, e => e.Flight == 4);
        }

        [Fact]
        public void Reassert_DeathInOneFlightDoesNotBlockAnother()
        {
            var ledger = Ledger(
                new KerbalCareerLogEntry(1, "Orbit", "Kerbin"),
                new KerbalCareerLogEntry(2, "Orbit", "Mun"));
            var roster = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(2, "Die", ""),
            };

            var missing = KerbalsModule.ResolveMissingCareerEntries(ledger, roster);

            Assert.Single(missing);
            Assert.Equal(1, missing[0].Flight);
        }

        // ================================================================
        // The Re-Fly deferral gate (the irreversible-write safety argument)
        // ================================================================

        [Fact]
        public void Reassert_IsDeferredWhileAReFlySessionIsActive()
        {
            // THE correctness argument for an irreversible write. Every other roster mutation
            // in KerbalsModule is re-derived idempotently from the current ELS; appending a
            // career-log entry cannot be walked back. During a Re-Fly session the rewound
            // origin branch's KerbalExperience rows are STILL ELS-effective (ELS is
            // ledger-minus-tombstones, and the session-suppressed subtree is a playback
            // concept the recalc does not consult), so the post-load recalc would put the
            // just-erased XP straight back and the merge that tombstones those rows minutes
            // later could not undo it. Fails if the gate is removed.
            var module = new KerbalsModule();
            module.ProcessAction(XpAction("Jeb", new KerbalCareerLogEntry(0, "Orbit", "Kerbin")));

            var roster = new RecordingRosterFacade();
            try
            {
                KerbalsModule.ReFlySessionActiveOverrideForTesting = true;
                module.ApplyToRoster(roster);
                Assert.Empty(roster.Appended);

                KerbalsModule.ReFlySessionActiveOverrideForTesting = false;
                module.ApplyToRoster(roster);
                Assert.Single(roster.Appended);
                Assert.Equal(new KerbalCareerLogEntry(0, "Orbit", "Kerbin"), roster.Appended[0]);
            }
            finally
            {
                KerbalsModule.ReFlySessionActiveOverrideForTesting = null;
            }
        }

        [Fact]
        public void Reassert_SkipsAKerbalTheRosterDoesNotHaveInsteadOfCreatingOne()
        {
            // This facet re-asserts experience; manufacturing a roster member from an XP row
            // would be a much larger claim. Fails if the absent-kerbal skip becomes a create.
            var module = new KerbalsModule();
            module.ProcessAction(XpAction("Ghost Kerman", new KerbalCareerLogEntry(0, "Orbit", "Kerbin")));

            var roster = new RecordingRosterFacade { KnownKerbals = { } };
            try
            {
                KerbalsModule.ReFlySessionActiveOverrideForTesting = false;
                module.ApplyToRoster(roster);
            }
            finally
            {
                KerbalsModule.ReFlySessionActiveOverrideForTesting = null;
            }

            Assert.Empty(roster.Appended);
        }

        /// <summary>
        /// Minimal roster facade that records what the re-assert appended. Everything outside
        /// the career-log surface is inert, so the cell isolates the append decision.
        /// </summary>
        private sealed class RecordingRosterFacade : KerbalsModule.IKerbalRosterFacade
        {
            public readonly HashSet<string> KnownKerbals =
                new HashSet<string>(StringComparer.Ordinal) { "Jeb" };
            public readonly List<KerbalCareerLogEntry> Appended = new List<KerbalCareerLogEntry>();

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                status = default(ProtoCrewMember.RosterStatus);
                return false;
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedName = null;
                return false;
            }

            public bool TryRecreateStandIn(string desiredName, string trait) { return false; }
            public bool TryRemove(string name) { return false; }
            public bool IsKerbalOnLiveVessel(string kerbalName) { return false; }
            public bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId) { return false; }

            public List<KerbalCareerLogEntry> GetCareerLogEntries(string kerbalName)
            {
                return KnownKerbals.Contains(kerbalName)
                    ? new List<KerbalCareerLogEntry>()
                    : null;
            }

            public int AppendCareerLogEntries(
                string kerbalName, IReadOnlyList<KerbalCareerLogEntry> entries)
            {
                if (!KnownKerbals.Contains(kerbalName)) return -1;
                if (entries == null) return 0;
                Appended.AddRange(entries);
                return entries.Count;
            }
        }

        // ================================================================
        // Layer A: CAREER_LOG parse from synthetic .sfs content
        // ================================================================

        [Fact]
        public void Parser_ReadsCareerLogNodeInStockShape()
        {
            // Node shape verified against the decompiled FlightLog.Save: a log-level `flight`
            // counter, then ONE VALUE PER ENTRY whose KEY is the entry's flight number and
            // whose VALUE is `type` or `type,target`. Repeating keys are the norm.
            var node = new ConfigNode("CAREER_LOG");
            node.AddValue("flight", "2");
            node.AddValue("0", "Launch,Kerbin");
            node.AddValue("0", "Flight,Kerbin");
            node.AddValue("0", "Orbit,Kerbin");
            node.AddValue("1", "Flyby,Mun");
            node.AddValue("1", "Recover");

            var entries = CareerSaveParser.ParseFlightLogNode(node);

            Assert.Equal(5, entries.Count);
            Assert.Contains(new KerbalCareerLogEntry(0, "Launch", "Kerbin"), entries);
            Assert.Contains(new KerbalCareerLogEntry(1, "Flyby", "Mun"), entries);
            // A target-less entry parses with an empty target, not a null one.
            Assert.Contains(new KerbalCareerLogEntry(1, "Recover", ""), entries);
            // The log-level counter is NOT an entry.
            Assert.DoesNotContain(entries, e => e.Type == "2");
        }

        [Fact]
        public void Parser_SkipsNonNumericKeysAndEmptyValues()
        {
            var node = new ConfigNode("CAREER_LOG");
            node.AddValue("flight", "1");
            node.AddValue("notanumber", "Orbit,Kerbin");
            node.AddValue("0", "");
            node.AddValue("0", ",OnlyTarget");
            node.AddValue("0", "Orbit,Kerbin");

            var entries = CareerSaveParser.ParseFlightLogNode(node);

            Assert.Single(entries);
            Assert.Contains(new KerbalCareerLogEntry(0, "Orbit", "Kerbin"), entries);
        }

        [Fact]
        public void Parser_NullOrEmptyNodeYieldsEmpty()
        {
            Assert.Empty(CareerSaveParser.ParseFlightLogNode(null));
            Assert.Empty(CareerSaveParser.ParseFlightLogNode(new ConfigNode("CAREER_LOG")));
        }

        [Fact]
        public void Parser_ReadsPerKerbalCareerLogsFromASyntheticSave()
        {
            // End-to-end over Layer A: a GAME node with a ROSTER whose KERBALs carry
            // CAREER_LOGs. This is the shape the ground-truth harness parses out of a real
            // quicksave; the fixture is written by hand so the parse is proven against the
            // stock node layout rather than against Parsek's own writer.
            var game = new ConfigNode("GAME");
            game.AddNode("FLIGHTSTATE");

            var roster = game.AddNode("ROSTER");

            var jeb = roster.AddNode("KERBAL");
            jeb.AddValue("name", "Jebediah Kerman");
            jeb.AddValue("trait", "Pilot");
            var jebLog = jeb.AddNode("CAREER_LOG");
            jebLog.AddValue("flight", "1");
            jebLog.AddValue("0", "Launch,Kerbin");
            jebLog.AddValue("0", "Orbit,Kerbin");

            var bill = roster.AddNode("KERBAL");
            bill.AddValue("name", "Bill Kerman");
            // Bill has no CAREER_LOG at all — an applicant or a never-flown kerbal.

            var val = roster.AddNode("KERBAL");
            val.AddValue("name", "Valentina Kerman");
            var valLog = val.AddNode("CAREER_LOG");
            valLog.AddValue("flight", "3");
            valLog.AddValue("2", "Land,Mun");

            var snapshot = CareerSaveParser.Parse(game);

            Assert.True(snapshot.Parsed);
            Assert.Equal(2, snapshot.KerbalCareerLog.Count);
            Assert.Equal(2, snapshot.KerbalCareerLog["Jebediah Kerman"].Count);
            Assert.Contains(new KerbalCareerLogEntry(2, "Land", "Mun"),
                snapshot.KerbalCareerLog["Valentina Kerman"]);
            Assert.False(snapshot.KerbalCareerLog.ContainsKey("Bill Kerman"));
        }

        [Fact]
        public void Parser_SaveWithNoRosterLeavesTheFacetEmptyRatherThanFailingTheParse()
        {
            var game = new ConfigNode("GAME");
            game.AddNode("FLIGHTSTATE");

            var snapshot = CareerSaveParser.Parse(game);

            Assert.True(snapshot.Parsed);
            Assert.Empty(snapshot.KerbalCareerLog);
        }

        // ================================================================
        // Layer A: the KerbalXp divergence facet
        // ================================================================

        private static CareerSaveSnapshot SaveWith(
            string kerbal, params KerbalCareerLogEntry[] entries)
        {
            var save = new CareerSaveSnapshot { Parsed = true };
            save.KerbalCareerLog[kerbal] = new HashSet<KerbalCareerLogEntry>(entries);
            return save;
        }

        private static LedgerReconstructionSnapshot ReconWith(
            string kerbal, params KerbalCareerLogEntry[] entries)
        {
            var recon = new LedgerReconstructionSnapshot();
            recon.KerbalCareerLog[kerbal] = new HashSet<KerbalCareerLogEntry>(entries);
            return recon;
        }

        [Fact]
        public void Diff_ReportsAnEntryTheReconCreditsAndTheSaveLacks()
        {
            var save = SaveWith("Jeb", new KerbalCareerLogEntry(0, "Launch", "Kerbin"));
            var recon = ReconWith("Jeb",
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(0, "Orbit", "Kerbin"));

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, new FacetTolerances(), null);

            var xp = report.All.FindAll(d => d.Facet == DivergenceFacet.KerbalXp);
            Assert.Single(xp);
            Assert.Equal(DivergenceKind.PhantomInRecon, xp[0].Kind);
            Assert.Equal("Jeb", xp[0].Identity);
            Assert.Contains("missingInSave=1", xp[0].Detail);
        }

        [Fact]
        public void Diff_DoesNotReportEntriesTheSaveHasAndTheReconDoesNot()
        {
            // Same one-directional reasoning as the patcher: a save-side career log
            // legitimately carries more than the ledger ever saw, so reporting the reverse
            // would bury the signal under every pre-Parsek flight in the career.
            var save = SaveWith("Jeb",
                new KerbalCareerLogEntry(0, "Launch", "Kerbin"),
                new KerbalCareerLogEntry(9, "Land", "Duna"));
            var recon = ReconWith("Jeb", new KerbalCareerLogEntry(0, "Launch", "Kerbin"));

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, new FacetTolerances(), null);

            Assert.Empty(report.All.FindAll(d => d.Facet == DivergenceFacet.KerbalXp));
        }

        [Fact]
        public void Diff_KerbalMissingFromTheSaveRosterIsReported()
        {
            var save = new CareerSaveSnapshot { Parsed = true };
            var recon = ReconWith("Ghost Kerman", new KerbalCareerLogEntry(0, "Orbit", "Kerbin"));

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, new FacetTolerances(), null);

            var xp = report.All.FindAll(d => d.Facet == DivergenceFacet.KerbalXp);
            Assert.Single(xp);
            Assert.Contains("inSaveRoster=False", xp[0].Detail);
        }

        [Fact]
        public void KerbalXp_IsReportOnlyNotAlwaysHard()
        {
            // Matches the SubjectScience posture. A career log legitimately diverges on a
            // real career, so promoting this to hard would red every ground-truth run.
            // Fails if KerbalXp is ever added to IsAlwaysHard without a scenario proving it.
            var divergence = new LedgerDivergence
            {
                Facet = DivergenceFacet.KerbalXp,
                Kind = DivergenceKind.PhantomInRecon,
                Identity = "Jeb",
                Detail = "kerbalXp",
            };

            Assert.False(LedgerDivergenceReport.IsAlwaysHard(divergence));
        }

        // ================================================================
        // The subscription pair
        // ================================================================

        [Fact]
        public void Recorder_SubscribesAndUnsubscribesTheExperienceHandlerSymmetrically()
        {
            // An Add with no matching Remove leaks a handler across scene loads, which for a
            // recovery handler means duplicate XP events on every subsequent recovery. Source
            // scan rather than reflection because the subscription is a statement, not a
            // member. Fails if either half of the pair is dropped.
            string source = ReadSource("GameStateRecorder.cs");

            const string handler = "OnVesselRecoveryProcessingForExperience";
            var adds = Regex.Matches(
                source, @"GameEvents\.onVesselRecoveryProcessing\.Add\(\s*" + handler + @"\s*\)");
            var removes = Regex.Matches(
                source, @"GameEvents\.onVesselRecoveryProcessing\.Remove\(\s*" + handler + @"\s*\)");

            Assert.Equal(1, adds.Count);
            Assert.Equal(1, removes.Count);
        }

        [Fact]
        public void Recorder_ExperienceHandlerIsAnInstanceMethod()
        {
            // KSP's EventData.Add dereferences the delegate's Target; a static handler throws
            // NullReferenceException at subscribe time and aborts the whole Subscribe pass.
            var handler = typeof(GameStateRecorder).GetMethod(
                "OnVesselRecoveryProcessingForExperience",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            Assert.NotNull(handler);
            Assert.False(handler.IsStatic);
        }

        private static string ReadSource(string fileName)
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ — five levels to the root.
            string path = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", fileName));
            Assert.True(File.Exists(path), "source file not found for scan: " + path);
            return File.ReadAllText(path);
        }
    }
}
