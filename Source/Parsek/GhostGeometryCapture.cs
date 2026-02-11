using System;
using System.IO;
using UnityEngine;

namespace Parsek
{
    internal static class GhostGeometryCapture
    {
        /// <summary>
        /// Phase 1 plumbing capture. Writes a save-scoped stub geometry file so recording
        /// metadata and migration paths are exercised before full geometry serialization.
        /// </summary>
        internal static void CaptureStub(RecordingStore.Recording rec, Vessel vesselAtCapture)
        {
            if (rec == null) return;

            rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            rec.GhostGeometryVersion = RecordingStore.CurrentGhostGeometryVersion;
            rec.GhostGeometryRelativePath = RecordingPaths.BuildGhostGeometryRelativePath(rec.RecordingId);
            rec.GhostGeometryAvailable = false;
            rec.GhostGeometryCaptureError = "stub_not_implemented";
            rec.GhostGeometryCaptureStrategy = "live_hierarchy_probe_v1";

            int partCount = 0;
            int missingTransforms = 0;
            bool vesselLoaded = vesselAtCapture != null && vesselAtCapture.loaded;
            if (vesselAtCapture != null && vesselAtCapture.parts != null)
            {
                partCount = vesselAtCapture.parts.Count;
                for (int i = 0; i < vesselAtCapture.parts.Count; i++)
                {
                    var part = vesselAtCapture.parts[i];
                    if (part == null || part.partTransform == null)
                        missingTransforms++;
                }
            }
            rec.GhostGeometryProbeStatus = DetermineProbeStatus(vesselLoaded, partCount, missingTransforms);

            try
            {
                string absolutePath = RecordingPaths.ResolveSaveScopedPath(rec.GhostGeometryRelativePath);
                if (string.IsNullOrEmpty(absolutePath))
                {
                    rec.GhostGeometryCaptureError = "no_save_context";
                    return;
                }
                string dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var node = new ConfigNode("GHOST_GEOMETRY");
                node.AddValue("recordingId", rec.RecordingId);
                node.AddValue("version", rec.GhostGeometryVersion);
                node.AddValue("status", "stub_not_implemented");
                node.AddValue("strategy", rec.GhostGeometryCaptureStrategy);
                node.AddValue("probeStatus", rec.GhostGeometryProbeStatus);
                node.AddValue("partCount", partCount);
                node.AddValue("missingTransforms", missingTransforms);
                node.AddValue("vesselLoaded", vesselLoaded);
                node.AddValue("capturedUT", Planetarium.GetUniversalTime().ToString("R"));
                node.AddValue("vesselName", vesselAtCapture != null ? vesselAtCapture.vesselName : rec.VesselName);
                node.Save(absolutePath);
            }
            catch (Exception ex)
            {
                rec.GhostGeometryCaptureError = $"stub_write_failed:{ex.GetType().Name}";
                ParsekLog.Log($"Ghost geometry stub write failed for '{rec.VesselName}': {ex.Message}");
            }
        }

        internal static string DetermineProbeStatus(bool vesselLoaded, int partCount, int missingTransforms)
        {
            if (!vesselLoaded) return "vessel_not_loaded";
            if (partCount <= 0) return "no_parts";
            if (missingTransforms <= 0) return "ready_for_hierarchy_clone";
            if (missingTransforms < partCount) return "partial_missing_transforms";
            return "missing_transforms";
        }
    }
}
