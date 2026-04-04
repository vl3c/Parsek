using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static helper for drawing ghost vessel map markers via OnGUI.
    /// Extracts the rendering primitive from ParsekUI so it can be shared
    /// between flight scene (ParsekUI.DrawMapMarkers) and tracking station
    /// (ParsekTrackingStation.OnGUI). No dependency on ParsekFlight or
    /// any scene-specific state.
    ///
    /// Uses the same KSP atlas icons and projection math as ParsekUI:
    /// worldPos → ScaledSpace → PlanetariumCamera → screen coordinates.
    /// </summary>
    internal static class MapMarkerRenderer
    {
        private static Texture2D vesselIconAtlas;
        private static Dictionary<VesselType, Rect> vesselIconUVs;
        private static Texture2D fallbackDiamond;
        private static GUIStyle labelStyle;

        internal static void DrawMarker(Vector3d worldPos, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            EnsureResources();

            Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
            Vector3 screenPos = PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos);

            if (screenPos.z < 0) return; // behind camera

            float x = screenPos.x;
            float y = Screen.height - screenPos.y; // GUI Y is inverted

            Color prevColor = GUI.color;
            int iconSize = 20;
            Rect uvRect;
            if (vesselIconAtlas != null && vesselIconUVs != null
                && vesselIconUVs.TryGetValue(vtype, out uvRect))
            {
                GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(
                    new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize),
                    vesselIconAtlas, uvRect);
            }
            else if (fallbackDiamond != null)
            {
                GUI.color = color;
                GUI.DrawTexture(
                    new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize),
                    fallbackDiamond);
            }
            GUI.color = prevColor;

            labelStyle.normal.textColor = color;
            GUI.Label(new Rect(x - 75, y + iconSize / 2 + 2, 150, 20), "Ghost: " + label, labelStyle);
        }

        private static void EnsureResources()
        {
            if (vesselIconAtlas == null && MapView.fetch != null)
                InitVesselTypeIcons();

            if (fallbackDiamond == null)
            {
                int size = 16;
                fallbackDiamond = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float halfDiag = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                    for (int px = 0; px < size; px++)
                    {
                        float manhattan = Mathf.Abs(px - center + 0.5f) + Mathf.Abs(py - center + 0.5f);
                        fallbackDiamond.SetPixel(px, py, manhattan <= halfDiag ? Color.white : Color.clear);
                    }
                fallbackDiamond.Apply();
            }

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label);
                labelStyle.fontSize = 11;
                labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.UpperCenter;
            }
        }

        private static void InitVesselTypeIcons()
        {
            vesselIconAtlas = MapView.OrbitIconsMap;
            if (vesselIconAtlas == null) return;

            var prefab = MapView.UINodePrefab;
            if (prefab == null) return;

            var fi = typeof(MapNode).GetField("iconSprites",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null)
            {
                vesselIconAtlas = null;
                return;
            }

            var sprites = fi.GetValue(prefab) as Sprite[];
            if (sprites == null || sprites.Length == 0)
            {
                vesselIconAtlas = null;
                return;
            }

            vesselIconUVs = new Dictionary<VesselType, Rect>();
            var vtypes = new[] {
                VesselType.Ship, VesselType.Plane, VesselType.Probe,
                VesselType.Relay, VesselType.Rover, VesselType.Lander,
                VesselType.Station, VesselType.Base, VesselType.EVA,
                VesselType.Flag, VesselType.Debris, VesselType.SpaceObject,
                VesselType.DeployedScienceController, VesselType.DeployedSciencePart
            };

            for (int i = 0; i < vtypes.Length && i < sprites.Length; i++)
            {
                Sprite s = sprites[i];
                if (s == null || s.texture != vesselIconAtlas) continue;
                Rect r = s.textureRect;
                vesselIconUVs[vtypes[i]] = new Rect(
                    r.x / vesselIconAtlas.width, r.y / vesselIconAtlas.height,
                    r.width / vesselIconAtlas.width, r.height / vesselIconAtlas.height);
            }
        }
    }
}
