using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace Parsek.Tests
{
    internal static class TestBodyRegistry
    {
        private static readonly FieldInfo FlightGlobalsBodiesField =
            typeof(FlightGlobals).GetField("bodies", BindingFlags.Static | BindingFlags.NonPublic);

        private static Dictionary<string, CelestialBody> installedBodiesByName;
        private static List<CelestialBody> installedBodiesInOrder;
        private static object originalFlightGlobalsBodies;

        internal static void Reset()
        {
            FlightGlobalsBodiesField?.SetValue(null, originalFlightGlobalsBodies);
            originalFlightGlobalsBodies = null;
            installedBodiesByName = null;
            installedBodiesInOrder = null;
        }

        internal static void Install(params (string name, double radius, double gravParameter)[] bodySpecs)
        {
            if (originalFlightGlobalsBodies == null)
                originalFlightGlobalsBodies = FlightGlobalsBodiesField?.GetValue(null);

            var bodies = new List<CelestialBody>();
            installedBodiesByName = new Dictionary<string, CelestialBody>(System.StringComparer.Ordinal);
            installedBodiesInOrder = bodies;

            foreach (var spec in bodySpecs)
            {
                CelestialBody body = CreateBody(spec.name, spec.radius, spec.gravParameter);
                bodies.Add(body);
                installedBodiesByName[spec.name] = body;
            }

            FlightGlobalsBodiesField?.SetValue(null, bodies);
        }

        internal static CelestialBody CreateBody(
            string bodyName,
            double radius = 0.0,
            double gravParameter = 0.0)
        {
            var body = (CelestialBody)FormatterServices.GetUninitializedObject(typeof(CelestialBody));
            typeof(CelestialBody).GetField("bodyName")?.SetValue(body, bodyName);
            typeof(CelestialBody).GetField("Radius")?.SetValue(body, radius);
            typeof(CelestialBody).GetField("gravParameter")?.SetValue(body, gravParameter);
            return body;
        }

        internal static bool ResolveBodyNameByIndex(int index, out string name)
        {
            name = null;
            if (installedBodiesInOrder == null || index < 0 || index >= installedBodiesInOrder.Count)
                return false;

            CelestialBody body = installedBodiesInOrder[index];
            name = body?.bodyName;
            return !string.IsNullOrEmpty(name);
        }

        internal static bool ResolveBodyByName(string bodyName, out CelestialBody body)
        {
            if (installedBodiesByName != null
                && !string.IsNullOrEmpty(bodyName)
                && installedBodiesByName.TryGetValue(bodyName, out body))
            {
                return true;
            }

            body = null;
            return false;
        }

        internal static bool ResolveBodyIndex(CelestialBody body, out int index)
        {
            index = -1;
            if (object.ReferenceEquals(body, null) || installedBodiesInOrder == null)
                return false;

            for (int i = 0; i < installedBodiesInOrder.Count; i++)
            {
                if (object.ReferenceEquals(installedBodiesInOrder[i], body))
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }
    }
}
