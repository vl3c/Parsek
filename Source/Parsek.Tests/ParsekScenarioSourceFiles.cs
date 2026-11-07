using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>ParsekScenario</c> is one partial class. <c>scripts/refactor/split_parsekscenario.py</c>
    /// splits the text of <c>ParsekScenario.cs</c> into <c>ParsekScenario.cs</c> (every field,
    /// constant, auto-property, the Unity / ScenarioModule callbacks OnAwake, Update, OnSave,
    /// OnLoad and OnDestroy, the instance and test seams and the state-version bumps) and the
    /// <c>ParsekScenario.&lt;Area&gt;.cs</c> files it writes, listed in
    /// <see cref="SplitFileNames"/>. A source gate that read "ParsekScenario.cs" as a whole reads
    /// exactly that set as one text: the main file first, then the area files in ordinal
    /// file-name order, each file whole, so a member's body is never split across the join. The
    /// older siblings in <see cref="OtherPartialFileNames"/> were never part of that file and are
    /// NOT read, so a gate's scope stays what it was. Every <c>ParsekScenario.*.cs</c> on disk
    /// must be named in one of the two lists, or <see cref="Paths"/> throws naming the new file.
    /// A gate that pins which FILES hold a token (a walk over every source file) names the file
    /// that holds it now.
    /// </summary>
    internal static class ParsekScenarioSourceFiles
    {
        internal const string MainFileName = "ParsekScenario.cs";

        internal static readonly string[] SplitFileNames =
        {
            "ParsekScenario.AutoCommit.cs",
            "ParsekScenario.CommittedTreeCarry.cs",
            "ParsekScenario.Diagnostics.cs",
            "ParsekScenario.InertPassthrough.cs",
            "ParsekScenario.LoadLedgerRecalc.cs",
            "ParsekScenario.LoadSteps.cs",
            "ParsekScenario.QuickloadDiscard.cs",
            "ParsekScenario.RewindLoad.cs",
            "ParsekScenario.RewindStaging.cs",
            "ParsekScenario.Save.cs",
            "ParsekScenario.SceneTransitions.cs",
            "ParsekScenario.StockActionIntent.cs",
            "ParsekScenario.TestBatchReconcile.cs",
            "ParsekScenario.TreeLoad.cs",
            "ParsekScenario.TreeRestore.cs",
            "ParsekScenario.VesselLifecycle.cs",
            "ParsekScenario.VesselStrip.cs",
        };

        /// <summary>
        /// The older sibling partials, never part of the split file and not read. Listed so a
        /// NEW <c>ParsekScenario.*.cs</c> fails <see cref="Paths"/> until it is declared.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames =
        {
            "ParsekScenario.DiscardedFlightRestore.cs",
            "ParsekScenario.HydrationRepair.cs",
            "ParsekScenario.InSessionHandoff.cs",
            "ParsekScenario.LoadDroppedRecordings.cs",
            "ParsekScenario.RemovedReplayFlags.cs",
            "ParsekScenario.RevertLedgerSnapshot.cs",
            "ParsekScenario.RewindVesselHold.cs",
            "ParsekScenario.SessionReset.cs",
            "ParsekScenario.Trim.cs",
        };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            return PartialClassSourceFiles.Paths(
                parsekSourceDir,
                MainFileName,
                SplitFileNames,
                OtherPartialFileNames,
                nameof(ParsekScenarioSourceFiles));
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }

        /// <summary>
        /// <see cref="File.ReadAllText(string)"/> for a source reader that takes a path: the
        /// path of <c>ParsekScenario.cs</c> reads <see cref="ReadAll"/> (the text that was
        /// ParsekScenario.cs), any other path reads what
        /// <see cref="ParsekFlightSourceFiles.ReadText"/> reads (that one file, or the text that
        /// was ParsekFlight.cs). For a reader of ONE named file, never for a walk over every
        /// source file (a walk reads each file on its own).
        /// </summary>
        internal static string ReadText(string path)
        {
            if (string.Equals(Path.GetFileName(path), MainFileName, StringComparison.Ordinal))
                return ReadAll(Path.GetDirectoryName(path));
            return ParsekFlightSourceFiles.ReadText(path);
        }
    }
}
