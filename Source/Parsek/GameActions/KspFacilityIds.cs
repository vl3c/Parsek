namespace Parsek
{
    internal static class KspFacilityIds
    {
        private const string SpaceCenterPrefix = "SpaceCenter/";

        internal static string ToUpgradeableFacilityId(string displayFacilityId)
        {
            if (string.IsNullOrEmpty(displayFacilityId))
                return "";

            if (displayFacilityId.StartsWith(SpaceCenterPrefix, System.StringComparison.Ordinal))
                return displayFacilityId;

            return SpaceCenterPrefix + displayFacilityId;
        }

        internal static string ToDisplayFacilityId(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId))
                return "";

            if (!facilityId.StartsWith(SpaceCenterPrefix, System.StringComparison.Ordinal))
                return facilityId;

            string remainder = facilityId.Substring(SpaceCenterPrefix.Length);
            return remainder.IndexOf('/') < 0 ? remainder : facilityId;
        }
    }
}
