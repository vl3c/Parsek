using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Inside stock <c>Vessel.Unload()</c> the vessel still reads loaded and its part list is
    /// already empty. Three readers ran there on every flying vessel stock unloaded: the
    /// finalizer read it dead (the fall was extrapolated again from a recorded sample), the
    /// identity check read "no part survived" (a pod landed past 2.5 km was sealed Destroyed,
    /// EVA-4 `2026-11-15_0948`), and the snapshot capture replaced its protoVessel with one that
    /// has no parts.
    /// </summary>
    [Collection("Sequential")]
    public class VesselUnloadStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VesselUnloadStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Vessel Build(
            bool unloading, bool loaded, int parts, int protoParts, Vessel.State state = Vessel.State.INACTIVE)
        {
            var v = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            // Unity's == reads an object with no native pointer as null.
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cachedPtr);
            cachedPtr.SetValue(v, new IntPtr(1));
            v.loaded = loaded;
            v.state = state;
            v.parts = new List<Part>();
            for (int i = 0; i < parts; i++)
                v.parts.Add((Part)FormatterServices.GetUninitializedObject(typeof(Part)));
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.protoPartSnapshots = new List<ProtoPartSnapshot>();
            for (int i = 0; i < protoParts; i++)
            {
                var pps = (ProtoPartSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartSnapshot));
                pps.persistentId = (uint)(500 + i);
                pv.protoPartSnapshots.Add(pps);
            }
            v.protoVessel = pv;
            FieldInfo flag = typeof(Vessel).GetField(
                "isUnloading", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(flag);
            flag.SetValue(v, unloading);
            return v;
        }

        [Fact]
        public void IsMidUnload_OnlyInsideStocksUnloadWithTheSnapshotTaken()
        {
            Assert.True(VesselUnloadState.IsMidUnload(Build(unloading: true, loaded: true, parts: 0, protoParts: 7)));

            // The same empty shape outside an unload is a vessel whose last part just died.
            Assert.False(VesselUnloadState.IsMidUnload(Build(unloading: false, loaded: true, parts: 0, protoParts: 7)));
            // Unload started, parts still there (the flag is set first).
            Assert.False(VesselUnloadState.IsMidUnload(Build(unloading: true, loaded: true, parts: 7, protoParts: 7)));
            // Unloaded already; nothing in the snapshot; dead.
            Assert.False(VesselUnloadState.IsMidUnload(Build(unloading: true, loaded: false, parts: 0, protoParts: 7)));
            Assert.False(VesselUnloadState.IsMidUnload(Build(unloading: true, loaded: true, parts: 0, protoParts: 0)));
            Assert.False(VesselUnloadState.IsMidUnload(
                Build(unloading: true, loaded: true, parts: 0, protoParts: 7, state: Vessel.State.DEAD)));
            Assert.False(VesselUnloadState.IsMidUnload(null));
        }

        [Fact]
        public void IsLiveVesselAlive_MidUnload_IsAlive_AndATornDownLoadedVesselIsNot()
        {
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsLiveVesselAlive(
                Build(unloading: true, loaded: true, parts: 0, protoParts: 7)));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsLiveVesselAlive(
                Build(unloading: false, loaded: true, parts: 0, protoParts: 7)));
        }

        private static Recording Controlled(uint controllerPid)
        {
            return new Recording
            {
                RecordingId = "pod-rec",
                Controllers = new List<ControllerInfo>
                {
                    new ControllerInfo { type = "KerbalEVA", partName = "mk1pod", partPersistentId = controllerPid }
                }
            };
        }

        [Fact]
        public void IdentityLoss_ControllerInTheProtoVessel_MidUnload_IsKept()
        {
            Vessel v = Build(unloading: true, loaded: true, parts: 0, protoParts: 3);

            Assert.False(IdentityLossClassifier.IsRecordedIdentityLost(Controlled(501u), v));
            Assert.Contains(logLines, l => l.Contains("[Recording]") && l.Contains("IsRecordedIdentityLost")
                && l.Contains("midUnload=True") && l.Contains("partSource=Proto") && l.Contains("lost=False"));
        }

        [Fact]
        public void IdentityLoss_ControllerGoneBeforeTheUnload_IsStillLost_AndTheRemnantSurvives()
        {
            Vessel v = Build(unloading: true, loaded: true, parts: 0, protoParts: 3);

            Assert.True(IdentityLossClassifier.IsRecordedIdentityLost(Controlled(999u), v));
            Assert.True(IdentityLossClassifier.RemnantSurvives(v));
        }

        [Fact]
        public void IdentityLoss_LastPartJustDied_IsStillLost_AndNothingSurvives()
        {
            Vessel v = Build(unloading: false, loaded: true, parts: 0, protoParts: 3);

            Assert.True(IdentityLossClassifier.IsRecordedIdentityLost(Controlled(501u), v));
            Assert.False(IdentityLossClassifier.RemnantSurvives(v));
        }

        [Fact]
        public void TryBackupSnapshot_AsksForTheUnloadBeforeItCallsBackupVessel()
        {
            // BackupVessel() on a vessel that still reads loaded rebuilds its protoVessel from
            // the live parts; mid-unload there are none.
            string path = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "Source", "Parsek", "Recording", "FlightRecorder.LiveSnapshot.cs");
            string source = System.IO.File.ReadAllText(path);
            int body = source.IndexOf("public static ConfigNode TryBackupSnapshot(", StringComparison.Ordinal);
            Assert.True(body >= 0);
            int guard = source.IndexOf("VesselUnloadState.IsMidUnload(vessel)", body, StringComparison.Ordinal);
            int choice = source.IndexOf(
                "midUnload ? vessel.protoVessel : vessel.BackupVessel();", body, StringComparison.Ordinal);
            Assert.True(guard > body && choice > guard);
        }
    }
}
