using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal struct RestorePoint
    {
        public string Id;
        public double UT;
        public string SaveFileName;
        public string Label;
        public int RecordingCount;
        public double Funds;
        public double Science;
        public float Reputation;
        public double ReservedFundsAtSave;
        public double ReservedScienceAtSave;
        public float ReservedRepAtSave;
        public bool SaveFileExists;

        public RestorePoint(bool init)
        {
            Id = null;
            UT = 0;
            SaveFileName = null;
            Label = null;
            RecordingCount = 0;
            Funds = 0;
            Science = 0;
            Reputation = 0;
            ReservedFundsAtSave = 0;
            ReservedScienceAtSave = 0;
            ReservedRepAtSave = 0;
            SaveFileExists = true;
        }

        public void SerializeInto(ConfigNode parent)
        {
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode("RESTORE_POINT");
            node.AddValue("id", Id ?? "");
            node.AddValue("ut", UT.ToString("R", ic));
            node.AddValue("saveFile", SaveFileName ?? "");
            node.AddValue("label", Label ?? "");
            node.AddValue("recCount", RecordingCount.ToString(ic));
            node.AddValue("funds", Funds.ToString("R", ic));
            node.AddValue("science", Science.ToString("R", ic));
            node.AddValue("rep", Reputation.ToString("R", ic));
            node.AddValue("resFunds", ReservedFundsAtSave.ToString("R", ic));
            node.AddValue("resSci", ReservedScienceAtSave.ToString("R", ic));
            node.AddValue("resRep", ReservedRepAtSave.ToString("R", ic));
        }

        public static RestorePoint DeserializeFrom(ConfigNode node)
        {
            var ic = CultureInfo.InvariantCulture;
            var rp = new RestorePoint(true);

            rp.Id = node.GetValue("id") ?? "";
            double ut;
            if (double.TryParse(node.GetValue("ut"), NumberStyles.Float, ic, out ut))
                rp.UT = ut;
            rp.SaveFileName = node.GetValue("saveFile") ?? "";
            rp.Label = node.GetValue("label") ?? "";
            int recCount;
            if (int.TryParse(node.GetValue("recCount"), NumberStyles.Integer, ic, out recCount))
                rp.RecordingCount = recCount;
            double funds;
            if (double.TryParse(node.GetValue("funds"), NumberStyles.Float, ic, out funds))
                rp.Funds = funds;
            double science;
            if (double.TryParse(node.GetValue("science"), NumberStyles.Float, ic, out science))
                rp.Science = science;
            float rep;
            if (float.TryParse(node.GetValue("rep"), NumberStyles.Float, ic, out rep))
                rp.Reputation = rep;
            double resFunds;
            if (double.TryParse(node.GetValue("resFunds"), NumberStyles.Float, ic, out resFunds))
                rp.ReservedFundsAtSave = resFunds;
            double resSci;
            if (double.TryParse(node.GetValue("resSci"), NumberStyles.Float, ic, out resSci))
                rp.ReservedScienceAtSave = resSci;
            float resRep;
            if (float.TryParse(node.GetValue("resRep"), NumberStyles.Float, ic, out resRep))
                rp.ReservedRepAtSave = resRep;

            return rp;
        }
    }

    internal struct PendingLaunchSave
    {
        public string SaveFileName;
        public double UT;
        public double Funds;
        public double Science;
        public float Reputation;
        public double ReservedFundsAtSave;
        public double ReservedScienceAtSave;
        public float ReservedRepAtSave;
    }

    internal static class RestorePointStore
    {
        private static List<RestorePoint> restorePoints = new List<RestorePoint>();

        internal static PendingLaunchSave? pendingLaunchSave;
        internal static bool initialLoadDone;
        private static string lastSaveFolder;

        // Go-back flags (survive scene change via static fields)
        internal static bool IsGoingBack;
        internal static double GoBackUT;
        internal static ResourceBudget.BudgetSummary GoBackReserved;

        internal static bool SuppressLogging;

        internal static bool HasRestorePoints => restorePoints.Count > 0;
        internal static bool HasPendingLaunchSave => pendingLaunchSave.HasValue;
        internal static IReadOnlyList<RestorePoint> RestorePoints => restorePoints;

        internal static void ResetForTesting()
        {
            restorePoints.Clear();
            pendingLaunchSave = null;
            initialLoadDone = false;
            lastSaveFolder = null;
            IsGoingBack = false;
            GoBackUT = 0;
            GoBackReserved = default(ResourceBudget.BudgetSummary);
        }

        internal static string BuildLabel(string vesselName, int recordingCount, bool isTree)
        {
            string recWord = recordingCount == 1 ? "recording" : "recordings";
            string launchType = isTree ? "tree launch" : "launch";
            return $"\"{vesselName}\" {launchType} ({recordingCount} {recWord})";
        }

        internal static string RestorePointSaveName(string shortId)
        {
            return $"parsek_rp_{shortId}";
        }

        internal static void AddForTesting(RestorePoint rp)
        {
            restorePoints.Add(rp);
        }
    }
}
