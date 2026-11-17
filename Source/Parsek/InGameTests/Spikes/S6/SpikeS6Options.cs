using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6.
    //
    // The env switches of the S6 spike. Everything is inert unless PARSEK_SPIKE names S6; these
    // only shape what the armed spike does. PARSEK_SPIKE_S6_AUTO=1 drives the main menu steps
    // (clone, keyboard navigation, dialog, Difficulty panel, Create, resume) without a hand.

    internal enum SpikeS6DiffChoice { Edit, Accept, Cancel }

    internal enum SpikeS6NavAction { Down, Accept, GiveUp }

    internal sealed class SpikeS6Options
    {
        internal const string EnvAuto = "PARSEK_SPIKE_S6_AUTO";
        internal const string EnvDiff = "PARSEK_SPIKE_S6_DIFF";
        internal const string EnvContent = "PARSEK_SPIKE_S6_CONTENT";
        internal const string EnvLabel = "PARSEK_SPIKE_S6_LABEL";
        internal const string EnvSave = "PARSEK_SPIKE_S6_SAVE";
        internal const string EnvFolder = "PARSEK_SPIKE_S6_FOLDER";
        internal const string EnvMode = "PARSEK_SPIKE_S6_MODE";
        internal const string EnvResume = "PARSEK_SPIKE_S6_RESUME";
        internal const string EnvQuit = "PARSEK_SPIKE_S6_QUIT";
        internal const string EnvSnapshots = "PARSEK_SPIKE_S6_SNAPSHOTS";

        internal const string DefaultSave = "spike-s6-created";
        internal const int DefaultFolderLength = 200;
        internal const int NavMaxSteps = 12;

        internal bool Auto;
        internal SpikeS6DiffChoice Diff = SpikeS6DiffChoice.Edit;
        internal bool Content;
        internal string Label = "run";
        internal string Save = DefaultSave;
        internal string Folder = DefaultFolder(DefaultFolderLength);
        internal string Mode = "CAREER";
        internal bool Resume = true;
        internal bool Quit;
        internal bool Snapshots = true;
        internal readonly List<string> Notes = new List<string>();

        internal static SpikeS6Options Parse(Func<string, string> env)
        {
            var o = new SpikeS6Options();
            if (env == null) return o;
            o.Auto = IsOn(env(EnvAuto));
            o.Content = IsOn(env(EnvContent));
            o.Quit = IsOn(env(EnvQuit));
            string resume = env(EnvResume);
            if (!string.IsNullOrEmpty(resume)) o.Resume = IsOn(resume);
            string snaps = env(EnvSnapshots);
            if (!string.IsNullOrEmpty(snaps)) o.Snapshots = IsOn(snaps);

            string diff = (env(EnvDiff) ?? "").Trim().ToLowerInvariant();
            if (diff == "accept") o.Diff = SpikeS6DiffChoice.Accept;
            else if (diff == "cancel") o.Diff = SpikeS6DiffChoice.Cancel;
            else if (diff == "edit" || diff.Length == 0) o.Diff = SpikeS6DiffChoice.Edit;
            else o.Notes.Add("diff '" + diff + "' unknown, using edit");

            string label = SanitizeName(env(EnvLabel));
            if (label.Length > 0) o.Label = label;
            string save = SanitizeName(env(EnvSave));
            if (save.Length > 0) o.Save = save;
            string folder = env(EnvFolder);
            if (!string.IsNullOrEmpty(folder)) o.Folder = folder;

            string mode = (env(EnvMode) ?? "").Trim().ToUpperInvariant();
            if (mode == "CAREER" || mode == "SCIENCE_SANDBOX" || mode == "SANDBOX") o.Mode = mode;
            else if (mode == "SCIENCE") o.Mode = "SCIENCE_SANDBOX";
            else if (mode.Length > 0) o.Notes.Add("mode '" + mode + "' unknown, using CAREER");
            return o;
        }

        internal static bool IsOn(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        /// <summary>Letters, digits, '-' and '_' only (a save folder name and a file label).</summary>
        internal static string SanitizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value.Trim())
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                    sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// A Windows-shaped folder path of exactly <paramref name="length"/> characters (the
        /// pasted-path check): "C:\Races\" then numbered segments, padded with 'x'.
        /// </summary>
        internal static string DefaultFolder(int length)
        {
            if (length <= 0) return "";
            var sb = new StringBuilder("C:\\Races\\");
            int n = 1;
            while (sb.Length < length)
            {
                sb.Append("spike-s6-segment-").Append(n.ToString("D2", CultureInfo.InvariantCulture)).Append('\\');
                n++;
            }
            if (sb.Length > length) sb.Length = length;
            if (sb[sb.Length - 1] == '\\') sb[sb.Length - 1] = 'x';
            return sb.ToString();
        }

        /// <summary>The wanted name, or the first "name-N" (N from 2) not in use.</summary>
        internal static string PickSaveName(string wanted, ICollection<string> existing)
        {
            string baseName = SanitizeName(wanted);
            if (baseName.Length == 0) baseName = DefaultSave;
            if (existing == null || !ContainsIgnoreCase(existing, baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + "-" + i.ToString(CultureInfo.InvariantCulture);
                if (!ContainsIgnoreCase(existing, candidate)) return candidate;
            }
            return baseName + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private static bool ContainsIgnoreCase(ICollection<string> set, string name)
        {
            foreach (string s in set)
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// One step of the keyboard drive: Accept once the clone is the hovered entry, Down
        /// while steps remain, else give up (the HAND step then covers the arrows).
        /// </summary>
        internal static SpikeS6NavAction NextNav(bool cloneHovered, int stepsTaken, int maxSteps)
        {
            if (cloneHovered) return SpikeS6NavAction.Accept;
            if (stepsTaken >= maxSteps) return SpikeS6NavAction.GiveUp;
            return SpikeS6NavAction.Down;
        }

        internal string Describe()
        {
            return "auto=" + Auto + " diff=" + Diff + " content=" + Content + " label=" + Label
                + " save=" + Save + " folderLen=" + Folder.Length.ToString(CultureInfo.InvariantCulture)
                + " mode=" + Mode + " resume=" + Resume + " quit=" + Quit + " snapshots=" + Snapshots
                + (Notes.Count > 0 ? " notes=[" + string.Join("; ", Notes.ToArray()) + "]" : "");
        }
    }
}
