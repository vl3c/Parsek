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
    }
}
