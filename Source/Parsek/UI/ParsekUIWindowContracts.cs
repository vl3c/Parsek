using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    // What ParsekUI itself calls on each of its sub-windows. ParsekUI holds every window
    // through one of these, so it names no window type; the concrete windows are built and
    // held by ParsekWindowSet, which callers ask for a typed window. Each interface lists
    // exactly the members ParsekUI calls on that window, so ParsekUI's own call sites are
    // unchanged and compile-time checked.

    internal interface IRecordingsTableWindow
    {
        bool IsOpen { get; set; }
        void DrawIfOpen(Rect mainWindowRect);
        bool IsMouseOverOpenWindow(Vector2 mousePosition);
        void ReleaseInputLock();
        bool IsGroupPickerOpen { get; }
        void CloseGroupPickerForModeChange();
        void ClampTabForBasic();
        List<string> KnownEmptyGroups { get; }
    }

    internal interface ITimelineWindow
    {
        bool IsOpen { get; set; }
        void DrawIfOpen(Rect mainWindowRect);
        void ReleaseInputLock();
        void InvalidateCache();
        void SubscribeTimelineDataChanged();
        void UnsubscribeTimelineDataChanged();
    }

    internal interface IKerbalsWindow
    {
        bool IsOpen { get; set; }
        void DrawIfOpen(Rect mainWindowRect);
        void ReleaseInputLock();
        void InvalidateCache();
        void SubscribeTimelineDataChanged();
        void UnsubscribeTimelineDataChanged();
        void SubscribeLiveCrewEvents();
        void UnsubscribeLiveCrewEvents();
    }

    internal interface ISettingsWindow
    {
        bool IsOpen { get; set; }
        void DrawIfOpen(Rect mainWindowRect);
        bool IsMouseOverOpenWindow(Vector2 mousePosition);
        void ReleaseInputLock();
        void RequestHeightRemeasure();
    }

    internal interface ISpawnControlWindow
    {
        bool IsOpen { get; set; }
        bool HasInputLock { get; }
        void DrawIfOpen(Rect mainWindowRect, ParsekFlight flight, bool inFlight);
        void ReleaseInputLock();
    }

    internal interface ITestRunnerWindow
    {
        bool IsOpen { get; set; }
        bool HasInputLock { get; }
        void DrawIfOpen(Rect mainWindowRect, MonoBehaviour host);
        bool IsMouseOverOpenWindow(Vector2 mousePosition);
        void ReleaseInputLock();
    }

    internal interface ILogisticsWindow
    {
        void DrawIfOpen(Rect mainWindowRect);
        void DrawMainWindowSection(UiComplexityMode complexity);
        bool IsLinkPickerOpen { get; }
        void CloseLinkPickerForModeChange();
    }

    // The Mission Log and the Route History: two instances of the same log-table window.
    internal interface IStructureLogWindow
    {
        void DrawIfOpen(Rect mainWindowRect);
        void ReleaseInputLock();
        void OpenForMission(string treeId, string missionIdOrNull, string displayTitle);
        void OpenForRoute(string routeId, string displayTitle);
    }

    // The window set as ParsekUI receives it from its constructor's build callback.
    internal interface IParsekUIWindows
    {
        IRecordingsTableWindow RecordingsTable { get; }
        ISpawnControlWindow SpawnControl { get; }
        ITimelineWindow Timeline { get; }
        IKerbalsWindow Kerbals { get; }
        ITestRunnerWindow TestRunner { get; }
        ISettingsWindow Settings { get; }
        ILogisticsWindow Logistics { get; }
        IStructureLogWindow StructureList { get; }
        IStructureLogWindow RouteHistory { get; }
    }
}
