using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>The kind of one Profile window row.</summary>
    internal enum MissionProfileRowKind
    {
        Summary = 0,
        SummaryFact,
        Line,
        Phase,
        Event,
        Empty,
    }

    /// <summary>
    /// One row of the Profile window (docs/dev/design-mission-profile.md sections 2.3, 2.4):
    /// its tree depth and collapse key, the seven cells as finished strings, the one-line hover
    /// sentence, and the flags the window colours and marks by. Pure data: the window draws it
    /// and never formats a number itself.
    /// </summary>
    internal sealed class MissionProfileRow
    {
        public MissionProfileRowKind Kind;
        public int Depth;
        // Stable across a rebuild while the element exists (the window keys collapse state on it).
        public string Key;
        // The key of the row whose caret hides this one; null at the top level.
        public string ParentKey;
        public bool HasCaret;
        public string LineId;
        public double UT = double.NaN;

        public ProfilePhaseKind? PhaseKind;
        // Where a phase row is flown (surface, atmosphere, space, kerbal); null on other rows.
        public ProfilePhaseClass? PhaseClass;
        public ProfileEventKind? EventKind;
        public ProfileBurnKind? BurnKind;

        // The cells of section 2.3.
        public string Time = "";
        public string Element = "";
        public string Location = "";
        public string Duration = "";
        public string Spent = "";
        public string Left = "";
        public string Result = "";

        // The hover strip's sentence (section 2.4).
        public string Hover = "";

        // Spent is the `~` estimate of section 4.6 (drawn in the same orange).
        public bool SpentIsEstimate;
        // Spent holds a figure (recorded or estimated): the window draws it in the house orange.
        public bool HasSpent;
        // A fact behind the row is inferred (a held altitude), not stored.
        public bool IsInferred;
        // The include set clipped this span (or dropped part of the line).
        public bool IsClipped;
        // The row is the Re-Fly bridge, or a line that was flown again.
        public bool IsReflown;
        // A number of the row is unknown (section 4.7); the hover says why.
        public bool HasDataGap;
        // A phase no rule could name (In space).
        public bool IsUnclassified;
        // A dock (or its undock) another mission recorded onto this mission's vessel: named
        // from the other tree, never expanded (section 3.2).
        public bool IsOtherMission;
        // The row's instant is after its line's end: this mission's recording had stopped, so
        // nothing of it was recorded here.
        public bool IsAfterLineEnd;
    }

    /// <summary>Career results tagged to the mission's recordings, read by the UI through ELS.</summary>
    internal struct MissionProfileCareerTotals
    {
        public bool Known;
        public double Science;
        public double Funds;
        public double Reputation;
    }

    /// <summary>
    /// The built profile of one mission: the window's rows (Summary first), the lines and their
    /// plans behind them, and the counts the build logged.
    /// </summary>
    internal sealed class MissionProfile
    {
        public string MissionId;
        public string TreeId;
        public string MissionName;
        public string Title;
        public readonly List<MissionProfileRow> Rows = new List<MissionProfileRow>();
        public readonly List<MissionProfileLine> Lines = new List<MissionProfileLine>();
        public readonly List<string> SummaryFacts = new List<string>();
        public bool IsEmpty;
        public double StartUT = double.NaN;
        public double EndUT = double.NaN;

        public int LineCount;
        public int Phases;
        public int Events;
        public int Burns;
        public int DvEstimated;
        public int DvRecorded;
        public int Inferred;
        public int OrbitUnknown;
        public int Unclassified;
        public int Reflown;
        public int ClippedByInclude;
        public int Gaps;
        // Docked by another mission events placed on the lines (docks and undocks).
        public int OtherMissionEvents;
        public double BuildMilliseconds;
    }
}
