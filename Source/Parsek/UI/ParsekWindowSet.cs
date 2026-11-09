namespace Parsek
{
    /// <summary>
    /// The sub-windows of one scene's <see cref="ParsekUI"/>: built once, in one fixed
    /// order, from inside the ParsekUI constructor (through its build callback, at the point
    /// where the constructor used to build them itself), and held here with their concrete
    /// types. ParsekUI reaches each one through the interface in
    /// <c>ParsekUIWindowContracts.cs</c> that lists what ParsekUI calls on it; everything
    /// that needs the concrete window (a sibling window, the automation seam, a test) asks
    /// this set for it by name.
    /// </summary>
    internal sealed class ParsekWindowSet : IParsekUIWindows
    {
        private readonly RecordingsTableUI recordingsTableUI;
        private readonly MissionsWindowUI missionsUI;
        private readonly SpawnControlUI spawnControlUI;
        private readonly TimelineWindowUI timelineUI;
        private readonly KerbalsWindowUI kerbalsUI;
        private readonly TestRunnerUI testRunnerUI;
        private readonly SettingsWindowUI settingsUI;
        private readonly LogisticsWindowUI logisticsUI;
        // One reusable log-table instance, retargeted per mission (the Mission Log).
        private readonly StructureListWindowUI structureListUI;
        // A second instance of the same window, opened from a supply route's Log button
        // (its own id, rect, lock and open state).
        private readonly StructureListWindowUI routeHistoryUI;

        private ParsekWindowSet(ParsekUI ui)
        {
            recordingsTableUI = new RecordingsTableUI(ui);
            missionsUI = new MissionsWindowUI(ui);
            spawnControlUI = new SpawnControlUI(ui);
            timelineUI = new TimelineWindowUI(ui);
            kerbalsUI = new KerbalsWindowUI(ui);
            testRunnerUI = new TestRunnerUI(ui);
            settingsUI = new SettingsWindowUI(ui);
            logisticsUI = new LogisticsWindowUI(ui);
            structureListUI = new StructureListWindowUI(ui);
            routeHistoryUI = new StructureListWindowUI(ui, routeHistory: true);
        }

        /// <summary>The FLIGHT scene's ParsekUI with its windows.</summary>
        internal static ParsekUI BuildUI(ParsekFlight flight)
        {
            return new ParsekUI(flight, ui => new ParsekWindowSet(ui));
        }

        /// <summary>A ParsekUI for a scene without a flight host (the Space Center).</summary>
        internal static ParsekUI BuildUI(UIMode mode)
        {
            return new ParsekUI(mode, ui => new ParsekWindowSet(ui));
        }

        /// <summary>
        /// The window set of <paramref name="ui"/>, or null for a null ui. Every ParsekUI is
        /// built through <see cref="BuildUI(UIMode)"/> or <see cref="BuildUI(ParsekFlight)"/>,
        /// so a ui whose windows are not this type is a wiring defect: logged at Warn, and
        /// null, as the old accessors were before construction.
        /// </summary>
        internal static ParsekWindowSet Of(ParsekUI ui)
        {
            if (ui == null)
                return null;
            ParsekWindowSet set = ui.Windows as ParsekWindowSet;
            if (set == null)
                ParsekLog.Warn("UI", "ParsekWindowSet.Of: the ParsekUI was not built with a ParsekWindowSet");
            return set;
        }

        internal RecordingsTableUI GetRecordingsTableUI() => recordingsTableUI;
        // The Missions view is drawn as a tab inside the Recordings window
        // (RecordingsTableUI dispatches to it); it is not a standalone window.
        internal MissionsWindowUI GetMissionsUI() => missionsUI;
        internal SpawnControlUI GetSpawnControlUI() => spawnControlUI;
        internal TimelineWindowUI GetTimelineUI() => timelineUI;
        internal KerbalsWindowUI GetKerbalsUI() => kerbalsUI;
        internal TestRunnerUI GetTestRunnerUI() => testRunnerUI;
        internal SettingsWindowUI GetSettingsWindowUI() => settingsUI;
        internal LogisticsWindowUI GetLogisticsUI() => logisticsUI;
        internal StructureListWindowUI GetStructureListUI() => structureListUI;
        internal StructureListWindowUI GetRouteHistoryUI() => routeHistoryUI;

        IRecordingsTableWindow IParsekUIWindows.RecordingsTable => recordingsTableUI;
        ISpawnControlWindow IParsekUIWindows.SpawnControl => spawnControlUI;
        ITimelineWindow IParsekUIWindows.Timeline => timelineUI;
        IKerbalsWindow IParsekUIWindows.Kerbals => kerbalsUI;
        ITestRunnerWindow IParsekUIWindows.TestRunner => testRunnerUI;
        ISettingsWindow IParsekUIWindows.Settings => settingsUI;
        ILogisticsWindow IParsekUIWindows.Logistics => logisticsUI;
        IStructureLogWindow IParsekUIWindows.StructureList => structureListUI;
        IStructureLogWindow IParsekUIWindows.RouteHistory => routeHistoryUI;
    }
}
