using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="GhostPlaybackLogic.ApplySnapshotBaselines"/> — the M1 spawn
    /// application pass — and for the pure
    /// <see cref="GhostPlaybackLogic.ResolveSnapshotBaselineActions"/> in front of it.
    ///
    /// Two constraints shape these cells. No Unity Transform can be constructed in a unit
    /// test (same as <see cref="GhostSpawnDeployableBaselineTests"/>), and some appliers
    /// cannot even be CALLED here: <c>SetLightState</c> and the canopy appliers throw
    /// SecurityException ("ECall methods must be packaged into a system module") the moment
    /// the runtime touches them. So the light and parachute decisions are pinned on the
    /// pure resolver, while the pass itself is exercised over the families whose appliers
    /// are reachable (deployables with empty transform lists, robotics with null servo
    /// transforms) plus its summary log line.
    /// </summary>
    [Collection("Sequential")]
    public class SnapshotBaselineApplicationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SnapshotBaselineApplicationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            DiagnosticsState.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
        }

        private static RoboticGhostInfo Servo(
            uint pid, int moduleIndex, string moduleName,
            RoboticVisualMode mode = RoboticVisualMode.Rotational)
        {
            return new RoboticGhostInfo
            {
                partPersistentId = pid,
                moduleIndex = moduleIndex,
                moduleName = moduleName,
                visualMode = mode,
                // servoTransform stays null: ApplyRoboticPose no-ops on it, and the
                // scalar bookkeeping this test reads is set either way.
            };
        }

        private static GhostBuildResult ResultWithServos(params RoboticGhostInfo[] servos)
        {
            return new GhostBuildResult
            {
                roboticInfos = new List<RoboticGhostInfo>(servos),
            };
        }

        #region Null / empty inputs stay byte-identical to pre-M1

        [Fact]
        public void ApplySnapshotBaselines_NullState_DoesNotThrow()
        {
            GhostPlaybackLogic.ApplySnapshotBaselines(null);
        }

        [Fact]
        public void ApplySnapshotBaselines_NoBaselines_LogsNothing()
        {
            var state = new GhostPlaybackState { vesselName = "NoBaselineVessel" };

            GhostPlaybackLogic.ApplySnapshotBaselines(state);

            Assert.DoesNotContain(logLines, l => l.Contains("Snapshot baseline applied"));
        }

        [Fact]
        public void PopulateGhostInfoDictionaries_NullSnapshotBaselines_StowBaselineLogUnchanged()
        {
            // The pre-M1 spawn baseline must still run and still log exactly as before
            // when the snapshot carried nothing readable.
            var result = new GhostBuildResult
            {
                deployableInfos = new List<DeployableGhostInfo>
                {
                    new DeployableGhostInfo
                    {
                        partPersistentId = 100000,
                        transforms = new List<DeployableTransformState>(),
                    },
                },
            };
            var state = new GhostPlaybackState { vesselName = "LegacyVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            Assert.Contains(logLines, l => l.Contains("[GhostVisual]")
                && l.Contains("Spawn baseline: stowed 0/1 deployable(s)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Snapshot baseline applied"));
            Assert.Null(state.snapshotBaselines);
        }

        #endregion

        #region Ordering: the baseline runs after the stow baseline, before event replay

        [Fact]
        public void PopulateGhostInfoDictionaries_WithBaselines_AppliesAfterStowBaseline()
        {
            var result = new GhostBuildResult
            {
                deployableInfos = new List<DeployableGhostInfo>
                {
                    new DeployableGhostInfo
                    {
                        partPersistentId = 100000,
                        transforms = new List<DeployableTransformState>(),
                    },
                },
                snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
                {
                    { 100000, new SnapshotPartBaseline { deployableExtended = true } },
                },
            };
            var state = new GhostPlaybackState { vesselName = "PanelVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            int stowIndex = logLines.FindIndex(l => l.Contains("Spawn baseline: stowed"));
            int baselineIndex = logLines.FindIndex(l => l.Contains("Snapshot baseline applied"));
            Assert.True(stowIndex >= 0, "stow baseline log missing");
            Assert.True(baselineIndex >= 0, "snapshot baseline log missing");
            Assert.True(stowIndex < baselineIndex,
                "the snapshot baseline must layer OVER the all-stowed baseline, not under it");
            // Kept on the state so the loop-cycle restore can re-apply it.
            Assert.NotNull(state.snapshotBaselines);
            Assert.Single(state.snapshotBaselines);
        }

        #endregion

        #region Pure action resolution

        // The appliers for lights and parachutes reach into Unity components
        // (Behaviour.enabled, Transform poses), which cannot execute under xUnit — calling
        // them throws SecurityException: "ECall methods must be packaged into a system
        // module". The decisions in front of them are therefore resolved by the pure
        // ResolveSnapshotBaselineActions, and that is what these cells pin.

        [Fact]
        public void ResolveActions_NullBaseline_NoActions()
        {
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(null);

            Assert.False(actions.deployableTarget.HasValue);
            Assert.False(actions.lightPower.HasValue);
            Assert.False(actions.blinkEnabled.HasValue);
            Assert.False(actions.blinkRateHz.HasValue);
            Assert.Equal(GhostPlaybackLogic.SnapshotParachuteAction.None, actions.parachuteAction);
        }

        [Fact]
        public void ResolveActions_DeployStateWinsOverGearAndCargo()
        {
            // A part owns exactly one DeployableGhostInfo, so contradictory opinions must
            // resolve by documented precedence rather than by dictionary order.
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    deployableExtended = true,
                    gearDeployed = false,
                    cargoBayOpen = false,
                    animationGroupDeployed = false,
                    animateGenericDeployed = false,
                });

            Assert.True(actions.deployableTarget);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }

        [Fact]
        public void ResolveActions_GearWinsOverCargoAndAnimations()
        {
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    gearDeployed = true,
                    cargoBayOpen = false,
                    animateGenericDeployed = false,
                });

            Assert.True(actions.deployableTarget);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }

        [Fact]
        public void ResolveActions_CargoBayRoutesThroughTheCascade()
        {
            // Bays whose doors are jettison-style panels have no DeployableGhostInfo, so
            // the baseline must take the same deployable-then-jettison cascade the
            // CargoBayOpened event takes.
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    cargoBayOpen = true,
                });

            Assert.True(actions.deployableTarget);
            Assert.True(actions.deployableThroughCargoBayCascade);
        }

        [Fact]
        public void ResolveActions_AnimationGroupWinsOverStandaloneAnimation()
        {
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    animationGroupDeployed = true,
                    animateGenericDeployed = false,
                });

            Assert.True(actions.deployableTarget);
        }

        [Fact]
        public void ResolveActions_StandaloneAnimationUsedWhenItIsTheOnlyOpinion()
        {
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    animateGenericDeployed = true,
                });

            Assert.True(actions.deployableTarget);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }

        [Fact]
        public void ResolveActions_LightPowerFromModuleLight()
        {
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    lightOn = true,
                    lightBlinking = true,
                    lightBlinkRate = 2.5f,
                });

            Assert.True(actions.lightPower);
            Assert.True(actions.blinkEnabled);
            Assert.Equal(2.5f, actions.blinkRateHz);
        }

        [Fact]
        public void ResolveActions_ColorChangerStandsInForAMissingModuleLight()
        {
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    colorChangerOn = true,
                });

            Assert.True(actions.lightPower);
        }

        [Fact]
        public void ResolveActions_ModuleLightWinsOverColorChanger()
        {
            // The parser already nulls colorChangerOn when a ModuleLight exists; the
            // resolver's ?? order is the second line of defence for that same rule. A
            // ModuleLight saying OFF must not be overridden into ON by a ColorChanger —
            // and it resolves to "no action" rather than an explicit off, because off IS
            // the default state (see the all-false skip below).
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    lightOn = false,
                    colorChangerOn = true,
                });

            Assert.NotEqual(true, actions.lightPower);
        }

        [Fact]
        public void ResolveActions_ModuleLightOnWinsOverColorChangerOff()
        {
            // The same precedence in the direction that survives the all-false skip.
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    lightOn = true,
                    colorChangerOn = false,
                });

            Assert.True(actions.lightPower);
        }

        [Fact]
        public void ResolveActions_AllFalseLightBaseline_ResolvesToNoLightAction()
        {
            // An all-off light opinion is byte-identical to a fresh LightPlaybackState,
            // and applying it would materialise a dict entry that UpdateBlinkingLights
            // then walks on every frame of every such ghost for no visual effect.
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    lightOn = false,
                    lightBlinking = false,
                });

            Assert.False(actions.lightPower.HasValue);
            Assert.False(actions.blinkEnabled.HasValue);
            Assert.False(actions.blinkRateHz.HasValue);
        }

        [Fact]
        public void ResolveActions_LampOffButBlinkArmed_StillResolves()
        {
            // Only the all-false case is dropped: a blink opinion or an explicit rate is
            // real state a fresh LightPlaybackState does not carry, so the whole light
            // block still resolves (including the lamp-off half, which
            // ApplyLightPowerEvent needs in order to agree with the blink pass).
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    lightOn = false,
                    lightBlinking = true,
                    lightBlinkRate = 2f,
                });

            Assert.False(actions.lightPower);
            Assert.True(actions.blinkEnabled);
            Assert.Equal(2f, actions.blinkRateHz);
        }

        [Fact]
        public void ResolveActions_ColorChangerOffOnly_ResolvesToNoLightAction()
        {
            // ColorChanger materials are initialised to their OFF colour at build time, so
            // an animState=False opinion has nothing to do — and this is the population the
            // skip actually saves work on, since ColorChanger-only parts have no
            // pre-populated LightPlaybackState entry.
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(new SnapshotPartBaseline
                {
                    colorChangerOn = false,
                });

            Assert.False(actions.lightPower.HasValue);
        }

        // `expected` is an int rather than the enum itself: the enum is internal (it lives
        // inside GhostPlaybackLogic) and an internal type cannot appear in the signature
        // of the public method xUnit needs in order to discover the theory.
        [Theory]
        [InlineData("SEMIDEPLOYED", 1)]
        [InlineData("DEPLOYED", 2)]
        [InlineData("CUT", 3)]
        [InlineData("STOWED", 0)]
        [InlineData("ACTIVE", 0)]
        [InlineData("SOMETHING_ELSE", 0)]
        [InlineData(null, 0)]
        public void ClassifyParachuteAction_MapsPersistedStates(
            string persistentState, int expected)
        {
            Assert.Equal(
                (GhostPlaybackLogic.SnapshotParachuteAction)expected,
                GhostPlaybackLogic.ClassifySnapshotParachuteAction(persistentState));
        }

        #endregion

        #region Robotics

        [Fact]
        public void ApplySnapshotBaselines_ServoPose_BecomesSpawnValueAndCurrentValue()
        {
            GhostBuildResult result = ResultWithServos(
                Servo(100000, 0, "ModuleRoboticServoHinge"));
            result.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                {
                    100000,
                    new SnapshotPartBaseline
                    {
                        roboticPoses = new List<SnapshotRoboticPose>
                        {
                            new SnapshotRoboticPose
                            {
                                ordinal = 0,
                                moduleName = "ModuleRoboticServoHinge",
                                value = 42f,
                            },
                        },
                    }
                },
            };
            var state = new GhostPlaybackState { vesselName = "ArmVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.Equal(42f, info.spawnValue);
            Assert.Equal(42f, info.currentValue);
            Assert.True(info.hasSnapshotBaseline);
            // A posed (non-rotor) servo is parked, not travelling: the recording's own
            // events re-arm motion when they fire.
            Assert.False(info.active);
            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied")
                && l.Contains("servos=1") && l.Contains("servosSkipped=0"));
        }

        [Fact]
        public void ApplySnapshotBaselines_ServoOrdinalDrift_SkipsWithoutPosingWrongServo()
        {
            // The ghost's ordinal 0 is a piston, the snapshot's ordinal 0 was a hinge —
            // a different mod set shifted the robotic ordinals. Posing the piston with a
            // hinge angle would be worse than doing nothing.
            GhostBuildResult result = ResultWithServos(
                Servo(100000, 0, "ModuleRoboticServoPiston", RoboticVisualMode.Linear));
            result.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                {
                    100000,
                    new SnapshotPartBaseline
                    {
                        roboticPoses = new List<SnapshotRoboticPose>
                        {
                            new SnapshotRoboticPose
                            {
                                ordinal = 0,
                                moduleName = "ModuleRoboticServoHinge",
                                value = 90f,
                            },
                        },
                    }
                },
            };
            var state = new GhostPlaybackState { vesselName = "DriftVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.Equal(0f, info.spawnValue);
            Assert.False(info.hasSnapshotBaseline);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]")
                && l.Contains("servo ordinal 0 on pid=100000")
                && l.Contains("mod-set drift"));
            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied")
                && l.Contains("servos=0") && l.Contains("servosSkipped=1"));
        }

        [Fact]
        public void ApplySnapshotBaselines_ServoAbsentFromGhost_CountedAsSkipped()
        {
            // Snapshot names a servo the ghost build never produced (unresolved transform,
            // missing prefab). No entry to pose; must not throw.
            GhostBuildResult result = ResultWithServos(
                Servo(100000, 0, "ModuleRoboticServoHinge"));
            result.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                {
                    100000,
                    new SnapshotPartBaseline
                    {
                        roboticPoses = new List<SnapshotRoboticPose>
                        {
                            new SnapshotRoboticPose
                            {
                                ordinal = 3,
                                moduleName = "ModuleRoboticServoHinge",
                                value = 12f,
                            },
                        },
                    }
                },
            };
            var state = new GhostPlaybackState { vesselName = "GhostlessServoVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied")
                && l.Contains("servos=0") && l.Contains("servosSkipped=1"));
        }

        /// <summary>
        /// Was <c>..._RotorWithRpm_SpinsFromSpawn</c>. The applier now parks EVERY rotor:
        /// no persisted key reflects actual spin (<c>currentRPM</c> is not persistent, and
        /// the persisted <c>rpmLimit</c> is a limit SETTING that ships at 460 on the stock
        /// helicopters), so the parser refuses to emit a rotor pose in the first place. The
        /// fixture still hands one in on purpose: even if a rotor pose reached the applier
        /// from somewhere, it must not turn into spin.
        /// </summary>
        [Fact]
        public void ApplySnapshotBaselines_RotorPose_NeverSpinsFromSpawn()
        {
            GhostBuildResult result = ResultWithServos(
                Servo(100000, 0, "ModuleRoboticServoRotor", RoboticVisualMode.RotorRpm));
            result.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                {
                    100000,
                    new SnapshotPartBaseline
                    {
                        roboticPoses = new List<SnapshotRoboticPose>
                        {
                            new SnapshotRoboticPose
                            {
                                ordinal = 0,
                                moduleName = "ModuleRoboticServoRotor",
                                value = 460f,
                            },
                        },
                    }
                },
            };
            var state = new GhostPlaybackState { vesselName = "RotorVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.False(info.active);
        }

        [Fact]
        public void ApplySnapshotBaselines_RotorWithZeroRpm_StaysParked()
        {
            GhostBuildResult result = ResultWithServos(
                Servo(100000, 0, "ModuleRoboticServoRotor", RoboticVisualMode.RotorRpm));
            result.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                {
                    100000,
                    new SnapshotPartBaseline
                    {
                        roboticPoses = new List<SnapshotRoboticPose>
                        {
                            new SnapshotRoboticPose
                            {
                                ordinal = 0,
                                moduleName = "ModuleRoboticServoRotor",
                                value = 0f,
                            },
                        },
                    }
                },
            };
            var state = new GhostPlaybackState { vesselName = "IdleRotorVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.True(info.hasSnapshotBaseline);
            Assert.False(info.active);
        }

        #endregion

        #region Deployable-family application

        [Fact]
        public void ApplySnapshotBaselines_DeployableAndGearOnSamePart_RunsOnceWithoutThrowing()
        {
            // Empty transform list: ApplyDeployableState returns false, so deployables=0.
            // What the cell pins is that the pass visits the part once, resolves the
            // contradictory opinions to a single action, and does not throw.
            var state = new GhostPlaybackState
            {
                vesselName = "MixedVessel",
                deployableInfos = new Dictionary<uint, DeployableGhostInfo>
                {
                    {
                        100000,
                        new DeployableGhostInfo
                        {
                            partPersistentId = 100000,
                            transforms = new List<DeployableTransformState>(),
                        }
                    },
                },
                snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
                {
                    {
                        100000,
                        new SnapshotPartBaseline
                        {
                            deployableExtended = true,
                            gearDeployed = false,
                            cargoBayOpen = true,
                        }
                    },
                },
            };

            GhostPlaybackLogic.ApplySnapshotBaselines(state);

            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied")
                && l.Contains("parts=1") && l.Contains("deployables=0"));
        }

        [Fact]
        public void ApplySnapshotBaselines_ParachuteStowed_IsNotAnAction()
        {
            // STOWED needs nothing: the build-time canopy pose IS stowed. Proven through
            // the pass (rather than only the classifier) because a STOWED baseline must
            // not reach the canopy appliers at all — parachuteInfos is deliberately null
            // here, so any call into them would be visible.
            var state = new GhostPlaybackState
            {
                vesselName = "StowedChuteVessel",
                snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
                {
                    { 100000, new SnapshotPartBaseline { parachutePersistentState = "STOWED" } },
                },
            };

            GhostPlaybackLogic.ApplySnapshotBaselines(state);

            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied")
                && l.Contains("parachutes=0"));
        }

        #endregion

        #region Loop-cycle restore (RestoreRoboticSpawnBaselines)

        [Fact]
        public void RestoreRoboticSpawnBaselines_ResetsCurrentValueToSpawnValue()
        {
            var state = new GhostPlaybackState
            {
                roboticInfos = new Dictionary<ulong, RoboticGhostInfo>
                {
                    {
                        FlightRecorder.EncodeEngineKey(100000, 0),
                        new RoboticGhostInfo
                        {
                            moduleName = "ModuleRoboticServoHinge",
                            visualMode = RoboticVisualMode.Rotational,
                            currentValue = 99f,
                            spawnValue = 15f,
                            hasSnapshotBaseline = true,
                            active = true,
                            lastUpdateUT = 1234.0,
                        }
                    },
                },
            };

            int restored = GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.Equal(1, restored);
            Assert.Equal(15f, info.currentValue);
            Assert.False(info.active);
            Assert.True(double.IsNaN(info.lastUpdateUT));
        }

        [Fact]
        public void RestoreRoboticSpawnBaselines_NoSnapshotBaseline_RestoresPrefabPoseZero()
        {
            var state = new GhostPlaybackState
            {
                roboticInfos = new Dictionary<ulong, RoboticGhostInfo>
                {
                    {
                        FlightRecorder.EncodeEngineKey(100000, 0),
                        new RoboticGhostInfo
                        {
                            moduleName = "ModuleRoboticServoPiston",
                            visualMode = RoboticVisualMode.Linear,
                            currentValue = 0.8f,
                            active = true,
                        }
                    },
                },
            };

            GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.Equal(0f, info.currentValue);
            Assert.False(info.active);
        }

        /// <summary>
        /// Was <c>..._RotorWithSnapshotRpm_ReArmsSpin</c>. The loop-cycle restore parks
        /// rotors unconditionally now, for the same reason the spawn path does — and this is
        /// the cell that catches the WORST shape of the old behaviour: a rotor re-armed at
        /// its limit on EVERY cycle boundary, so a parked helicopter's replay spun up again
        /// each time the loop came round.
        /// </summary>
        [Fact]
        public void RestoreRoboticSpawnBaselines_RotorWithSnapshotRpm_StillParks()
        {
            var state = new GhostPlaybackState
            {
                roboticInfos = new Dictionary<ulong, RoboticGhostInfo>
                {
                    {
                        FlightRecorder.EncodeEngineKey(100000, 0),
                        new RoboticGhostInfo
                        {
                            moduleName = "ModuleRoboticServoRotor",
                            visualMode = RoboticVisualMode.RotorRpm,
                            currentValue = 0f,
                            spawnValue = 460f,
                            hasSnapshotBaseline = true,
                            active = false,
                        }
                    },
                },
            };

            GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.False(info.active);
        }

        [Fact]
        public void RestoreRoboticSpawnBaselines_RotorWithoutSnapshotBaseline_StaysParked()
        {
            // A rotor whose spawnValue is only the default 0 must never be re-armed —
            // that would make a stationary rotor start spinning on cycle 2.
            var state = new GhostPlaybackState
            {
                roboticInfos = new Dictionary<ulong, RoboticGhostInfo>
                {
                    {
                        FlightRecorder.EncodeEngineKey(100000, 0),
                        new RoboticGhostInfo
                        {
                            moduleName = "ModuleRoboticServoRotor",
                            visualMode = RoboticVisualMode.RotorRpm,
                            currentValue = 120f,
                            active = true,
                        }
                    },
                },
            };

            GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state);

            RoboticGhostInfo info =
                state.roboticInfos[FlightRecorder.EncodeEngineKey(100000, 0)];
            Assert.Equal(0f, info.currentValue);
            Assert.False(info.active);
        }

        [Fact]
        public void RestoreRoboticSpawnBaselines_NullInfoOrDict_NoThrow()
        {
            Assert.Equal(0, GhostPlaybackLogic.RestoreRoboticSpawnBaselines(null));
            Assert.Equal(0, GhostPlaybackLogic.RestoreRoboticSpawnBaselines(
                new GhostPlaybackState()));

            var state = new GhostPlaybackState
            {
                roboticInfos = new Dictionary<ulong, RoboticGhostInfo>
                {
                    { FlightRecorder.EncodeEngineKey(100000, 0), null },
                },
            };
            Assert.Equal(0, GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state));
        }

        #endregion

        #region Build-type provenance (end-state fallback suppression)

        [Fact]
        public void SnapshotBaselineTrusted_OnlyForTheRecordingStartSnapshot()
        {
            Assert.True(GhostVisualBuilder.SnapshotBaselineTrustedForBuildType(
                HeaviestSpawnBuildType.RecordingStartSnapshot));
        }

        // `buildType` is a byte rather than the enum itself: HeaviestSpawnBuildType is
        // internal and an internal type cannot appear in the signature of the public method
        // xUnit needs in order to discover the theory (same reason as the parachute-action
        // theory above). 2 = VesselSnapshot, 3 = SphereFallback, 0 = None.
        [Theory]
        [InlineData((byte)2)]
        [InlineData((byte)3)]
        [InlineData((byte)0)]
        public void SnapshotBaselineNotTrusted_ForEveryOtherBuildType(byte rawBuildType)
        {
            var buildType = (HeaviestSpawnBuildType)rawBuildType;
            // VesselSnapshot is the load-bearing one: GetGhostSnapshot fell back to the
            // END-of-recording snapshot, and spawning a ghost at its own end state is a new
            // way to be wrong (a chute whose end state is CUT would hide its canopy through
            // the whole descent). Pre-M1 the fallback was harmless precisely because nothing
            // read module state out of it.
            Assert.False(GhostVisualBuilder.SnapshotBaselineTrustedForBuildType(buildType));
        }

        #endregion
    }
}
