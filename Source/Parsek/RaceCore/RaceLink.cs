using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The race mark (design 6.1): the identity and the choices of a race save that never change
    /// after the save is created, so every kind of load takes them from the loaded save as is.
    /// It lives in the node of its own scenario module (<see cref="RaceFormat.ScenarioModuleName"/>),
    /// never in the PARSEK node. The display name and the look are NOT here: they are cosmetic,
    /// may change, and live in player.cfg and local.cfg.
    /// </summary>
    internal sealed class RaceLink
    {
        internal readonly int RaceLinkFormat;
        internal readonly string RaceId;
        internal readonly string PlayerId;
        internal readonly int SiteSlot;
        internal readonly string SiteTableId;
        internal readonly string LadderId;
        internal readonly string DateUnit;
        internal readonly string Calendar;
        internal readonly bool AlternateSites;
        internal readonly string GameMode;
        internal readonly string DifficultyDigest;

        internal RaceLink(int raceLinkFormat, string raceId, string playerId, int siteSlot,
            string siteTableId, string ladderId, string dateUnit, string calendar,
            bool alternateSites, string gameMode, string difficultyDigest)
        {
            RaceLinkFormat = raceLinkFormat;
            RaceId = raceId;
            PlayerId = playerId;
            SiteSlot = siteSlot;
            SiteTableId = siteTableId;
            LadderId = ladderId;
            DateUnit = dateUnit;
            Calendar = calendar;
            AlternateSites = alternateSites;
            GameMode = gameMode;
            DifficultyDigest = difficultyDigest;
        }
    }

    /// <summary>
    /// Reads the race mark from its scenario node. Numbers and bools parse invariantly; keys this
    /// build does not know are ignored on read (the scenario keeps the whole node verbatim, so
    /// they survive a save). The writer lands with its first production caller (R1-10's
    /// before-start step).
    /// </summary>
    internal static class RaceLinkCodec
    {
        internal const string KeyRaceLinkFormat = "raceLinkFormat";
        internal const string KeyRaceId = "raceId";
        internal const string KeyPlayerId = "playerId";
        internal const string KeySiteSlot = "siteSlot";
        internal const string KeySiteTableId = "siteTableId";
        internal const string KeyLadderId = "ladderId";
        internal const string KeyDateUnit = "dateUnit";
        internal const string KeyCalendar = "calendar";
        internal const string KeyAlternateSites = "alternateSites";
        internal const string KeyGameMode = "gameMode";
        internal const string KeyDifficultyDigest = "difficultyDigest";

        /// <summary>Seats are always four (design 7.3): slots 0 to 3.</summary>
        internal const int MaxSiteSlot = 3;

        internal const string ReasonNoNode = "no-node";
        internal const string ReasonFormatNewer = "format-newer";
        internal const string ReasonFormatOlder = "format-older";
        internal const string MissingPrefix = "missing:";
        internal const string BadValuePrefix = "bad-value:";

        /// <summary>
        /// Pure: parses <paramref name="node"/> into a link. False with a reason when it does not
        /// read: <c>no-node</c>; <c>missing:&lt;key&gt;</c> for an absent or empty required key;
        /// <c>bad-value:&lt;key&gt;</c> for a number or bool that does not parse or a slot outside
        /// 0..3; <c>format-newer</c> / <c>format-older</c> for a <c>raceLinkFormat</c> this build
        /// does not read. The format is classified first, so a mark from a newer build reads
        /// <c>format-newer</c> whatever else it carries.
        /// </summary>
        internal static bool TryRead(ConfigNode node, out RaceLink link, out string reason)
        {
            link = null;
            if (node == null)
            {
                reason = ReasonNoNode;
                return false;
            }

            int format;
            if (!TryReadInt(node, KeyRaceLinkFormat, out format, out reason))
                return false;
            RaceFormatClass formatClass = RaceFormat.Classify(format, RaceFormat.LinkCurrent);
            if (formatClass == RaceFormatClass.Newer)
            {
                reason = ReasonFormatNewer;
                return false;
            }
            // No upgrade step exists while LinkCurrent equals the baseline; a bump adds its step
            // and reads an Upgradable mark through it here.
            if (formatClass != RaceFormatClass.Current)
            {
                reason = ReasonFormatOlder;
                return false;
            }

            string raceId, playerId, siteTableId, ladderId, dateUnit, calendar, gameMode, digest;
            int siteSlot;
            bool alternateSites;
            if (!TryReadString(node, KeyRaceId, out raceId, out reason)
                || !TryReadString(node, KeyPlayerId, out playerId, out reason)
                || !TryReadInt(node, KeySiteSlot, out siteSlot, out reason)
                || !TryReadString(node, KeySiteTableId, out siteTableId, out reason)
                || !TryReadString(node, KeyLadderId, out ladderId, out reason)
                || !TryReadString(node, KeyDateUnit, out dateUnit, out reason)
                || !TryReadString(node, KeyCalendar, out calendar, out reason)
                || !TryReadBool(node, KeyAlternateSites, out alternateSites, out reason)
                || !TryReadString(node, KeyGameMode, out gameMode, out reason)
                || !TryReadString(node, KeyDifficultyDigest, out digest, out reason))
                return false;
            if (siteSlot < 0 || siteSlot > MaxSiteSlot)
            {
                reason = BadValuePrefix + KeySiteSlot;
                return false;
            }

            link = new RaceLink(format, raceId, playerId, siteSlot, siteTableId, ladderId,
                dateUnit, calendar, alternateSites, gameMode, digest);
            reason = null;
            return true;
        }

        private static bool TryReadString(ConfigNode node, string key, out string value, out string reason)
        {
            value = node.GetValue(key);
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
            {
                value = null;
                reason = MissingPrefix + key;
                return false;
            }
            reason = null;
            return true;
        }

        private static bool TryReadInt(ConfigNode node, string key, out int value, out string reason)
        {
            value = 0;
            string text;
            if (!TryReadString(node, key, out text, out reason))
                return false;
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                reason = BadValuePrefix + key;
                return false;
            }
            return true;
        }

        private static bool TryReadBool(ConfigNode node, string key, out bool value, out string reason)
        {
            value = false;
            string text;
            if (!TryReadString(node, key, out text, out reason))
                return false;
            if (!bool.TryParse(text.Trim(), out value))
            {
                reason = BadValuePrefix + key;
                return false;
            }
            return true;
        }
    }
}
