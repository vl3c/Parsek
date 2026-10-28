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
        /// <summary>The experiment results dialog's "process in lab" button: the dialog page's own
        /// <c>OnSendToLab</c> callback (the experiment's <c>sendDataToLab</c>, then the page
        /// dismissal), the page opened by the experiment's "Review Data" when none is up.</summary>
        LabProcess,
        /// <summary>A science lab's "Transmit Science" (<c>ModuleScienceLab.TransmitScience</c>).</summary>
        LabTransmit,
        /// <summary>Presses nothing: reads the named part's module state only.</summary>
        Inspect,
        /// <summary>A radiator's "Activate Radiator" (<c>ModuleActiveRadiator.Activate</c>).</summary>
        RadiatorOn,
        /// <summary>A radiator's "Shutdown Radiator" (<c>ModuleActiveRadiator.Shutdown</c>).</summary>
        RadiatorOff,
        /// <summary>An engine's "Activate Engine" (<c>ModuleEngines.Activate</c>, KSP 1.12.5
        /// decompiled: <c>[KSPEvent(guiActive = true, guiName = "#autoLOC_6001382")]</c>, the
        /// localization key reading "Activate Engine"; <c>ModuleEnginesFX</c> inherits it). It
        /// ignites the engine without staging, as the player's PAW click does.</summary>
        EngineActivate,
        /// <summary>Destroys the named part the way a crash does: stock's
        /// <c>Part.explode()</c> (explosion FX, then <c>Die()</c>, which fires
        /// <c>onPartDie</c> / <c>onCrewKilled</c> for its crew). Not a part-menu button; it lets a
        /// lane end a part deterministically when a physics impact may miss it.</summary>
        Explode,
        /// <summary>A parachute's "Deploy Chute" (<c>ModuleParachute.Deploy</c>, KSP 1.12.5
        /// decompiled: <c>[KSPEvent(guiActive = true, guiName = "#autoLOC_6001348")]</c>). It arms
        /// the chute without staging, as the player's PAW click does; stock opens it once its own
        /// pressure and altitude conditions hold.</summary>
        ChuteDeploy,
    }

    /// <summary>One module reading of the target part for the <c>state=</c> answer field: plain
    /// values the applier read from a live module, formatted by
    /// <see cref="TestCommandPartAction.FormatState"/>.</summary>
    internal struct PartActionModuleReading
    {
        internal enum ReadingKind { Converter, Lab, Experiment, AnimationGroup, Radiator, Engine }

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

        internal static PartActionModuleReading Radiator(bool cooling)
            => new PartActionModuleReading { Kind = ReadingKind.Radiator, Flag = cooling };

        internal static PartActionModuleReading Engine(bool ignited)
            => new PartActionModuleReading { Kind = ReadingKind.Engine, Flag = ignited };
    }

    /// <summary>The warp context of a press, read right after it: the target vessel's
    /// <c>packed</c>, the stock warp rung and rate, and the game clock
    /// (<see cref="TestCommandPartAction.AppendWarpContext"/>).</summary>
    internal struct PartActionWarpContext
    {
        internal bool Packed;
        internal int WarpIndex;
        internal double WarpRate;
        internal double UT;

        internal PartActionWarpContext(bool packed, int warpIndex, double warpRate, double ut)
        {
            Packed = packed;
            WarpIndex = warpIndex;
            WarpRate = warpRate;
            UT = ut;
        }
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
    /// state back. <c>radiatoron</c> / <c>radiatoroff</c> press a radiator's Activate / Shutdown
    /// (<c>ModuleActiveRadiator</c>), the switch a mining craft's overheating parts depend on.
    /// <c>activate</c> presses an engine's "Activate Engine" (<c>ModuleEngines.Activate</c>, which
    /// <c>ModuleEnginesFX</c> inherits): it ignites the engine without staging, so a lane can
    /// fire a solid booster whose stage also holds a parachute without deploying it.</para>
    ///
    /// <para><b>Packed vessels.</b> Nothing here refuses a PACKED (on-rails) vessel: it is still
    /// loaded and its part modules and events still exist, so a press in rails warp runs the same
    /// KSPEvent a player's PAW click runs then. Stock applies it at once (the converter's
    /// <c>IsActivated</c>, the radiator's <c>IsCooling</c>) and simulates the effect on its own:
    /// a converter catches up over the warp from its <c>lastUpdateTime</c>.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=PartAction button=undock|decouple|controlfrom|deploy|retract|
    /// startconverter|stopconverter|experiment|labprocess|labtransmit|inspect|radiatoron|radiatoroff|
    /// activate|explode|chute [part=&lt;part name&gt;] [converter=&lt;ConverterName&gt;] [all=true] [pid=&lt;persistentId&gt;]
    /// [expectSnapshot=true|false rec=&lt;recordingId&gt; tolerance=&lt;abs&gt;]</c>. The three snapshot
    /// args are for <c>inspect</c> only (<c>snapshot-arg-unexpected</c> elsewhere, and for
    /// <c>rec=</c> / <c>tolerance=</c> without <c>expectSnapshot=true</c>).
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
    /// (none with data: <c>experiment-no-data</c>); the target must be the ACTIVE vessel
    /// (<c>lab-not-active-vessel</c>: stock's <c>UpdatePageLabValue</c> reads the active vessel's
    /// lab). The press is the dialog button's body, <c>OnSendToLab</c> on the results dialog's
    /// current page for that part and datum (<c>labRoute=page</c>); with no such page the
    /// experiment's own "Review Data" (<c>ReviewDataEvent</c>) opens it first
    /// (<c>labRoute=review</c>; no page after it: <c>lab-page-missing</c>). Stock writes
    /// <c>ScienceData.labValue</c>, the data the lab stores, ONLY when the dialog shows the page
    /// with a lab aboard (<c>ExperimentsResultDialog.SetPage</c>), and nothing saves or copies
    /// it, so the press calls the page's <c>UpdatePageLabValue()</c> itself and refuses
    /// <c>lab-value-zero</c> when it is not positive, and <c>lab-unavailable</c> when no lab on
    /// the vessel can take the datum (stock's own <c>ScienceLabSearch</c>), both BEFORE the send:
    /// a send that stores nothing would still burn the subject's one lab slot and end the
    /// experiment. A dialog or page the press opened and then refused on is closed again (the
    /// whole dialog when the press opened it, else its one page through the page's Keep); a
    /// successful send dismisses the page itself (stock closes the dialog with its last page).
    /// <b>inspect</b> presses nothing; its candidate is the one part carrying the name. With
    /// <c>expectSnapshot=true rec=&lt;recordingId&gt; [tolerance=&lt;abs&gt;]</c> it compares each
    /// inspected part's <c>ModuleScienceLab</c> (<c>dataStored</c>, <c>storedScience</c>, the
    /// EXPERIMENT_DATA subject set) with the same part in the recording's end vessel snapshot
    /// (<see cref="EvaluateLabSnapshot"/>). <b>radiatoron / radiatoroff</b> candidates are the named part's
    /// <c>ModuleActiveRadiator</c>s whose <c>Activate</c> / <c>Shutdown</c> event is live (stock
    /// shows only the one that changes <c>IsCooling</c>, and neither on a deployable radiator,
    /// which its own Extend / Retract drives); named parts carrying no such module refuse
    /// <c>radiator-not-found</c>. <b>activate</b> candidates are the named part's
    /// <c>ModuleEngines</c> (and so <c>ModuleEnginesFX</c>) modules whose <c>Activate</c> event is
    /// live (stock hides it on an ignited engine, and on a non-restartable one already shut
    /// down); named parts carrying no engine module refuse <c>engine-not-found</c>. Only the
    /// engine module's event is pressed: a radiator on the same part also carries an
    /// <c>Activate</c>. <b>explode</b> is no part-menu button: its candidate is the one part
    /// carrying the name, and it calls stock's <c>Part.explode()</c> on it (the crash path:
    /// FX, then <c>Die()</c>, so <c>onPartDie</c> and, for a crewed part, <c>onCrewKilled</c>
    /// fire as on an impact); <c>all=true</c> is <c>all-arg-unexpected</c> and <c>state=</c> is
    /// empty (the part is gone).</para>
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
    /// activePid= activeName='..' converter='..' state='..' count= packed= warpIndex= warpRate= ut=</c>;
    /// the newer fields are appended so every earlier anchor still matches (the payload carries
    /// the same four warp fields last). A labprocess appends <c> labRoute=page|review
    /// labValue=&lt;F2&gt;</c> (one comma-joined entry per press) and an inspect
    /// <c> snapshotMatch=true|false|none</c> (payload <c>labRoute labValue</c> /
    /// <c>snapshotMatch snapshotMismatches</c> after the warp fields).</para>
    /// </summary>
    internal static class TestCommandPartAction
    {
        internal const string Verb = "PartAction";
        internal const string ButtonKey = "button";
        internal const string PartKey = "part";
        internal const string PidKey = "pid";
        internal const string ConverterKey = "converter";
        internal const string AllKey = "all";
        internal const string ExpectSnapshotKey = "expectSnapshot";
        internal const string RecKey = "rec";
        internal const string ToleranceKey = "tolerance";
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
        internal const string RadiatorOnToken = "radiatoron";
        internal const string RadiatorOffToken = "radiatoroff";
        internal const string ActivateToken = "activate";
        internal const string ExplodeToken = "explode";
        internal const string ChuteToken = "chute";

        /// <summary>The <c>event=</c> of every explode: stock's <c>Part.explode()</c>.</summary>
        internal const string ExplodeEventName = "explode";

        /// <summary>The closed <c>button=</c> vocabulary. hlib.PARTACTION_BUTTON_VALUES mirrors it
        /// (PartActionSourceSyncTests).</summary>
        internal static readonly string[] ButtonValues =
        {
            UndockToken, DecoupleToken, ControlFromToken, DeployToken, RetractToken,
            StartConverterToken, StopConverterToken, ExperimentToken, LabProcessToken,
            LabTransmitToken, InspectToken, RadiatorOnToken, RadiatorOffToken, ActivateToken,
            ExplodeToken, ChuteToken,
        };

        /// <summary>The <c>event=</c> of every labprocess: the results dialog page's own lab
        /// callback (the dialog button's body, KSP 1.12.5 decompiled: the experiment's private
        /// <c>sendDataToLab</c>, then the dialog's <c>dismissCurrentPage</c>).</summary>
        internal const string LabProcessDialogEventName = "OnSendToLab";

        /// <summary>The <c>labRoute=</c> of a labprocess that pressed the page already showing.</summary>
        internal const string LabRoutePageToken = "page";

        /// <summary>The <c>labRoute=</c> of a labprocess that opened the page first through the
        /// experiment's public <c>ReviewDataEvent</c> (its "Review Data" PAW button).</summary>
        internal const string LabRouteReviewToken = "review";

        /// <summary>The default absolute tolerance of <c>expectSnapshot=</c>, ReadVesselResources'.</summary>
        internal const double DefaultSnapshotTolerance = 0.01;

        /// <summary>The <c>snapshotMatch=</c> value when nothing was compared.</summary>
        internal const string SnapshotNotAskedToken = "none";

        internal const string ButtonArgMissingReason = "partaction-button-arg-missing";
        internal const string ButtonArgInvalidReason = "partaction-button-arg-invalid";
        internal const string PartArgMissingReason = "partaction-part-arg-missing";
        internal const string ConverterArgUnexpectedReason = "partaction-converter-arg-unexpected";
        internal const string AllArgInvalidReason = "partaction-all-arg-invalid";
        internal const string AllArgUnexpectedReason = "partaction-all-arg-unexpected";
        internal const string PidArgInvalidReason = "partaction-pid-arg-invalid";
        internal const string ExpectSnapshotArgInvalidReason = "partaction-expect-snapshot-arg-invalid";
        internal const string SnapshotArgUnexpectedReason = "partaction-snapshot-arg-unexpected";
        internal const string ExpectSnapshotNeedsRecReason = "partaction-expect-snapshot-needs-rec";
        internal const string RecArgInvalidReason = "partaction-rec-arg-invalid";
        internal const string ToleranceArgInvalidReason = "partaction-tolerance-arg-invalid";
        internal const string NoActiveVesselReason = "partaction-no-active-vessel";
        internal const string VesselNotFoundReason = "partaction-vessel-not-found";
        internal const string TargetIsGhostReason = "partaction-target-is-ghost";
        internal const string VesselUnloadedReason = "partaction-vessel-unloaded";
        internal const string UnknownRecordingReason = "partaction-unknown-recording";
        internal const string PartNotFoundReason = "partaction-part-not-found";
        internal const string ConverterNotFoundReason = "partaction-converter-not-found";
        internal const string PartAmbiguousReason = "partaction-part-ambiguous";
        internal const string EventUnavailableReason = "partaction-event-unavailable";
        internal const string ExperimentNoDataReason = "partaction-experiment-no-data";
        internal const string LabNotActiveVesselReason = "partaction-lab-not-active-vessel";
        internal const string LabPageMissingReason = "partaction-lab-page-missing";
        internal const string LabValueZeroReason = "partaction-lab-value-zero";
        internal const string LabUnavailableReason = "partaction-lab-unavailable";
        internal const string RadiatorNotFoundReason = "partaction-radiator-not-found";
        internal const string EngineNotFoundReason = "partaction-engine-not-found";
        internal const string ParachuteNotFoundReason = "partaction-parachute-not-found";

        // Post-call ERROR terminal: the event was invoked and threw.
        internal const string EventThrewReason = "partaction-event-threw";

        // Post-call ERROR terminal: an all=true labprocess walk refused on candidate k > 0,
        // after k earlier presses had already sent data (their lab slots are taken). The
        // typed gate reason rides the log line as cause=.
        internal const string LabWalkStoppedReason = "partaction-lab-walk-stopped";

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
            "partaction-expect-snapshot-arg-invalid",
            "partaction-snapshot-arg-unexpected",
            "partaction-expect-snapshot-needs-rec",
            "partaction-rec-arg-invalid",
            "partaction-tolerance-arg-invalid",
            "partaction-no-active-vessel",
            "partaction-vessel-not-found",
            "partaction-target-is-ghost",
            "partaction-vessel-unloaded",
            "partaction-unknown-recording",
            "partaction-part-not-found",
            "partaction-converter-not-found",
            "partaction-part-ambiguous",
            "partaction-event-unavailable",
            "partaction-experiment-no-data",
            "partaction-lab-not-active-vessel",
            "partaction-lab-page-missing",
            "partaction-lab-value-zero",
            "partaction-lab-unavailable",
            "partaction-radiator-not-found",
            "partaction-engine-not-found",
            "partaction-parachute-not-found",
        };

        /// <summary>The post-call ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "partaction-event-threw",
            "partaction-lab-walk-stopped",
        };

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
                case RadiatorOnToken: kind = PartActionKind.RadiatorOn; return null;
                case RadiatorOffToken: kind = PartActionKind.RadiatorOff; return null;
                case ActivateToken: kind = PartActionKind.EngineActivate; return null;
                case ExplodeToken: kind = PartActionKind.Explode; return null;
                case ChuteToken: kind = PartActionKind.ChuteDeploy; return null;
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
                case PartActionKind.RadiatorOn: return RadiatorOnToken;
                case PartActionKind.RadiatorOff: return RadiatorOffToken;
                case PartActionKind.EngineActivate: return ActivateToken;
                case PartActionKind.Explode: return ExplodeToken;
                case PartActionKind.ChuteDeploy: return ChuteToken;
                default: return "unknown";
            }
        }

        /// <summary>True when <paramref name="kind"/> needs a <c>part=</c>: every button but an
        /// undock is chosen by the player on one named part; an undock has one obvious port pair
        /// on an ordinary docked vessel.</summary>
        internal static bool RequiresPart(PartActionKind kind)
            => kind != PartActionKind.Unknown && kind != PartActionKind.Undock;

        /// <summary>True for the two radiator buttons, whose candidates come from
        /// <c>ModuleActiveRadiator</c> only.</summary>
        internal static bool IsRadiatorKind(PartActionKind kind)
            => kind == PartActionKind.RadiatorOn || kind == PartActionKind.RadiatorOff;

        /// <summary>True for the engine button, whose candidates come from <c>ModuleEngines</c>
        /// (and its subclass <c>ModuleEnginesFX</c>) only.</summary>
        internal static bool IsEngineKind(PartActionKind kind)
            => kind == PartActionKind.EngineActivate;

        /// <summary>True for the chute button, whose candidates come from <c>ModuleParachute</c>
        /// only.</summary>
        internal static bool IsParachuteKind(PartActionKind kind)
            => kind == PartActionKind.ChuteDeploy;

        /// <summary>True for the buttons whose candidates come from one module type only (the
        /// radiator pair, the engine button, the chute button): the applier counts that type's
        /// modules on the named parts for the <c>radiator-not-found</c> / <c>engine-not-found</c> /
        /// <c>parachute-not-found</c> gate.</summary>
        internal static bool IsTypedModuleKind(PartActionKind kind)
            => IsRadiatorKind(kind) || IsEngineKind(kind) || IsParachuteKind(kind);

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

        /// <summary>True for the buttons <c>all=true</c> applies to: every button but the four
        /// whose single press reshapes or re-points the vessel (undock, decouple, controlfrom,
        /// explode).</summary>
        internal static bool AcceptsAll(PartActionKind kind)
            => kind != PartActionKind.Unknown && kind != PartActionKind.Undock
                && kind != PartActionKind.Decouple && kind != PartActionKind.ControlFrom
                && kind != PartActionKind.Explode;

        /// <summary>True for the buttons whose candidates are the named parts themselves rather
        /// than a live module event on them: inspect (reads) and explode (destroys the part).</summary>
        internal static bool IsWholePartKind(PartActionKind kind)
            => kind == PartActionKind.Inspect || kind == PartActionKind.Explode;

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
                case PartActionKind.RadiatorOn: return new[] { "Activate" };
                case PartActionKind.RadiatorOff: return new[] { "Shutdown" };
                // ModuleEngines: [KSPEvent(guiActive = true, guiName = "#autoLOC_6001382")]
                // public virtual void Activate(), "Activate Engine" (KSP 1.12.5 decompiled).
                case PartActionKind.EngineActivate: return new[] { "Activate" };
                // ModuleParachute: [KSPEvent(guiActive = true, guiName = "#autoLOC_6001348")]
                // public void Deploy(), "Deploy Chute" (KSP 1.12.5 decompiled).
                case PartActionKind.ChuteDeploy: return new[] { "Deploy" };
                // labprocess presses the results dialog page's callback; inspect presses nothing;
                // explode calls Part.explode() on the part itself (no KSPEvent).
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
            => DecideCandidates(kind, partName, namedParts, converterName, matchingConverters,
                experimentModules, -1, candidates, all);

        /// <summary>The candidate gate with the typed-module count: a radiator button on named
        /// part(s) carrying no <c>ModuleActiveRadiator</c> at all, or the engine button on named
        /// part(s) carrying no <c>ModuleEngines</c> (<paramref name="typedModules"/> 0, counted
        /// whatever the event state), is <c>radiator-not-found</c> / <c>engine-not-found</c>, a
        /// spec fault like <c>converter-not-found</c>; a module that is there but already in the
        /// asked state (stock hides the pressed button: a cooling radiator's Activate, an
        /// ignited engine's Activate) stays <c>event-unavailable</c>. A negative count means
        /// "not counted" and never refuses.</summary>
        internal static string DecideCandidates(
            PartActionKind kind, string partName, int namedParts, string converterName,
            int matchingConverters, int experimentModules, int typedModules, int candidates, bool all)
        {
            if (partName != null && namedParts == 0) return PartNotFoundReason;
            if (converterName != null && matchingConverters == 0) return ConverterNotFoundReason;
            if (IsRadiatorKind(kind) && typedModules == 0) return RadiatorNotFoundReason;
            if (IsEngineKind(kind) && typedModules == 0) return EngineNotFoundReason;
            if (IsParachuteKind(kind) && typedModules == 0) return ParachuteNotFoundReason;
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
        /// <c>exp:data=&lt;count&gt;</c>; an animation group <c>deployed=0|1</c>; an active
        /// radiator <c>radiator:cooling=0|1</c>; an engine <c>engine:ignited=0|1</c>
        /// (<c>ModuleEngines.EngineIgnited</c>). Invariant culture; empty for no readings.
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
                    case PartActionModuleReading.ReadingKind.Radiator:
                        parts.Add("radiator:cooling=" + (r.Flag ? "1" : "0"));
                        break;
                    case PartActionModuleReading.ReadingKind.Engine:
                        parts.Add("engine:ignited=" + (r.Flag ? "1" : "0"));
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

        /// <summary>The OK payload, in wire order (the newer fields last). The four warp
        /// fields (<see cref="AppendWarpContext"/>) close it.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            PartActionKind kind, uint pid, string partName, uint partId, string eventName,
            uint newPid, string newName, int vesselsBefore, int vesselsAfter,
            uint activePid, string activeName, string converterName = null, string state = null,
            int count = 1, PartActionWarpContext warp = default(PartActionWarpContext))
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            var payload = new List<KeyValuePair<string, string>>
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
            payload.Add(new KeyValuePair<string, string>("packed", warp.Packed ? "1" : "0"));
            payload.Add(new KeyValuePair<string, string>("warpIndex", warp.WarpIndex.ToString(ic)));
            payload.Add(new KeyValuePair<string, string>("warpRate", warp.WarpRate.ToString("R", ic)));
            payload.Add(new KeyValuePair<string, string>("ut", warp.UT.ToString("R", ic)));
            return payload;
        }

        /// <summary>
        /// The ` packed=0|1 warpIndex=&lt;i&gt; warpRate=&lt;r&gt; ut=&lt;u&gt;` tail of the applied
        /// line: whether the target vessel was PACKED (on rails), the stock warp rung and rate,
        /// and the game clock, all read right after the press. A lane proves a press happened
        /// during a <c>WarpToUT detach=true</c> warp with <c>warpIndex=[1-9]</c> (the rung
        /// moves on stock's <c>setRate</c> call; the rate eases up over the next frames).
        /// </summary>
        internal static string AppendWarpContext(PartActionWarpContext warp)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return " packed=" + (warp.Packed ? "1" : "0")
                + " warpIndex=" + warp.WarpIndex.ToString(ic)
                + " warpRate=" + warp.WarpRate.ToString("R", ic)
                + " ut=" + warp.UT.ToString("R", ic);
        }

        /// <summary>The grep-stable success line. Names are single-quoted so a spec can anchor
        /// a name containing spaces.</summary>
        internal static string FormatAppliedLine(
            PartActionKind kind, uint pid, string partName, uint partId, string eventName,
            uint newPid, string newName, int vesselsBefore, int vesselsAfter,
            uint activePid, string activeName, string converterName = null, string state = null,
            int count = 1, PartActionWarpContext warp = default(PartActionWarpContext))
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
                + " count=" + count.ToString(ic)
                + AppendWarpContext(warp);
        }

        // ----- labprocess -----

        /// <summary>The two ways a labprocess reaches the results dialog page it presses.</summary>
        internal enum LabProcessRoute
        {
            /// <summary>The dialog's current page is already this part's page for this datum.</summary>
            OpenPage,
            /// <summary>No such page: the experiment's <c>ReviewDataEvent</c> opens it first.</summary>
            Review,
        }

        /// <summary>What to undo when a labprocess refuses after it opened something.</summary>
        internal enum LabDialogCleanup
        {
            /// <summary>Nothing: the page was already up, or the review added no page.</summary>
            None,
            /// <summary>The press opened the whole dialog: <c>ExperimentsResultDialog.Dismiss()</c>
            /// (its <c>OnDestroy</c> keeps every page's data).</summary>
            DismissDialog,
            /// <summary>The press added one page to a dialog already up: the page's own Keep
            /// callback (the experiment's <c>keepData</c>, then <c>dismissCurrentPage</c>).</summary>
            KeepPage,
        }

        /// <summary>The <c>labRoute=</c> token of <paramref name="route"/>.</summary>
        internal static string LabRouteToken(LabProcessRoute route)
            => route == LabProcessRoute.OpenPage ? LabRoutePageToken : LabRouteReviewToken;

        /// <summary>
        /// Whether a results dialog page is the one a labprocess may press: present, hosted by
        /// the target part, carrying the lab callback, and holding the experiment module's OWN
        /// datum (the same object, not just the same subject: stock's <c>sendDataToLab</c>
        /// processes the module's <c>experimentData</c>, so the <c>labValue</c> written on any
        /// other object would never reach the lab).
        /// </summary>
        internal static bool LabPageMatches(bool pagePresent, bool hostIsPart, bool hasLabCallback,
            bool pageDataIsModuleDatum)
            => pagePresent && hostIsPart && hasLabCallback && pageDataIsModuleDatum;

        /// <summary>The route: the open page when it matches, else a review that opens it.</summary>
        internal static LabProcessRoute DecideLabRoute(bool openPageMatches)
            => openPageMatches ? LabProcessRoute.OpenPage : LabProcessRoute.Review;

        /// <summary>The labprocess target gate, before any press: stock's
        /// <c>UpdatePageLabValue</c> reads <c>FlightGlobals.ActiveVessel</c>'s lab and situation,
        /// so a target that is not the active vessel would be valued against another vessel.</summary>
        internal static string DecideLabTarget(bool targetIsActiveVessel)
            => targetIsActiveVessel ? null : LabNotActiveVesselReason;

        /// <summary>The page gate after a review: the review must leave this part's page for
        /// this datum current.</summary>
        internal static string DecideLabPageAfterReview(bool pageMatches)
            => pageMatches ? null : LabPageMissingReason;

        /// <summary>The value gate, after <c>UpdatePageLabValue()</c> and BEFORE the send: a
        /// zero (or NaN) <c>labValue</c> would store no data yet still take the subject's one
        /// lab slot and end the experiment.</summary>
        internal static string DecideLabValue(float labValue)
            => labValue > 0f ? null : LabValueZeroReason;

        /// <summary>The lab gate, BEFORE the send: stock's own <c>ScienceLabSearch</c> over the
        /// vessel (the one <c>sendDataToLab</c> runs) must find a lab that can take the datum,
        /// else the send only posts the search's error.</summary>
        internal static string DecideLabSearch(bool nextLabFound)
            => nextLabFound ? null : LabUnavailableReason;

        /// <summary>
        /// What a refused labprocess closes again: nothing on the open-page route (the page was
        /// the player's or the lane's); on the review route the whole dialog when none was up
        /// before, else the one page the review added (a review whose datum already had a page
        /// only re-points the dialog at it and adds none).
        /// </summary>
        internal static LabDialogCleanup DecideLabCleanup(LabProcessRoute route, bool dialogExistedBefore,
            int pagesBefore, int pagesAfter)
        {
            if (route == LabProcessRoute.OpenPage) return LabDialogCleanup.None;
            if (!dialogExistedBefore) return LabDialogCleanup.DismissDialog;
            return pagesAfter > pagesBefore ? LabDialogCleanup.KeepPage : LabDialogCleanup.None;
        }

        /// <summary>
        /// The answer of a labprocess gate refusal: REJECTED with the typed reason when no press
        /// of the walk has sent yet (nothing changed), else ERROR
        /// <see cref="LabWalkStoppedReason"/> (an all=true walk stopped after earlier sends took
        /// their lab slots: a terminal after the verb acted, never a side-effect-free refusal).
        /// Returns the verdict; <paramref name="answerReason"/> is the wire reason.
        /// </summary>
        internal static string DecideLabRefusalVerdict(string gateReason, int succeeded, out string answerReason)
        {
            if (succeeded <= 0)
            {
                answerReason = gateReason;
                return "REJECTED";
            }
            answerReason = LabWalkStoppedReason;
            return "ERROR";
        }

        /// <summary>The ` labRoute=.. labValue=..` tail a labprocess appends to the applied line:
        /// one comma-joined entry per press, values invariant <c>F2</c>.</summary>
        internal static string FormatLabTail(IList<LabProcessRoute> routes, IList<float> labValues)
        {
            return " labRoute=" + JoinLabRoutes(routes) + " labValue=" + JoinLabValues(labValues);
        }

        /// <summary>The <c>labRoute</c> / <c>labValue</c> payload fields (after the warp fields).</summary>
        internal static List<KeyValuePair<string, string>> LabPayloadFields(
            IList<LabProcessRoute> routes, IList<float> labValues)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("labRoute", JoinLabRoutes(routes)),
                new KeyValuePair<string, string>("labValue", JoinLabValues(labValues)),
            };
        }

        private static string JoinLabRoutes(IList<LabProcessRoute> routes)
        {
            if (routes == null || routes.Count == 0) return string.Empty;
            var parts = new string[routes.Count];
            for (int i = 0; i < routes.Count; i++) parts[i] = LabRouteToken(routes[i]);
            return string.Join(",", parts);
        }

        private static string JoinLabValues(IList<float> labValues)
        {
            if (labValues == null || labValues.Count == 0) return string.Empty;
            CultureInfo ic = CultureInfo.InvariantCulture;
            var parts = new string[labValues.Count];
            for (int i = 0; i < labValues.Count; i++) parts[i] = labValues[i].ToString("F2", ic);
            return string.Join(",", parts);
        }

        /// <summary>The grep-stable labprocess refusal detail: the gate's numbers.</summary>
        internal static string FormatLabRefusalDetail(uint pid, string partName, uint partId, string subjectId,
            LabProcessRoute route, LabDialogCleanup cleanup, float labValue, string labError,
            int succeeded, int candidates)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "button=" + LabProcessToken
                + " pid=" + pid.ToString(ic)
                + " part='" + (partName ?? string.Empty) + "'"
                + " partId=" + partId.ToString(ic)
                + " subject='" + (subjectId ?? string.Empty) + "'"
                + " labRoute=" + LabRouteToken(route)
                + " labValue=" + labValue.ToString("F2", ic)
                + " labError=" + (string.IsNullOrEmpty(labError) ? "-" : labError)
                + " cleanup=" + cleanup
                + " " + FormatPressProgress(succeeded, candidates);
        }

        // ----- inspect expectSnapshot -----

        /// <summary>
        /// Parses the inspect-only snapshot args. <c>expectSnapshot=</c> is <c>true</c> /
        /// <c>false</c> (ordinal; anything else <c>expect-snapshot-arg-invalid</c>); any of the
        /// three on another button, or <c>rec=</c> / <c>tolerance=</c> without
        /// <c>expectSnapshot=true</c>, is <c>snapshot-arg-unexpected</c> (it would be silently
        /// ignored); <c>expectSnapshot=true</c> needs <c>rec=</c>
        /// (<c>expect-snapshot-needs-rec</c>), a recording id <paramref name="isValidRecordingId"/>
        /// accepts (<c>rec-arg-invalid</c>); <c>tolerance=</c> is a finite invariant decimal
        /// &gt;= 0 (<c>tolerance-arg-invalid</c>), default <see cref="DefaultSnapshotTolerance"/>.
        /// </summary>
        internal static string ParseSnapshotArgs(PartActionKind kind, string rawExpect, string rawRec,
            string rawTolerance, Func<string, bool> isValidRecordingId,
            out bool expectSnapshot, out string recId, out double tolerance)
        {
            expectSnapshot = false;
            recId = null;
            tolerance = DefaultSnapshotTolerance;
            bool hasExpect = !string.IsNullOrEmpty(rawExpect);
            bool hasRec = !string.IsNullOrEmpty(rawRec);
            bool hasTolerance = !string.IsNullOrEmpty(rawTolerance);
            bool expect = false;
            if (hasExpect)
            {
                if (string.Equals(rawExpect, "true", StringComparison.Ordinal)) expect = true;
                else if (!string.Equals(rawExpect, "false", StringComparison.Ordinal))
                    return ExpectSnapshotArgInvalidReason;
            }
            if ((hasExpect || hasRec || hasTolerance) && kind != PartActionKind.Inspect)
                return SnapshotArgUnexpectedReason;
            if (expect && !hasRec) return ExpectSnapshotNeedsRecReason;
            if (!expect && (hasRec || hasTolerance)) return SnapshotArgUnexpectedReason;
            if (!expect) return null;
            if (isValidRecordingId == null || !isValidRecordingId(rawRec)) return RecArgInvalidReason;
            if (hasTolerance)
            {
                if (!double.TryParse(rawTolerance, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out double t) || double.IsNaN(t) || double.IsInfinity(t) || t < 0)
                    return ToleranceArgInvalidReason;
                tolerance = t;
            }
            expectSnapshot = true;
            recId = rawRec;
            return null;
        }

        /// <summary>One PART of a recording's end vessel snapshot, as the lab comparison reads it.</summary>
        internal sealed class LabSnapshotPart
        {
            internal uint Uid;
            internal uint PersistentId;
            internal string Name;
            /// <summary>True when the PART carries a <c>MODULE name = ModuleScienceLab</c>.</summary>
            internal bool HasLab;
            internal double DataStored;
            internal double StoredScience;
            /// <summary>The union of every <c>experiment</c> value under the lab module's
            /// EXPERIMENT_DATA node(s) (stock's <c>ModuleScienceLab.OnSave</c>).</summary>
            internal List<string> Experiments = new List<string>();
        }

        /// <summary>The live lab of one inspected part, read by the applier.</summary>
        internal struct LiveLabReading
        {
            internal uint PartId;
            internal uint PersistentId;
            internal string Name;
            /// <summary>The part's index in <c>Vessel.parts</c> and the vessel's part count.</summary>
            internal int Index;
            internal int PartCount;
            internal bool HasLab;
            internal double DataStored;
            internal double StoredScience;
            internal IList<string> Experiments;
        }

        /// <summary>One compared field of one lab part.</summary>
        internal struct LabFieldComparison
        {
            internal string Field;
            internal string Live;
            internal string Snapshot;
            internal string Diff;
            internal bool Match;
            /// <summary>For <c>experiments</c>: the subjects only the live lab / only the
            /// snapshot holds, ordinal-sorted, comma-joined (null for the numeric fields).</summary>
            internal string OnlyLive;
            internal string OnlySnapshot;
        }

        /// <summary>The result of one inspected part's snapshot comparison.</summary>
        internal sealed class LabSnapshotOutcome
        {
            /// <summary><c>true</c>, <c>false</c>, or <c>none</c> for a part without a lab.</summary>
            internal string Token = SnapshotNotAskedToken;
            /// <summary>The grep-stable lines, in order.</summary>
            internal List<string> Lines = new List<string>();
            /// <summary><c>&lt;partId&gt;:&lt;field or reason&gt;</c> per failed field.</summary>
            internal List<string> Mismatches = new List<string>();
        }

        /// <summary>The PART list of a vessel snapshot, in node order, or null when the snapshot
        /// is null or has no PART node.</summary>
        internal static List<LabSnapshotPart> ParseSnapshotParts(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return null;
            ConfigNode[] partNodes = vesselSnapshot.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0) return null;
            CultureInfo ic = CultureInfo.InvariantCulture;
            var parts = new List<LabSnapshotPart>(partNodes.Length);
            foreach (ConfigNode pn in partNodes)
            {
                var sp = new LabSnapshotPart { Name = pn.GetValue("name") };
                uint.TryParse(pn.GetValue("uid"), NumberStyles.Integer, ic, out sp.Uid);
                uint.TryParse(pn.GetValue("persistentId"), NumberStyles.Integer, ic, out sp.PersistentId);
                foreach (ConfigNode mod in pn.GetNodes("MODULE"))
                {
                    if (!string.Equals(mod.GetValue("name"), "ModuleScienceLab", StringComparison.Ordinal))
                        continue;
                    sp.HasLab = true;
                    double.TryParse(mod.GetValue("dataStored"), NumberStyles.Float, ic, out sp.DataStored);
                    double.TryParse(mod.GetValue("storedScience"), NumberStyles.Float, ic, out sp.StoredScience);
                    foreach (ConfigNode ed in mod.GetNodes("EXPERIMENT_DATA"))
                        foreach (string subject in ed.GetValues("experiment"))
                            if (!string.IsNullOrEmpty(subject) && !sp.Experiments.Contains(subject))
                                sp.Experiments.Add(subject);
                    break;
                }
                parts.Add(sp);
            }
            return parts;
        }

        /// <summary>
        /// Finds the snapshot PART of a live part, in this order: the same flight id
        /// (<c>uid</c>; the recorded vessel itself), the same part persistentId, the same index
        /// with the same name in a snapshot of the same part count (a Parsek spawn regenerates
        /// both ids but keeps the PART order), else the one snapshot PART with that name that
        /// carries a lab. Returns the index (or -1) and how it matched
        /// (<c>uid|persistentId|index|name|none</c>).
        /// </summary>
        internal static int MatchSnapshotPart(IList<LabSnapshotPart> parts, uint liveFlightId,
            uint livePersistentId, string liveName, int liveIndex, int livePartCount, out string matchedBy)
        {
            matchedBy = "none";
            if (parts == null || parts.Count == 0) return -1;
            if (liveFlightId != 0)
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i].Uid == liveFlightId) { matchedBy = "uid"; return i; }
            if (livePersistentId != 0)
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i].PersistentId == livePersistentId) { matchedBy = "persistentId"; return i; }
            if (liveIndex >= 0 && livePartCount == parts.Count && liveIndex < parts.Count
                && string.Equals(parts[liveIndex].Name, liveName, StringComparison.Ordinal))
            {
                matchedBy = "index";
                return liveIndex;
            }
            int found = -1;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!parts[i].HasLab || !string.Equals(parts[i].Name, liveName, StringComparison.Ordinal))
                    continue;
                if (found >= 0) return -1; // two same-name labs: no silent pick
                found = i;
            }
            if (found >= 0) matchedBy = "name";
            return found;
        }

        /// <summary>Compares one live lab with its snapshot PART: <c>dataStored</c> and
        /// <c>storedScience</c> within <paramref name="tolerance"/> (absolute, at float
        /// precision: both sides rounded through float), and the
        /// EXPERIMENT_DATA subject SETS for equality.</summary>
        internal static List<LabFieldComparison> CompareLab(LiveLabReading live, LabSnapshotPart snap,
            double tolerance)
        {
            var rows = new List<LabFieldComparison>(3)
            {
                CompareNumber("dataStored", live.DataStored, snap.DataStored, tolerance),
                CompareNumber("storedScience", live.StoredScience, snap.StoredScience, tolerance),
            };
            var liveSet = new SortedSet<string>(StringComparer.Ordinal);
            if (live.Experiments != null)
                foreach (string s in live.Experiments) if (!string.IsNullOrEmpty(s)) liveSet.Add(s);
            var snapSet = new SortedSet<string>(snap.Experiments ?? new List<string>(), StringComparer.Ordinal);
            var onlyLive = new List<string>();
            var onlySnap = new List<string>();
            foreach (string s in liveSet) if (!snapSet.Contains(s)) onlyLive.Add(s);
            foreach (string s in snapSet) if (!liveSet.Contains(s)) onlySnap.Add(s);
            CultureInfo ic = CultureInfo.InvariantCulture;
            rows.Add(new LabFieldComparison
            {
                Field = "experiments",
                Live = liveSet.Count.ToString(ic),
                Snapshot = snapSet.Count.ToString(ic),
                Diff = (onlyLive.Count + onlySnap.Count).ToString(ic),
                Match = onlyLive.Count == 0 && onlySnap.Count == 0,
                OnlyLive = string.Join(",", onlyLive.ToArray()),
                OnlySnapshot = string.Join(",", onlySnap.ToArray()),
            });
            return rows;
        }

        /// <summary>Both sides are float fields in stock (<c>[KSPField] float</c>), the
        /// snapshot holding the float's round-trip text: each side is rounded through float
        /// before the difference, so a live value widened to double and the same value parsed
        /// back from text compare equal, and <c>tolerance=0</c> means exact float equality.</summary>
        private static LabFieldComparison CompareNumber(string field, double live, double snapshot, double tolerance)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            live = (float)live;
            snapshot = (float)snapshot;
            double diff = live - snapshot;
            return new LabFieldComparison
            {
                Field = field,
                Live = live.ToString("F4", ic),
                Snapshot = snapshot.ToString("F4", ic),
                Diff = diff.ToString("F4", ic),
                Match = Math.Abs(diff) <= tolerance,
            };
        }

        private static string SnapshotLineHead(string recId, uint partId)
            => "partaction snapshot rec=" + (recId ?? string.Empty)
                + " partId=" + partId.ToString(CultureInfo.InvariantCulture);

        /// <summary>One compared field: <c>partaction snapshot rec= partId= field= live= snapshot=
        /// diff= match=</c> (the subject set adds <c>onlyLive='..' onlySnapshot='..'</c>).</summary>
        internal static string FormatSnapshotFieldLine(string recId, uint partId, LabFieldComparison c)
        {
            string line = SnapshotLineHead(recId, partId)
                + " field=" + c.Field
                + " live=" + c.Live
                + " snapshot=" + c.Snapshot
                + " diff=" + c.Diff
                + " match=" + (c.Match ? "true" : "false");
            if (c.OnlyLive != null || c.OnlySnapshot != null)
                line += " onlyLive='" + (c.OnlyLive ?? string.Empty) + "'"
                    + " onlySnapshot='" + (c.OnlySnapshot ?? string.Empty) + "'";
            return line;
        }

        /// <summary>
        /// One inspected part against the recording's end snapshot. A part without a live
        /// <c>ModuleScienceLab</c> answers <c>none</c>. A lab part answers <c>false</c> when the
        /// recording has no snapshot (<c>reason=no-snapshot</c>), no snapshot PART maps to it
        /// (<c>reason=part-not-in-snapshot</c>) or the mapped PART carries no lab
        /// (<c>reason=no-lab-in-snapshot</c>); else one line per field and <c>true</c> only when
        /// every field matches. The first line is the <c>field=part</c> mapping line.
        /// </summary>
        internal static LabSnapshotOutcome EvaluateLabSnapshot(string recId, LiveLabReading live,
            IList<LabSnapshotPart> snapshotParts, double tolerance)
        {
            var o = new LabSnapshotOutcome();
            CultureInfo ic = CultureInfo.InvariantCulture;
            string head = SnapshotLineHead(recId, live.PartId) + " field=part";
            if (!live.HasLab)
            {
                o.Token = SnapshotNotAskedToken;
                o.Lines.Add(head + " match=none reason=no-lab-module");
                return o;
            }
            string reason = null;
            int index = -1;
            string matchedBy = "none";
            if (snapshotParts == null) reason = "no-snapshot";
            else
            {
                index = MatchSnapshotPart(snapshotParts, live.PartId, live.PersistentId, live.Name,
                    live.Index, live.PartCount, out matchedBy);
                if (index < 0) reason = "part-not-in-snapshot";
                else if (!snapshotParts[index].HasLab) reason = "no-lab-in-snapshot";
            }
            if (reason != null)
            {
                o.Token = "false";
                o.Lines.Add(head + " matchedBy=" + matchedBy + " match=false reason=" + reason);
                o.Mismatches.Add(live.PartId.ToString(ic) + ":" + reason);
                return o;
            }
            o.Lines.Add(head + " matchedBy=" + matchedBy + " snapshotIndex=" + index.ToString(ic)
                + " match=true");
            bool all = true;
            foreach (LabFieldComparison c in CompareLab(live, snapshotParts[index], tolerance))
            {
                o.Lines.Add(FormatSnapshotFieldLine(recId, live.PartId, c));
                if (c.Match) continue;
                all = false;
                o.Mismatches.Add(live.PartId.ToString(ic) + ":" + c.Field);
            }
            o.Token = all ? "true" : "false";
            return o;
        }

        /// <summary>Folds per-part tokens: <c>false</c> if any part is false, else <c>true</c> if
        /// any part was compared, else <c>none</c> (no lab among the inspected parts, or no
        /// comparison asked).</summary>
        internal static string FoldSnapshotTokens(IEnumerable<string> tokens)
        {
            bool anyTrue = false;
            if (tokens != null)
                foreach (string t in tokens)
                {
                    if (t == "false") return "false";
                    if (t == "true") anyTrue = true;
                }
            return anyTrue ? "true" : SnapshotNotAskedToken;
        }

        /// <summary>The ` snapshotMatch=..` tail an inspect appends to the applied line.</summary>
        internal static string FormatSnapshotTail(string token)
            => " snapshotMatch=" + (string.IsNullOrEmpty(token) ? SnapshotNotAskedToken : token);

        /// <summary>The <c>snapshotMatch</c> / <c>snapshotMismatches</c> payload fields.</summary>
        internal static List<KeyValuePair<string, string>> SnapshotPayloadFields(string token,
            IList<string> mismatches)
        {
            string list = mismatches == null ? string.Empty : string.Join(",", new List<string>(mismatches).ToArray());
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("snapshotMatch",
                    string.IsNullOrEmpty(token) ? SnapshotNotAskedToken : token),
                new KeyValuePair<string, string>("snapshotMismatches", list),
            };
        }
    }
}
