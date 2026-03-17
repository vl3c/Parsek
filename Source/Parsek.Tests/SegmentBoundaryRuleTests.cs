using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SegmentBoundaryRuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SegmentBoundaryRuleTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region ClassifyJointBreakResult

        [Fact]
        public void ClassifyJointBreakResult_NoNewVessels_ReturnsWithinSegment()
        {
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint>(),
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.WithinSegment, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_NullVesselList_ReturnsWithinSegment()
        {
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: null,
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.WithinSegment, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_OnlyOriginalVesselInList_ReturnsWithinSegment()
        {
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 42,
                postBreakVesselPids: new List<uint> { 42 },
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.WithinSegment, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_NewControlledVessel_ReturnsStructuralSplit()
        {
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 1000, 2000 },
                anyNewVesselHasController: true);

            Assert.Equal(JointBreakResult.StructuralSplit, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_NewDebrisVessel_ReturnsDebrisSplit()
        {
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 1000, 2000 },
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.DebrisSplit, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_MultipleNewVessels_ControlledReturnsStructuralSplit()
        {
            // Multiple new vessels after break, at least one controlled
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 100,
                postBreakVesselPids: new List<uint> { 100, 200, 300 },
                anyNewVesselHasController: true);

            Assert.Equal(JointBreakResult.StructuralSplit, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_MultipleNewVessels_UncontrolledReturnsDebrisSplit()
        {
            // Multiple new vessels after break, none controlled
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 100,
                postBreakVesselPids: new List<uint> { 100, 200, 300 },
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.DebrisSplit, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_OnlyNewVessel_NoOriginal_ControlledReturnsStructuralSplit()
        {
            // Edge case: original vessel PID not in the post-break list (vessel was destroyed/consumed)
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 2000 },
                anyNewVesselHasController: true);

            Assert.Equal(JointBreakResult.StructuralSplit, result);
        }

        [Fact]
        public void ClassifyJointBreakResult_OnlyNewVessel_NoOriginal_UncontrolledReturnsDebrisSplit()
        {
            // Edge case: original vessel PID not in the post-break list, new vessel is debris
            var result = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 2000 },
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.DebrisSplit, result);
        }

        #endregion

        #region ClassifyJointBreakResult logging

        [Fact]
        public void ClassifyJointBreakResult_WithinSegment_LogsOriginalPid()
        {
            SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 5555,
                postBreakVesselPids: new List<uint>(),
                anyNewVesselHasController: false);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("5555") &&
                l.Contains("WithinSegment"));
        }

        [Fact]
        public void ClassifyJointBreakResult_StructuralSplit_LogsControlled()
        {
            SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 7777,
                postBreakVesselPids: new List<uint> { 7777, 8888 },
                anyNewVesselHasController: true);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("7777") &&
                l.Contains("StructuralSplit"));
        }

        [Fact]
        public void ClassifyJointBreakResult_DebrisSplit_LogsDebris()
        {
            SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 3333,
                postBreakVesselPids: new List<uint> { 3333, 4444 },
                anyNewVesselHasController: false);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("3333") &&
                l.Contains("DebrisSplit"));
        }

        #endregion

        #region EmitBreakageSegmentEvents

        [Fact]
        public void EmitBreakageSegmentEvents_NonControllerPart_EmitsPartDestroyedOnly()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 17050.0,
                destroyedPartName: "fuelTank",
                destroyedPartPid: 12345,
                wasController: false,
                controllerDetails: null);

            Assert.Single(events);
            Assert.Equal(SegmentEventType.PartDestroyed, events[0].type);
            Assert.Equal(17050.0, events[0].ut);
        }

        [Fact]
        public void EmitBreakageSegmentEvents_ControllerPart_EmitsBothEvents()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 17060.0,
                destroyedPartName: "probeCoreOcto",
                destroyedPartPid: 99999,
                wasController: true,
                controllerDetails: "lost=probeCoreOcto remaining=mk1pod");

            Assert.Equal(2, events.Count);
            Assert.Equal(SegmentEventType.PartDestroyed, events[0].type);
            Assert.Equal(SegmentEventType.ControllerChange, events[1].type);
            Assert.Equal(17060.0, events[0].ut);
            Assert.Equal(17060.0, events[1].ut);
        }

        [Fact]
        public void EmitBreakageSegmentEvents_PartDestroyed_DetailsContainPartNameAndPid()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 100.0,
                destroyedPartName: "solidBooster.v2",
                destroyedPartPid: 54321,
                wasController: false,
                controllerDetails: null);

            Assert.Single(events);
            Assert.Contains("solidBooster.v2", events[0].details);
            Assert.Contains("54321", events[0].details);
        }

        [Fact]
        public void EmitBreakageSegmentEvents_ControllerChange_DetailsContainProvidedInfo()
        {
            var events = new List<SegmentEvent>();
            string details = "lost=probeCoreHex remaining=mk1pod";

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 200.0,
                destroyedPartName: "probeCoreHex",
                destroyedPartPid: 11111,
                wasController: true,
                controllerDetails: details);

            var controllerEvent = events.First(e => e.type == SegmentEventType.ControllerChange);
            Assert.Equal(details, controllerEvent.details);
        }

        [Fact]
        public void EmitBreakageSegmentEvents_NullControllerDetails_DefaultsToEmptyString()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 300.0,
                destroyedPartName: "probeCoreOcto2",
                destroyedPartPid: 22222,
                wasController: true,
                controllerDetails: null);

            var controllerEvent = events.First(e => e.type == SegmentEventType.ControllerChange);
            Assert.Equal("", controllerEvent.details);
        }

        [Fact]
        public void EmitBreakageSegmentEvents_NullEventsList_DoesNotThrow()
        {
            // Should log a warning but not throw
            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                null,
                ut: 400.0,
                destroyedPartName: "noseCone",
                destroyedPartPid: 33333,
                wasController: false,
                controllerDetails: null);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Boundary]") &&
                l.Contains("null"));
        }

        [Fact]
        public void EmitBreakageSegmentEvents_AppendsToExistingEvents()
        {
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = 50.0, type = SegmentEventType.CrewTransfer, details = "existing" }
            };

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 100.0,
                destroyedPartName: "winglet",
                destroyedPartPid: 44444,
                wasController: false,
                controllerDetails: null);

            Assert.Equal(2, events.Count);
            Assert.Equal(SegmentEventType.CrewTransfer, events[0].type); // existing event untouched
            Assert.Equal(SegmentEventType.PartDestroyed, events[1].type); // appended
        }

        #endregion

        #region EmitBreakageSegmentEvents logging

        [Fact]
        public void EmitBreakageSegmentEvents_LogsPartNamePidAndControllerStatus()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 17100.0,
                destroyedPartName: "decoupler1-2",
                destroyedPartPid: 77777,
                wasController: false,
                controllerDetails: null);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("decoupler1-2") &&
                l.Contains("77777") &&
                l.Contains("wasController=False"));
        }

        [Fact]
        public void EmitBreakageSegmentEvents_ControllerPart_LogsControllerTrue()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 17200.0,
                destroyedPartName: "mk1pod",
                destroyedPartPid: 88888,
                wasController: true,
                controllerDetails: "lost=mk1pod");

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("mk1pod") &&
                l.Contains("88888") &&
                l.Contains("wasController=True"));
        }

        [Fact]
        public void EmitBreakageSegmentEvents_LogsPartDestroyedEmission()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 500.5,
                destroyedPartName: "solarPanel",
                destroyedPartPid: 66666,
                wasController: false,
                controllerDetails: null);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("Emitted PartDestroyed") &&
                l.Contains("solarPanel") &&
                l.Contains("66666"));
        }

        [Fact]
        public void EmitBreakageSegmentEvents_Controller_LogsControllerChangeEmission()
        {
            var events = new List<SegmentEvent>();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                events,
                ut: 600.0,
                destroyedPartName: "probeCoreSphere",
                destroyedPartPid: 55555,
                wasController: true,
                controllerDetails: "lost=probeCoreSphere remaining=mk1-3pod");

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("Emitted ControllerChange") &&
                l.Contains("probeCoreSphere") &&
                l.Contains("55555"));
        }

        #endregion

        #region IsControllerPart

        [Fact]
        public void IsControllerPart_WithModuleCommand_ReturnsTrue()
        {
            var modules = new List<string> { "ModuleReactionWheel", "ModuleCommand", "ModuleSAS" };

            bool result = SegmentBoundaryLogic.IsControllerPart("mk1pod", modules);

            Assert.True(result);
        }

        [Fact]
        public void IsControllerPart_WithoutModuleCommand_ReturnsFalse()
        {
            var modules = new List<string> { "ModuleReactionWheel", "ModuleSAS", "ModuleDecouple" };

            bool result = SegmentBoundaryLogic.IsControllerPart("decoupler1-2", modules);

            Assert.False(result);
        }

        [Fact]
        public void IsControllerPart_NullModuleList_ReturnsFalse()
        {
            bool result = SegmentBoundaryLogic.IsControllerPart("fuelTank", null);

            Assert.False(result);
        }

        [Fact]
        public void IsControllerPart_EmptyModuleList_ReturnsFalse()
        {
            bool result = SegmentBoundaryLogic.IsControllerPart("strut", new List<string>());

            Assert.False(result);
        }

        [Fact]
        public void IsControllerPart_OnlyModuleCommand_ReturnsTrue()
        {
            var modules = new List<string> { "ModuleCommand" };

            bool result = SegmentBoundaryLogic.IsControllerPart("probeCoreOcto", modules);

            Assert.True(result);
        }

        [Fact]
        public void IsControllerPart_CaseSensitive_ModulecommandReturnsFalse()
        {
            // KSP module names are case-sensitive; "moduleCommand" is not "ModuleCommand"
            var modules = new List<string> { "moduleCommand" };

            bool result = SegmentBoundaryLogic.IsControllerPart("badPod", modules);

            Assert.False(result);
        }

        [Fact]
        public void IsControllerPart_NullPartName_DoesNotThrow()
        {
            var modules = new List<string> { "ModuleCommand" };

            bool result = SegmentBoundaryLogic.IsControllerPart(null, modules);

            Assert.True(result);
        }

        #endregion

        #region IsControllerPart logging

        [Fact]
        public void IsControllerPart_NullModules_LogsVerbose()
        {
            ParsekLog.VerboseOverrideForTesting = true;

            SegmentBoundaryLogic.IsControllerPart("fuelTank", null);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("null module list") &&
                l.Contains("fuelTank"));
        }

        [Fact]
        public void IsControllerPart_WithModules_LogsModuleCount()
        {
            ParsekLog.VerboseOverrideForTesting = true;
            var modules = new List<string> { "ModuleCommand", "ModuleSAS" };

            SegmentBoundaryLogic.IsControllerPart("mk1pod", modules);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("moduleCount=2") &&
                l.Contains("hasModuleCommand=True"));
        }

        #endregion

        #region Integration: EmitBreakageSegmentEvents with Recording.SegmentEvents

        [Fact]
        public void EmitBreakageSegmentEvents_WorksWithRecordingSegmentEvents()
        {
            var recording = new Recording();

            SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                recording.SegmentEvents,
                ut: 17500.0,
                destroyedPartName: "radialDecoupler",
                destroyedPartPid: 98765,
                wasController: false,
                controllerDetails: null);

            Assert.Single(recording.SegmentEvents);
            Assert.Equal(SegmentEventType.PartDestroyed, recording.SegmentEvents[0].type);
            Assert.Contains("radialDecoupler", recording.SegmentEvents[0].details);
            Assert.Contains("98765", recording.SegmentEvents[0].details);
        }

        #endregion

        #region JointBreakResult enum values

        [Fact]
        public void JointBreakResult_WithinSegment_HasValue0()
        {
            Assert.Equal(0, (int)JointBreakResult.WithinSegment);
        }

        [Fact]
        public void JointBreakResult_StructuralSplit_HasValue1()
        {
            Assert.Equal(1, (int)JointBreakResult.StructuralSplit);
        }

        [Fact]
        public void JointBreakResult_DebrisSplit_HasValue2()
        {
            Assert.Equal(2, (int)JointBreakResult.DebrisSplit);
        }

        [Fact]
        public void JointBreakResult_HasExactlyThreeValues()
        {
            var values = Enum.GetValues(typeof(JointBreakResult));
            Assert.Equal(3, values.Length);
        }

        #endregion
    }
}
