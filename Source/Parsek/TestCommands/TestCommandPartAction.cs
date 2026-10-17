using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Which stock part-menu button a <c>PartAction</c> presses.</summary>
    internal enum PartActionKind
    {
        Unknown = 0,
        /// <summary>A docking port's "Undock" (<c>ModuleDockingNode.Undock</c>).</summary>
        Undock,
        /// <summary>A decoupler's "Decouple" (<c>ModuleDecouplerBase.Decouple</c>).</summary>
        Decouple,
        /// <summary>A command part's or docking port's "Control From Here"
        /// (<c>ModuleCommand.MakeReference</c> / <c>ModuleDockingNode.MakeReferenceTransform</c>).</summary>
        ControlFrom,
        /// <summary>An animation group's "Deploy" (<c>ModuleAnimationGroup.DeployModule</c>; the
        /// drill's deploy).</summary>
        Deploy,
        /// <summary>An animation group's "Retract" (<c>ModuleAnimationGroup.RetractModule</c>).</summary>
        Retract,
        /// <summary>A converter's "Start" (<c>BaseConverter.StartResourceConverter</c>: a
        /// harvester, an ISRU converter, a lab's research).</summary>
        StartConverter,
        /// <summary>A converter's "Stop" (<c>BaseConverter.StopResourceConverter</c>).</summary>
        StopConverter,
        /// <summary>A science experiment's run button (<c>ModuleScienceExperiment.DeployExperiment</c>).</summary>
        Experiment,
        /// <summary>The experiment results dialog's "process in lab" button: the experiment's
        /// private <c>sendDataToLab(ScienceData)</c>, the callback the dialog page carries.</summary>
        LabProcess,
        /// <summary>A science lab's "Transmit Science" (<c>ModuleScienceLab.TransmitScience</c>).</summary>
        LabTransmit,
        /// <summary>Presses nothing: reads the named part's module state only.</summary>
        Inspect,
    }

    /// <summary>One module reading of the target part for the <c>state=</c> answer field: plain
    /// values the applier read from a live module, formatted by
    /// <see cref="TestCommandPartAction.FormatState"/>.</summary>
    internal struct PartActionModuleReading
    {
        internal enum ReadingKind { Converter, Lab, Experiment, AnimationGroup }

        internal ReadingKind Kind;
        internal string Name;
        internal bool Flag;
        internal float StoredScience;
        internal float DataStored;
        internal int Count;

        internal static PartActionModuleReading Converter(string converterName, bool active)
            => new PartActionModuleReading { Kind = ReadingKind.Converter, Name = converterName, Flag = active };

        internal static PartActionModuleReading Lab(float storedScience, float dataStored)
            => new PartActionModuleReading
            {
                Kind = ReadingKind.Lab, StoredScience = storedScience, DataStored = dataStored,
            };

        internal static PartActionModuleReading Experiment(int dataCount)
            => new PartActionModuleReading { Kind = ReadingKind.Experiment, Count = dataCount };

        internal static PartActionModuleReading AnimationGroup(bool deployed)
            => new PartActionModuleReading { Kind = ReadingKind.AnimationGroup, Flag = deployed };
    }

    /// <summary>
    /// Pure half of the automation-only <c>PartAction</c> verb (vessel names, design
    /// <c>design-vessel-and-mission-names.md</c> section 15.3): a player's click on one button
    /// of one part's stock right-click menu (PAW), in FLIGHT.
    ///
    /// <para><b>Why.</b> Three vessel-name claims need a stock part event no mission or verb
    /// drove: an undock after a dock (the dock-absorb row turning into an UndockReturn link), a
    /// decouple after a load (stock's <c>onVesselWasModified</c> re-reads the vessel's naming
    /// part, so a rename that did not update it reverts), and "Control From Here" on a part
    /// below a decoupler (the staging race where stock's <c>ModuleDecouple.WaitAndSwitchFocus</c>
    /// renames the OLD vessel one Update after the split). Each act invokes the part module's
    /// own <c>KSPEvent</c> through its <c>BaseEvent</c>, which is exactly what the PAW button
    /// does, so stock and every Parsek hook run as for a player.</para>
    ///
    /// <para>The ISRU / lab buttons (deploy, retract, startconverter, stopconverter,
    /// experiment, labprocess, labtransmit, inspect) let a lane drive a drill, a converter, a
    /// science lab and an experiment the way a player clicks them, and read the part's module
    /// state back.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=PartAction button=undock|decouple|controlfrom|deploy|retract|
    /// startconverter|stopconverter|experiment|labprocess|labtransmit|inspect
    /// [part=&lt;part name&gt;] [converter=&lt;ConverterName&gt;] [all=true] [pid=&lt;persistentId&gt;]</c>.
    /// <c>button=</c> REQUIRED, closed. <c>part=</c> is the part's runtime name (dot form,
    /// <c>Decoupler.1</c>); REQUIRED for every button but <c>undock</c>, OPTIONAL for
    /// <c>undock</c> (absent: the vessel's one docked port pair). <c>converter=</c> is OPTIONAL
    /// and only for <c>startconverter</c> / <c>stopconverter</c>: it keeps the converter modules
    /// whose <c>BaseConverter.ConverterName</c> equals it (exact, ordinal), so a part carrying
    /// several converters (the Convert-O-Tron's four) names one; without it such a part is
    /// <c>part-ambiguous</c>, never a silent first pick. <c>all=true</c> (closed: <c>true</c>
    /// only) presses EVERY candidate instead of requiring exactly one (a craft carrying four
    /// drills deploys all four); allowed on every button but <c>undock</c>, <c>decouple</c> and
    /// <c>controlfrom</c>, whose one press reshapes the vessel (<c>all-arg-unexpected</c>), and
    /// on <c>inspect</c> it reads every part carrying the name. <c>pid=</c> names a loaded, non-ghost
    /// vessel by persistentId; absent, the ACTIVE vessel. SINGLE-PHASE, <c>RequiresFlight</c>,
    /// default budget: every event runs synchronously (the undock and the decouple create the
    /// new vessel inside the call; a converter, an experiment's data and a lab's processing
    /// move on later frames, so <c>state=</c> is the state right after the click).</para>
    ///
    /// <para><b>labprocess</b> candidates are the named part's experiment modules holding data
    /// (none with data: <c>experiment-no-data</c>). When the results dialog shows that part's
    /// page, its own lab callback runs (<c>event=OnSendToLab</c>: process and dismiss the page,
    /// the dialog button's body); otherwise the experiment's private <c>sendDataToLab</c> runs
    /// by reflection (<c>event=sendDataToLab</c>). A build without that method refuses
    /// <c>lab-method-missing</c>. <b>inspect</b> presses nothing; its candidate is the one part
    /// carrying the name.</para>
    ///
    /// <para><b>Answer.</b> OK payload <c>button= pid= part= partId= event= newPid= newName=
    /// vesselsBefore= vesselsAfter= activePid= activeName= converter= state= count=</c>: <c>newPid</c> /
    /// <c>newName</c> are the one vessel the event created (0 / empty when none, as for
    /// <c>controlfrom</c>), read at once, so a stock rename that lands one Update later is NOT
    /// in it. <c>converter</c> is the given filter (empty when none); <c>state</c> is
    /// <see cref="FormatState"/> over the target part's modules read after the press;
    /// <c>count</c> is how many candidates were pressed (inspect: read), 1 without
    /// <c>all=true</c>. With <c>all=true</c>, <c>part</c> / <c>partId</c> / <c>event</c> are
    /// the first candidate's and <c>state</c> is <see cref="FormatAllState"/>: every acted-on
    /// part, in vessel part order, as <c>&lt;flightID&gt;:&lt;FormatState&gt;</c> joined by
    /// <c> | </c>. A press that throws stops the walk and answers ERROR <c>event-threw</c>, the
    /// log line naming how many presses succeeded first. Line:
    /// <c>partaction applied button= pid= part='..' partId= event= newPid= newName='..' vessels=N->M
    /// activePid= activeName='..' converter='..' state='..' count=</c>; the newer fields are
    /// appended so every earlier anchor still matches.</para>
    /// </summary>
    internal static class TestCommandPartAction
    {
        internal const string Verb = "PartAction";
        internal const string ButtonKey = "button";
        internal const string PartKey = "part";
        internal const string PidKey = "pid";
        internal const string ConverterKey = "converter";
        internal const string AllKey = "all";
        /// <summary>The one value <c>all=</c> takes.</summary>
        internal const string AllTrueToken = "true";

        internal const string UndockToken = "undock";
        internal const string DecoupleToken = "decouple";
        internal const string ControlFromToken = "controlfrom";
        internal const string DeployToken = "deploy";
        internal const string RetractToken = "retract";
        internal const string StartConverterToken = "startconverter";
        internal const string StopConverterToken = "stopconverter";
        internal const string ExperimentToken = "experiment";
        internal const string LabProcessToken = "labprocess";
        internal const string LabTransmitToken = "labtransmit";
        internal const string InspectToken = "inspect";

        /// <summary>The closed <c>button=</c> vocabulary. hlib.PARTACTION_BUTTON_VALUES mirrors it
        /// (PartActionSourceSyncTests).</summary>
        internal static readonly string[] ButtonValues =
        {
            UndockToken, DecoupleToken, ControlFromToken, DeployToken, RetractToken,
            StartConverterToken, StopConverterToken, ExperimentToken, LabProcessToken,
            LabTransmitToken, InspectToken,
        };

        /// <summary>The private <c>ModuleScienceExperiment</c> method the results dialog's lab
        /// button calls (KSP 1.12.5, decompiled): <c>void sendDataToLab(ScienceData)</c>.</summary>
        internal const string LabProcessMethodName = "sendDataToLab";

        /// <summary>The <c>event=</c> of a labprocess that ran the open results dialog page's own
        /// lab callback (the dialog button's body: process, then dismiss the page).</summary>
        internal const string LabProcessDialogEventName = "OnSendToLab";

        internal const string ButtonArgMissingReason = "partaction-button-arg-missing";
        internal const string ButtonArgInvalidReason = "partaction-button-arg-invalid";
        internal const string PartArgMissingReason = "partaction-part-arg-missing";
        internal const string ConverterArgUnexpectedReason = "partaction-converter-arg-unexpected";
        internal const string AllArgInvalidReason = "partaction-all-arg-invalid";
        internal const string AllArgUnexpectedReason = "partaction-all-arg-unexpected";
        internal const string PidArgInvalidReason = "partaction-pid-arg-invalid";
        internal const string NoActiveVesselReason = "partaction-no-active-vessel";
        internal const string VesselNotFoundReason = "partaction-vessel-not-found";
        internal const string TargetIsGhostReason = "partaction-target-is-ghost";
        internal const string VesselUnloadedReason = "partaction-vessel-unloaded";
        internal const string PartNotFoundReason = "partaction-part-not-found";
        internal const string ConverterNotFoundReason = "partaction-converter-not-found";
        internal const string PartAmbiguousReason = "partaction-part-ambiguous";
        internal const string EventUnavailableReason = "partaction-event-unavailable";
        internal const string ExperimentNoDataReason = "partaction-experiment-no-data";
        internal const string LabMethodMissingReason = "partaction-lab-method-missing";

        // Post-call ERROR terminal: the event was invoked and threw.
        internal const string EventThrewReason = "partaction-event-threw";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.PARTACTION_REASONS mirrors it
        /// (PartActionSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "partaction-button-arg-missing",
            "partaction-button-arg-invalid",
            "partaction-part-arg-missing",
            "partaction-converter-arg-unexpected",
            "partaction-all-arg-invalid",
            "partaction-all-arg-unexpected",
            "partaction-pid-arg-invalid",
            "partaction-no-active-vessel",
            "partaction-vessel-not-found",
            "partaction-target-is-ghost",
            "partaction-vessel-unloaded",
            "partaction-part-not-found",
            "partaction-converter-not-found",
            "partaction-part-ambiguous",
            "partaction-event-unavailable",
            "partaction-experiment-no-data",
            "partaction-lab-method-missing",
        };

        /// <summary>The post-call ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons = { "partaction-event-threw" };

        /// <summary>Parses the REQUIRED closed <c>button=</c> (case-sensitive, like every closed
        /// seam vocabulary). Returns null on success or the refusal reason.</summary>
        internal static string ParseButton(string raw, out PartActionKind kind)
        {
            kind = PartActionKind.Unknown;
            if (string.IsNullOrEmpty(raw)) return ButtonArgMissingReason;
            switch (raw)
            {
                case UndockToken: kind = PartActionKind.Undock; return null;
                case DecoupleToken: kind = PartActionKind.Decouple; return null;
                case ControlFromToken: kind = PartActionKind.ControlFrom; return null;
                case DeployToken: kind = PartActionKind.Deploy; return null;
                case RetractToken: kind = PartActionKind.Retract; return null;
                case StartConverterToken: kind = PartActionKind.StartConverter; return null;
                case StopConverterToken: kind = PartActionKind.StopConverter; return null;
                case ExperimentToken: kind = PartActionKind.Experiment; return null;
                case LabProcessToken: kind = PartActionKind.LabProcess; return null;
                case LabTransmitToken: kind = PartActionKind.LabTransmit; return null;
                case InspectToken: kind = PartActionKind.Inspect; return null;
                default: return ButtonArgInvalidReason;
            }
        }

        /// <summary>The wire token of <paramref name="kind"/>.</summary>
        internal static string Token(PartActionKind kind)
        {
            switch (kind)
            {
                case PartActionKind.Undock: return UndockToken;
                case PartActionKind.Decouple: return DecoupleToken;
                case PartActionKind.ControlFrom: return ControlFromToken;
                case PartActionKind.Deploy: return DeployToken;
                case PartActionKind.Retract: return RetractToken;
                case PartActionKind.StartConverter: return StartConverterToken;
                case PartActionKind.StopConverter: return StopConverterToken;
                case PartActionKind.Experiment: return ExperimentToken;
                case PartActionKind.LabProcess: return LabProcessToken;
                case PartActionKind.LabTransmit: return LabTransmitToken;
                case PartActionKind.Inspect: return InspectToken;
                default: return "unknown";
            }
        }

        /// <summary>True when <paramref name="kind"/> needs a <c>part=</c>: every button but an
        /// undock is chosen by the player on one named part; an undock has one obvious port pair
        /// on an ordinary docked vessel.</summary>
        internal static bool RequiresPart(PartActionKind kind)
            => kind != PartActionKind.Unknown && kind != PartActionKind.Undock;

        /// <summary>True for the two buttons a <c>converter=</c> filter applies to.</summary>
        internal static bool AcceptsConverter(PartActionKind kind)
            => kind == PartActionKind.StartConverter || kind == PartActionKind.StopConverter;

        /// <summary>Parses the OPTIONAL <c>converter=</c>: null (success) with
        /// <paramref name="converterName"/> null when absent, the name verbatim when given on a
        /// converter button, or <c>converter-arg-unexpected</c> on any other button (the filter
        /// would be silently ignored).</summary>
        internal static string ParseConverter(string raw, PartActionKind kind, out string converterName)
        {
            converterName = string.IsNullOrEmpty(raw) ? null : raw;
            if (converterName != null && !AcceptsConverter(kind))
            {
                converterName = null;
                return ConverterArgUnexpectedReason;
            }
            return null;
        }

        /// <summary>Parses <c>part=</c> for <paramref name="kind"/>: null (success) with
        /// <paramref name="partName"/> null when absent and optional, or the refusal reason.
        /// The name is matched VERBATIM against <c>Part.partInfo.name</c> (dot form).</summary>
        internal static string ParsePart(string raw, PartActionKind kind, out string partName)
        {
            partName = string.IsNullOrEmpty(raw) ? null : raw;
            if (partName == null && RequiresPart(kind)) return PartArgMissingReason;
            return null;
        }

        /// <summary>True for the buttons <c>all=true</c> applies to: every button but the three
        /// whose single press reshapes or re-points the vessel (undock, decouple, controlfrom).</summary>
        internal static bool AcceptsAll(PartActionKind kind)
            => kind != PartActionKind.Unknown && kind != PartActionKind.Undock
                && kind != PartActionKind.Decouple && kind != PartActionKind.ControlFrom;

        /// <summary>Parses the OPTIONAL closed <c>all=</c>: absent or empty is false; <c>true</c>
        /// (case-sensitive) is true on a button that accepts it; any other value is
        /// <c>all-arg-invalid</c>, and <c>true</c> on undock / decouple / controlfrom is
        /// <c>all-arg-unexpected</c>.</summary>
        internal static string ParseAll(string raw, PartActionKind kind, out bool all)
        {
            all = false;
            if (string.IsNullOrEmpty(raw)) return null;
            if (!string.Equals(raw, AllTrueToken, StringComparison.Ordinal)) return AllArgInvalidReason;
            if (!AcceptsAll(kind)) return AllArgUnexpectedReason;
            all = true;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>pid=</c> (invariant decimal uint, nonzero; the
        /// Recover / RenameVessel grammar).</summary>
        internal static string ParsePid(string raw, out uint pid, out bool hasPid)
        {
            pid = 0;
            hasPid = false;
            if (string.IsNullOrEmpty(raw)) return null;
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
                || parsed == 0)
                return PidArgInvalidReason;
            pid = parsed;
            hasPid = true;
            return null;
        }

        /// <summary>The vessel gate: found (by pid, else the active one), not a ghost map
        /// vessel, and loaded (a part menu exists only on a loaded vessel's parts). Null to
        /// proceed.</summary>
        internal static string DecideVessel(bool hasPid, bool found, bool isGhostMapVessel, bool loaded)
        {
            if (!found) return hasPid ? VesselNotFoundReason : NoActiveVesselReason;
            if (isGhostMapVessel) return TargetIsGhostReason;
            if (!loaded) return VesselUnloadedReason;
            return null;
        }

        /// <summary>The stock <c>KSPEvent</c> names an act presses, in lookup order: the
        /// part-menu buttons' method names in KSP 1.12.5 (decompiled).</summary>
        internal static string[] EventNamesFor(PartActionKind kind)
        {
            switch (kind)
            {
                case PartActionKind.Undock: return new[] { "Undock" };
                case PartActionKind.Decouple: return new[] { "Decouple" };
                case PartActionKind.ControlFrom: return new[] { "MakeReference", "MakeReferenceTransform" };
                case PartActionKind.Deploy: return new[] { "DeployModule" };
                case PartActionKind.Retract: return new[] { "RetractModule" };
                case PartActionKind.StartConverter: return new[] { "StartResourceConverter" };
                case PartActionKind.StopConverter: return new[] { "StopResourceConverter" };
                case PartActionKind.Experiment: return new[] { "DeployExperiment" };
                case PartActionKind.LabTransmit: return new[] { "TransmitScience" };
                // labprocess calls a private method, not a KSPEvent; inspect presses nothing.
                default: return new string[0];
            }
        }

        /// <summary>
        /// The candidate gate, after the applier counted the vessel's parts: with a
        /// <c>part=</c>, how many parts carry that name (<paramref name="namedParts"/>); and how
        /// many distinct buttons the act could press (<paramref name="candidates"/>: a part
        /// module with the act's event active and shown in flight, one per docked port PAIR for
        /// an undock). Exactly one candidate proceeds; none is <c>part-not-found</c> (a named
        /// part the vessel does not carry) or <c>event-unavailable</c> (no live button); more
        /// than one is <c>part-ambiguous</c>, never a silent first pick.
        /// </summary>
        internal static string DecideCandidates(string partName, int namedParts, int candidates)
            => DecideCandidates(PartActionKind.Unknown, partName, namedParts, null, 0, 0, candidates);

        /// <summary>
        /// The full candidate gate. On top of the three-count form: a <c>converter=</c> that no
        /// converter module on the named part(s) carries (<paramref name="matchingConverters"/>,
        /// counted whatever the event state) is <c>converter-not-found</c>, a spec typo rather
        /// than a live state; a labprocess with no candidate on a part that does carry an
        /// experiment module (<paramref name="experimentModules"/>) is <c>experiment-no-data</c>
        /// (nothing to process yet). For inspect the candidates are the named parts.
        /// </summary>
        internal static string DecideCandidates(
            PartActionKind kind, string partName, int namedParts, string converterName,
            int matchingConverters, int experimentModules, int candidates)
            => DecideCandidates(kind, partName, namedParts, converterName, matchingConverters,
                experimentModules, candidates, false);

        /// <summary>The candidate gate with <c>all=</c>: with <paramref name="all"/> true, one or
        /// more candidates proceed (every one is pressed); zero refuses exactly as without it.</summary>
        internal static string DecideCandidates(
            PartActionKind kind, string partName, int namedParts, string converterName,
            int matchingConverters, int experimentModules, int candidates, bool all)
        {
            if (partName != null && namedParts == 0) return PartNotFoundReason;
            if (converterName != null && matchingConverters == 0) return ConverterNotFoundReason;
            if (candidates == 0)
                return kind == PartActionKind.LabProcess && experimentModules > 0
                    ? ExperimentNoDataReason
                    : EventUnavailableReason;
            if (candidates > 1 && !all) return PartAmbiguousReason;
            return null;
        }

        /// <summary>
        /// The <c>all=true</c> <c>state=</c> field: one entry per acted-on part, in the given
        /// (vessel part) order, as <c>&lt;flightID&gt;:&lt;FormatState(readings)&gt;</c>, joined by
        /// <c> | </c>. Empty for no parts.
        /// </summary>
        internal static string FormatAllState(IList<KeyValuePair<uint, IList<PartActionModuleReading>>> parts)
        {
            if (parts == null || parts.Count == 0) return string.Empty;
            CultureInfo ic = CultureInfo.InvariantCulture;
            var entries = new string[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                entries[i] = parts[i].Key.ToString(ic) + ":" + FormatState(parts[i].Value);
            return string.Join(" | ", entries);
        }

        /// <summary>The ERROR detail of an <c>all=true</c> walk that stopped on a throwing press:
        /// <c>presses=&lt;succeeded&gt;/&lt;candidates&gt;</c>.</summary>
        internal static string FormatPressProgress(int succeeded, int candidates)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "presses=" + succeeded.ToString(ic) + "/" + candidates.ToString(ic);
        }

        /// <summary>
        /// The <c>state=</c> field: one segment per reading, in the part's module order, joined
        /// by <c>;</c> (a converter name may hold spaces). Segments: a converter
        /// <c>&lt;ConverterName&gt;:active=0|1</c>; a science lab
        /// <c>lab:storedScience=&lt;F2&gt; dataStored=&lt;F2&gt;</c>; an experiment
        /// <c>exp:data=&lt;count&gt;</c>; an animation group <c>deployed=0|1</c>. Invariant
        /// culture; empty for no readings.
        /// </summary>
        internal static string FormatState(IList<PartActionModuleReading> readings)
        {
            if (readings == null || readings.Count == 0) return string.Empty;
            CultureInfo ic = CultureInfo.InvariantCulture;
            var parts = new List<string>(readings.Count);
            for (int i = 0; i < readings.Count; i++)
            {
                PartActionModuleReading r = readings[i];
                switch (r.Kind)
                {
                    case PartActionModuleReading.ReadingKind.Converter:
                        parts.Add((r.Name ?? string.Empty) + ":active=" + (r.Flag ? "1" : "0"));
                        break;
                    case PartActionModuleReading.ReadingKind.Lab:
                        parts.Add("lab:storedScience=" + r.StoredScience.ToString("F2", ic)
                            + " dataStored=" + r.DataStored.ToString("F2", ic));
                        break;
                    case PartActionModuleReading.ReadingKind.Experiment:
                        parts.Add("exp:data=" + r.Count.ToString(ic));
                        break;
                    case PartActionModuleReading.ReadingKind.AnimationGroup:
                        parts.Add("deployed=" + (r.Flag ? "1" : "0"));
                        break;
                }
            }
            return string.Join(";", parts.ToArray());
        }

        /// <summary>
        /// Folds the docking ports whose Undock is live into distinct PAIRS: stock shows Undock
        /// on both ports of a docked pair, and either press undocks the same joint. Each entry is
        /// (port id, the id of the port it is docked to, 0 when unknown); a port whose partner is
        /// already listed is the same pair. Returns the kept ports' indexes, in order.
        /// </summary>
        internal static List<int> FoldUndockPairs(IList<KeyValuePair<uint, uint>> ports)
        {
            var kept = new List<int>();
            if (ports == null) return kept;
            var seen = new HashSet<uint>();
            for (int i = 0; i < ports.Count; i++)
            {
                uint self = ports[i].Key;
                uint other = ports[i].Value;
                if (seen.Contains(self) || (other != 0 && seen.Contains(other))) continue;
                seen.Add(self);
                if (other != 0) seen.Add(other);
                kept.Add(i);
            }
            return kept;
        }

        /// <summary>The pids present after the event and absent before it, in after-order:
        /// the vessel(s) an undock or a decouple created.</summary>
        internal static List<uint> NewVesselPids(IEnumerable<uint> before, IEnumerable<uint> after)
        {
            var created = new List<uint>();
            var old = new HashSet<uint>();
            if (before != null) foreach (uint p in before) old.Add(p);
            if (after != null)
                foreach (uint p in after)
                    if (!old.Contains(p) && !created.Contains(p)) created.Add(p);
            return created;
        }

        /// <summary>The OK payload, in wire order (the two newer fields last).</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            PartActionKind kind, uint pid, string partName, uint partId, string eventName,
            uint newPid, string newName, int vesselsBefore, int vesselsAfter,
            uint activePid, string activeName, string converterName = null, string state = null,
            int count = 1)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("button", Token(kind)),
                new KeyValuePair<string, string>("pid", pid.ToString(ic)),
                new KeyValuePair<string, string>("part", partName ?? string.Empty),
                new KeyValuePair<string, string>("partId", partId.ToString(ic)),
                new KeyValuePair<string, string>("event", eventName ?? string.Empty),
                new KeyValuePair<string, string>("newPid", newPid.ToString(ic)),
                new KeyValuePair<string, string>("newName", newName ?? string.Empty),
                new KeyValuePair<string, string>("vesselsBefore", vesselsBefore.ToString(ic)),
                new KeyValuePair<string, string>("vesselsAfter", vesselsAfter.ToString(ic)),
                new KeyValuePair<string, string>("activePid", activePid.ToString(ic)),
                new KeyValuePair<string, string>("activeName", activeName ?? string.Empty),
                new KeyValuePair<string, string>("converter", converterName ?? string.Empty),
                new KeyValuePair<string, string>("state", state ?? string.Empty),
                new KeyValuePair<string, string>("count", count.ToString(ic)),
            };
        }

        /// <summary>The grep-stable success line. Names are single-quoted so a spec can anchor
        /// a name containing spaces.</summary>
        internal static string FormatAppliedLine(
            PartActionKind kind, uint pid, string partName, uint partId, string eventName,
            uint newPid, string newName, int vesselsBefore, int vesselsAfter,
            uint activePid, string activeName, string converterName = null, string state = null,
            int count = 1)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "partaction applied button=" + Token(kind)
                + " pid=" + pid.ToString(ic)
                + " part='" + (partName ?? string.Empty) + "'"
                + " partId=" + partId.ToString(ic)
                + " event=" + (eventName ?? string.Empty)
                + " newPid=" + newPid.ToString(ic)
                + " newName='" + (newName ?? string.Empty) + "'"
                + " vessels=" + vesselsBefore.ToString(ic) + "->" + vesselsAfter.ToString(ic)
                + " activePid=" + activePid.ToString(ic)
                + " activeName='" + (activeName ?? string.Empty) + "'"
                + " converter='" + (converterName ?? string.Empty) + "'"
                + " state='" + (state ?? string.Empty) + "'"
                + " count=" + count.ToString(ic);
        }
    }
}
