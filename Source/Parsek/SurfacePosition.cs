using System;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public enum SurfaceSituation
    {
        Landed   = 0,
        Splashed = 1
    }

    public struct SurfacePosition
    {
        public string body;
        public double latitude;
        public double longitude;
        public double altitude;
        public Quaternion rotation;
        public SurfaceSituation situation;

        public override string ToString()
        {
            return $"body={body ?? "?"} lat={latitude:F4} lon={longitude:F4} " +
                   $"alt={altitude:F1} sit={situation}";
        }

        public static void SaveInto(ConfigNode node, SurfacePosition pos)
        {
            var ic = CultureInfo.InvariantCulture;
            node.AddValue("body", pos.body ?? "");
            node.AddValue("lat", pos.latitude.ToString("R", ic));
            node.AddValue("lon", pos.longitude.ToString("R", ic));
            node.AddValue("alt", pos.altitude.ToString("R", ic));
            node.AddValue("rotX", pos.rotation.x.ToString("R", ic));
            node.AddValue("rotY", pos.rotation.y.ToString("R", ic));
            node.AddValue("rotZ", pos.rotation.z.ToString("R", ic));
            node.AddValue("rotW", pos.rotation.w.ToString("R", ic));
            node.AddValue("situation", ((int)pos.situation).ToString(ic));
        }

        public static SurfacePosition LoadFrom(ConfigNode node)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;
            var pos = new SurfacePosition();
            pos.body = node.GetValue("body") ?? "Kerbin";
            double.TryParse(node.GetValue("lat"), inv, ic, out pos.latitude);
            double.TryParse(node.GetValue("lon"), inv, ic, out pos.longitude);
            double.TryParse(node.GetValue("alt"), inv, ic, out pos.altitude);
            float rx = 0, ry = 0, rz = 0, rw = 1;  // default to identity quaternion
            float.TryParse(node.GetValue("rotX"), inv, ic, out rx);
            float.TryParse(node.GetValue("rotY"), inv, ic, out ry);
            float.TryParse(node.GetValue("rotZ"), inv, ic, out rz);
            if (!float.TryParse(node.GetValue("rotW"), inv, ic, out rw))
                rw = 1;  // preserve identity if only rotW fails to parse
            pos.rotation = new Quaternion(rx, ry, rz, rw);
            int sitInt;
            if (int.TryParse(node.GetValue("situation"), NumberStyles.Integer, ic, out sitInt)
                && Enum.IsDefined(typeof(SurfaceSituation), sitInt))
                pos.situation = (SurfaceSituation)sitInt;
            return pos;
        }
    }
}
