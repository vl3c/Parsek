using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    internal struct KerbalSlotLoadSummary
    {
        public bool HasData;
        public bool LoadedFromLegacyCrewReplacements;
        public int SlotsLoaded;
        public int ChainEntriesLoaded;
        public int IgnoredEntries;
    }

    internal sealed class KerbalLoadRepairSummary
    {
        private const int MaxSamples = 3;
        private readonly List<string> samples = new List<string>();

        public bool HasSlotData;
        public bool LoadedFromLegacyCrewReplacements;
        public int SlotsLoaded;
        public int ChainEntriesLoaded;
        public int IgnoredSlotEntries;
        public int ChainExtensionsAdded;
        public int RepairedRecordings;
        public int OldRows;
        public int NewRows;
        public int RemappedRows;
        public int EndStateRewrites;
        public int TouristRowsSkipped;
        public int RetiredStandInsRecreated;
        public int RetiredStandInsKept;
        public int UnusedStandInsDeleted;

        public bool HasInterestingChanges
        {
            get
            {
                return LoadedFromLegacyCrewReplacements
                    || IgnoredSlotEntries > 0
                    || ChainExtensionsAdded > 0
                    || RepairedRecordings > 0
                    || RemappedRows > 0
                    || EndStateRewrites > 0
                    || TouristRowsSkipped > 0
                    || RetiredStandInsRecreated > 0
                    || UnusedStandInsDeleted > 0;
            }
        }

        public bool HasSamples => samples.Count > 0;

        public void AddSample(string sample)
        {
            if (string.IsNullOrEmpty(sample) || samples.Count >= MaxSamples || samples.Contains(sample))
                return;

            samples.Add(sample);
        }

        public string FormatSummaryLine()
        {
            var sb = new StringBuilder();
            sb.Append("repair summary: ");
            sb.Append("slotSource=");
            if (!HasSlotData)
                sb.Append("none");
            else
                sb.Append(LoadedFromLegacyCrewReplacements ? "legacy-creplacements" : "KERBAL_SLOTS");
            sb.Append(" slots=");
            sb.Append(SlotsLoaded.ToString(CultureInfo.InvariantCulture));
            sb.Append(" chainEntries=");
            sb.Append(ChainEntriesLoaded.ToString(CultureInfo.InvariantCulture));
            sb.Append(" ignored=");
            sb.Append(IgnoredSlotEntries.ToString(CultureInfo.InvariantCulture));
            sb.Append(" chainExtensions=");
            sb.Append(ChainExtensionsAdded.ToString(CultureInfo.InvariantCulture));
            sb.Append(" repairedRecordings=");
            sb.Append(RepairedRecordings.ToString(CultureInfo.InvariantCulture));
            sb.Append(" oldRows=");
            sb.Append(OldRows.ToString(CultureInfo.InvariantCulture));
            sb.Append(" newRows=");
            sb.Append(NewRows.ToString(CultureInfo.InvariantCulture));
            sb.Append(" remappedRows=");
            sb.Append(RemappedRows.ToString(CultureInfo.InvariantCulture));
            sb.Append(" endStateRewrites=");
            sb.Append(EndStateRewrites.ToString(CultureInfo.InvariantCulture));
            sb.Append(" touristRowsSkipped=");
            sb.Append(TouristRowsSkipped.ToString(CultureInfo.InvariantCulture));
            sb.Append(" retiredRecreated=");
            sb.Append(RetiredStandInsRecreated.ToString(CultureInfo.InvariantCulture));
            sb.Append(" retiredKept=");
            sb.Append(RetiredStandInsKept.ToString(CultureInfo.InvariantCulture));
            sb.Append(" deletedUnused=");
            sb.Append(UnusedStandInsDeleted.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public string FormatSamplesLine()
        {
            return "repair samples: " + string.Join("; ", samples.ToArray());
        }
    }

    internal static class KerbalLoadRepairDiagnostics
    {
        private const string Tag = "KerbalLoad";
        private static KerbalLoadRepairSummary current;

        internal static bool IsActive => current != null;
        internal static KerbalLoadRepairSummary CurrentForTesting => current;

        internal static void Begin()
        {
            current = new KerbalLoadRepairSummary();
        }

        internal static void Reset()
        {
            current = null;
        }

        internal static void RecordSlotLoad(KerbalSlotLoadSummary summary)
        {
            if (current == null || !summary.HasData)
                return;

            current.HasSlotData = true;
            current.LoadedFromLegacyCrewReplacements = summary.LoadedFromLegacyCrewReplacements;
            current.SlotsLoaded = summary.SlotsLoaded;
            current.ChainEntriesLoaded = summary.ChainEntriesLoaded;
            current.IgnoredSlotEntries += summary.IgnoredEntries;
        }

        internal static void RecordChainExtension(string ownerName, int depth)
        {
            if (current == null)
                return;

            current.ChainExtensionsAdded++;
            current.AddSample(
                "chain " + ownerName + " depth=" + depth.ToString(CultureInfo.InvariantCulture));
        }

        internal static void RecordMigrationRepair(int repairedRecordings, int oldRows, int newRows)
        {
            if (current == null || repairedRecordings <= 0)
                return;

            current.RepairedRecordings += repairedRecordings;
            current.OldRows += oldRows;
            current.NewRows += newRows;
        }

        internal static void RecordRemappedRow(string recordingId, string fromName, string toName)
        {
            if (current == null)
                return;

            current.RemappedRows++;
            current.AddSample("remap " + recordingId + " " + fromName + "->" + toName);
        }

        internal static void RecordEndStateRewrite(string recordingId, string kerbalName,
            KerbalEndState fromState, KerbalEndState toState)
        {
            if (current == null)
                return;

            current.EndStateRewrites++;
            current.AddSample(
                "end-state " + recordingId + " " + kerbalName + " " + fromState + "->" + toState);
        }

        internal static void RecordTouristRowsSkipped(string recordingId, int count)
        {
            if (current == null || count <= 0)
                return;

            current.TouristRowsSkipped += count;
            current.AddSample(
                "tourist-skip " + recordingId + " count=" + count.ToString(CultureInfo.InvariantCulture));
        }

        internal static void RecordRetiredStandInRecreated(string standIn)
        {
            if (current == null || string.IsNullOrEmpty(standIn))
                return;

            current.RetiredStandInsRecreated++;
            current.AddSample("retired-recreated " + standIn);
        }

        internal static void RecordRetiredStandInKept(string standIn)
        {
            if (current == null || string.IsNullOrEmpty(standIn))
                return;

            current.RetiredStandInsKept++;
        }

        internal static void RecordUnusedStandInDeleted(string standIn)
        {
            if (current == null || string.IsNullOrEmpty(standIn))
                return;

            current.UnusedStandInsDeleted++;
            current.AddSample("deleted-unused " + standIn);
        }

        internal static void EmitAndReset()
        {
            if (current != null && current.HasInterestingChanges)
            {
                ParsekLog.Info(Tag, current.FormatSummaryLine());
                if (current.HasSamples)
                    ParsekLog.Info(Tag, current.FormatSamplesLine());
            }

            current = null;
        }
    }
}
