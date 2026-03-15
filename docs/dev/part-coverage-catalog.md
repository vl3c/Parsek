# KSP Part Coverage Catalog for Parsek

Last updated: 2026-03-15
Generated from: KSP 1.12.5 GameData (Squad + SquadExpansion)

**Generation method:** Script-based extraction from all `.cfg` files under
`GameData/Squad/Parts/` and `GameData/SquadExpansion/`, cross-referenced with
Parsek source code (`GhostVisualBuilder.cs`, `FlightRecorder.cs`, `ParsekFlight.cs`)
and showcase test entries in `SyntheticRecordingTests.cs`. Generator scripts are not
checked in; re-run the extraction from the KSP GameData directory to refresh.

## Summary

- Total unique KSP parts: 483
- Stock: 358 | Making History: 71 | Breaking Ground: 54
- Ghost mesh rendering: 483 supported (all parts with MeshRenderer cloning)
- Dynamic visual support: 171 full / 73 partial / 239 N/A (no visual modules)
- Showcase coverage: 211 / 244 parts with visual modules (+ 1 EVA kerbal = 212 total showcased)
- EVA kerbals: 8 | Uncategorized/deprecated (category=none): 14

## Known Visual Playback Issues

| Bug | Description | Status | Affected Parts |
|-----|-------------|--------|----------------|
| #28 | Building collision doesn't set TerminalState.Destroyed | Open | crash into KSC buildings |
| #29 | Ghost parts missing or wrong state | Open | rover wheels, SmallGearBay |
| #30 | RCS fires constantly | Open | all RCS parts |
| #31 | Engine shroud/cover variants | Open | multi-variant engines |
| #32 | LES plumes need verification | Open | LaunchEscapeSystem |
| #33 | Crash breakup not progressive | Open | all parts |
| #34 | ShouldTriggerExplosion log spam | Fixed | all ghost parts |
| #35 | Engine FX playing=False first frame | Not a bug | all engine parts |
| #36 | GhostVisual VERBOSE log spam | Fixed | all ghost parts |
| #37 | Ghost shows wrong texture variant | Fixed | parts with TEXTURE variant rules |
| #38 | SRB nozzle glow persists after burnout | Fixed | 33 FXModuleAnimateThrottle parts (7 SRBs most visible) |

## Unsupported Visual Module Types

| Module | Description | Affected Parts | Impact |
|--------|-------------|----------------|--------|
| ModuleColorChanger | Cabin lights, ablator colors | 33 parts | Low — cosmetic interior lights |
| FXModuleAnimateThrottle | Throttle-driven nozzle glow | 33 parts | Medium — engine nozzle animations missing |
| FXModuleAnimateRCS | RCS response animation | 5 parts | Low — subtle visual only |
| ModulePartFirework | Firework FX | 2 parts | None — novelty item |

## Parts by Category

### Command Pods

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| cupola | cupola | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| HECS2_ProbeCore | HECS2.ProbeCore | Stock | — | N/A | — |  |
| kv1Pod | kv1Pod | Making History | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| kv2Pod | kv2Pod | Making History | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| kv3Pod | kv3Pod | Making History | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| landerCabinSmall | landerCabinSmall | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| Mark1Cockpit | Mark1Cockpit | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| Mark2Cockpit | Mark2Cockpit | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| MEMLander | MEMLander | Making History | ModuleRCSFX, ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk1-3pod | mk1-3pod | Stock | ModuleRCSFX, ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk1pod_v2 | mk1pod.v2 | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk2Cockpit_Inline | mk2Cockpit.Inline | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk2Cockpit_Standard | mk2Cockpit.Standard | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk2DroneCore | mk2DroneCore | Stock | — | N/A | — |  |
| mk2LanderCabin_v2 | mk2LanderCabin.v2 | Stock | ModuleAnimateGeneric, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| Mk2Pod | Mk2Pod | Making History | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk3Cockpit_Shuttle | mk3Cockpit.Shuttle | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| MpoProbe | MpoProbe | Stock | — | N/A | — |  |
| MtmStage | MtmStage | Stock | — | N/A | — |  |
| probeCoreCube | probeCoreCube | Stock | — | N/A | — |  |
| probeCoreHex_v2 | probeCoreHex.v2 | Stock | — | N/A | — |  |
| probeCoreOcto2_v2 | probeCoreOcto2.v2 | Stock | — | N/A | — |  |
| probeCoreOcto_v2 | probeCoreOcto.v2 | Stock | — | N/A | — |  |
| probeCoreSphere_v2 | probeCoreSphere.v2 | Stock | — | N/A | — |  |
| probeStackLarge | probeStackLarge | Stock | — | N/A | — |  |
| probeStackSmall | probeStackSmall | Stock | — | N/A | — |  |
| roverBody_v2 | roverBody.v2 | Stock | — | N/A | — |  |
| seatExternalCmd | seatExternalCmd | Stock | — | N/A | — |  |

### Propulsion — Liquid Engines

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| engineLargeSkipper_v2 | engineLargeSkipper.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| ionEngine | ionEngine | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| liquidEngine2-2_v2 | liquidEngine2-2.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| liquidEngine2_v2 | liquidEngine2.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| liquidEngine3_v2 | liquidEngine3.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| liquidEngine_v2 | liquidEngine.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| LiquidEngineKE-1 | LiquidEngineKE-1 | Making History | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| LiquidEngineLV-T91 | LiquidEngineLV-T91 | Making History | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| LiquidEngineLV-TX87 | LiquidEngineLV-TX87 | Making History | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| liquidEngineMainsail_v2 | liquidEngineMainsail.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| liquidEngineMini_v2 | liquidEngineMini.v2 | Stock | ModuleEnginesFX, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| LiquidEngineRE-I2 | LiquidEngineRE-I2 | Making History | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| LiquidEngineRE-J10 | LiquidEngineRE-J10 | Making History | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| LiquidEngineRK-7 | LiquidEngineRK-7 | Making History | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| LiquidEngineRV-1 | LiquidEngineRV-1 | Making History | ModuleEnginesFX | Full | Yes |  |
| microEngine_v2 | microEngine.v2 | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| nuclearEngine | nuclearEngine | Stock | ModuleEngines, ModuleJettison, ModuleAnimateHeat | Full | Yes | Has shroud (ModuleJettison) |
| omsEngine | omsEngine | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| radialEngineMini_v2 | radialEngineMini.v2 | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| radialLiquidEngine1-2 | radialLiquidEngine1-2 | Stock | ModuleEngines, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| Size2LFB_v2 | Size2LFB.v2 | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| Size3AdvancedEngine | Size3AdvancedEngine | Stock | ModuleEnginesFX, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| Size3EngineCluster | Size3EngineCluster | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| smallRadialEngine_v2 | smallRadialEngine.v2 | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| SSME | SSME | Stock | ModuleEnginesFX, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| toroidalAerospike | toroidalAerospike | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |

### Propulsion — Solid Boosters

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| Clydesdale | Clydesdale | Stock | ModuleEngines, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| MassiveBooster | MassiveBooster | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| Mite | Mite | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| Pollux | Pollux | Making History | ModuleEngines, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| sepMotor1 | sepMotor1 | Stock | ModuleEngines | Full | Yes |  |
| Shrimp | Shrimp | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| solidBooster1-1 | solidBooster1-1 | Stock | ModuleEngines, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| solidBooster_sm_v2 | solidBooster.sm.v2 | Stock | ModuleEngines, ModuleJettison | Full | Yes | Has shroud (ModuleJettison) |
| solidBooster_v2 | solidBooster.v2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| Thoroughbred | Thoroughbred | Stock | ModuleEngines, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |

### Propulsion — Jet Engines

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| JetEngine | JetEngine | Stock | ModuleEnginesFX, ModuleAnimateHeat, ModuleAnimateGeneric | Full | Yes |  |
| miniJetEngine | miniJetEngine | Stock | ModuleEnginesFX | Full | Yes |  |
| RAPIER | RAPIER | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| turboFanEngine | turboFanEngine | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |
| turboFanSize2 | turboFanSize2 | Stock | ModuleEnginesFX, ModuleAnimateHeat, ModuleAnimateGeneric, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work); Has shroud (ModuleJettison) |
| turboJet | turboJet | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | Yes | Throttle animation not tracked (FX particles work) |

### Fuel Tanks

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| adapterMk3-Mk2 | adapterMk3-Mk2 | Stock | — | N/A | — |  |
| adapterMk3-Size2 | adapterMk3-Size2 | Stock | — | N/A | — |  |
| adapterMk3-Size2Slant | adapterMk3-Size2Slant | Stock | — | N/A | — |  |
| adapterSize2-Mk2 | adapterSize2-Mk2 | Stock | — | N/A | — |  |
| adapterSize2-Size1 | adapterSize2-Size1 | Stock | — | N/A | — |  |
| adapterSize2-Size1Slant | adapterSize2-Size1Slant | Stock | — | N/A | — |  |
| adapterSize3-Mk3 | adapterSize3-Mk3 | Stock | — | N/A | — |  |
| externalTankCapsule | externalTankCapsule | Stock | — | N/A | — |  |
| externalTankRound | externalTankRound | Stock | — | N/A | — |  |
| externalTankToroid | externalTankToroid | Stock | — | N/A | — |  |
| fuelLine | fuelLine | Stock | — | N/A | — |  |
| fuelTank | fuelTank | Stock | — | N/A | — |  |
| fuelTank_long | fuelTank.long | Stock | — | N/A | — |  |
| fuelTankSmall | fuelTankSmall | Stock | — | N/A | — |  |
| fuelTankSmallFlat | fuelTankSmallFlat | Stock | — | N/A | — |  |
| LargeTank | LargeTank | Stock | — | N/A | — |  |
| miniFuelTank | miniFuelTank | Stock | — | N/A | — |  |
| miniFuselage | miniFuselage | Stock | — | N/A | — |  |
| MK1Fuselage | MK1Fuselage | Stock | — | N/A | — |  |
| mk2_1m_AdapterLong | mk2.1m.AdapterLong | Stock | — | N/A | — |  |
| mk2_1m_Bicoupler | mk2.1m.Bicoupler | Stock | — | N/A | — |  |
| mk2Fuselage | mk2Fuselage | Stock | — | N/A | — |  |
| mk2FuselageLongLFO | mk2FuselageLongLFO | Stock | — | N/A | — |  |
| mk2FuselageShortLFO | mk2FuselageShortLFO | Stock | — | N/A | — |  |
| mk2FuselageShortLiquid | mk2FuselageShortLiquid | Stock | — | N/A | — |  |
| mk2FuselageShortMono | mk2FuselageShortMono | Stock | — | N/A | — |  |
| mk2SpacePlaneAdapter | mk2SpacePlaneAdapter | Stock | — | N/A | — |  |
| mk3FuselageLF_100 | mk3FuselageLF.100 | Stock | — | N/A | — |  |
| mk3FuselageLF_25 | mk3FuselageLF.25 | Stock | — | N/A | — |  |
| mk3FuselageLF_50 | mk3FuselageLF.50 | Stock | — | N/A | — |  |
| mk3FuselageLFO_100 | mk3FuselageLFO.100 | Stock | — | N/A | — |  |
| mk3FuselageLFO_25 | mk3FuselageLFO.25 | Stock | — | N/A | — |  |
| mk3FuselageLFO_50 | mk3FuselageLFO.50 | Stock | — | N/A | — |  |
| mk3FuselageMONO | mk3FuselageMONO | Stock | — | N/A | — |  |
| monopropMiniSphere | monopropMiniSphere | Making History | — | N/A | — |  |
| noseConeAdapter | noseConeAdapter | Stock | ModuleAnimateHeat | Full | Yes |  |
| RadialOreTank | RadialOreTank | Stock | — | N/A | — |  |
| radialRCSTank | radialRCSTank | Stock | — | N/A | — |  |
| RCSFuelTank | RCSFuelTank | Stock | — | N/A | — |  |
| RCSTank1-2 | RCSTank1-2 | Stock | — | N/A | — |  |
| rcsTankMini | rcsTankMini | Stock | — | N/A | — |  |
| rcsTankRadialLong | rcsTankRadialLong | Stock | — | N/A | — |  |
| ReleaseValve | ReleaseValve | Stock | — | N/A | — |  |
| Rockomax16_BW | Rockomax16.BW | Stock | — | N/A | — |  |
| Rockomax32_BW | Rockomax32.BW | Stock | — | N/A | — |  |
| Rockomax64_BW | Rockomax64.BW | Stock | — | N/A | — |  |
| Rockomax8BW | Rockomax8BW | Stock | — | N/A | — |  |
| Size1p5_Monoprop | Size1p5.Monoprop | Making History | — | N/A | — |  |
| Size1p5_Size0_Adapter_01 | Size1p5.Size0.Adapter.01 | Making History | — | N/A | — |  |
| Size1p5_Size1_Adapter_01 | Size1p5.Size1.Adapter.01 | Making History | — | N/A | — |  |
| Size1p5_Size1_Adapter_02 | Size1p5.Size1.Adapter.02 | Making History | — | N/A | — |  |
| Size1p5_Size2_Adapter_01 | Size1p5.Size2.Adapter.01 | Making History | — | N/A | — |  |
| Size1p5_Tank_01 | Size1p5.Tank.01 | Making History | — | N/A | — |  |
| Size1p5_Tank_02 | Size1p5.Tank.02 | Making History | — | N/A | — |  |
| Size1p5_Tank_03 | Size1p5.Tank.03 | Making History | — | N/A | — |  |
| Size1p5_Tank_04 | Size1p5.Tank.04 | Making History | — | N/A | — |  |
| Size1p5_Tank_05 | Size1p5.Tank.05 | Making History | ModuleEngines | Full | — |  |
| Size3_Size4_Adapter_01 | Size3.Size4.Adapter.01 | Making History | — | N/A | — |  |
| Size3LargeTank | Size3LargeTank | Stock | — | N/A | — |  |
| Size3MediumTank | Size3MediumTank | Stock | — | N/A | — |  |
| Size3SmallTank | Size3SmallTank | Stock | — | N/A | — |  |
| Size3To2Adapter_v2 | Size3To2Adapter.v2 | Stock | — | N/A | — |  |
| Size4_EngineAdapter_01 | Size4.EngineAdapter.01 | Making History | — | N/A | — |  |
| Size4_Tank_01 | Size4.Tank.01 | Making History | — | N/A | — |  |
| Size4_Tank_02 | Size4.Tank.02 | Making History | — | N/A | — |  |
| Size4_Tank_03 | Size4.Tank.03 | Making History | — | N/A | — |  |
| Size4_Tank_04 | Size4.Tank.04 | Making History | — | N/A | — |  |
| SmallTank | SmallTank | Stock | — | N/A | — |  |
| xenonTank | xenonTank | Stock | — | N/A | — |  |
| xenonTankLarge | xenonTankLarge | Stock | — | N/A | — |  |
| xenonTankRadial | xenonTankRadial | Stock | — | N/A | — |  |

### Aerodynamics

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| AdvancedCanard | AdvancedCanard | Stock | ModuleControlSurface | Full | Yes |  |
| airbrake1 | airbrake1 | Stock | ModuleAeroSurface | Full | Yes |  |
| airlinerCtrlSrf | airlinerCtrlSrf | Stock | ModuleControlSurface | Full | Yes |  |
| airlinerMainWing | airlinerMainWing | Stock | — | N/A | — |  |
| airlinerTailFin | airlinerTailFin | Stock | ModuleControlSurface | Full | Yes |  |
| airplaneTail | airplaneTail | Stock | ModuleAnimateHeat | Full | Yes |  |
| airplaneTailB | airplaneTailB | Stock | ModuleAnimateHeat | Full | Yes |  |
| airScoop | airScoop | Stock | — | N/A | — |  |
| basicFin | basicFin | Stock | — | N/A | — |  |
| CanardController | CanardController | Stock | ModuleControlSurface | Full | Yes |  |
| CircularIntake | CircularIntake | Stock | ModuleAnimateHeat | Full | Yes |  |
| delta_small | delta.small | Stock | — | N/A | — |  |
| deltaWing | deltaWing | Stock | — | N/A | — |  |
| elevon2 | elevon2 | Stock | ModuleControlSurface | Full | Yes |  |
| elevon3 | elevon3 | Stock | ModuleControlSurface | Full | Yes |  |
| elevon5 | elevon5 | Stock | ModuleControlSurface | Full | Yes |  |
| FanShroud_01 | FanShroud.01 | Breaking Ground | — | N/A | — |  |
| FanShroud_02 | FanShroud.02 | Breaking Ground | — | N/A | — |  |
| FanShroud_03 | FanShroud.03 | Breaking Ground | — | N/A | — |  |
| IntakeRadialLong | IntakeRadialLong | Stock | — | N/A | — |  |
| largeFanBlade | largeFanBlade | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| largeHeliBlade | largeHeliBlade | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| largePropeller | largePropeller | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| mediumFanBlade | mediumFanBlade | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| mediumHeliBlade | mediumHeliBlade | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| mediumPropeller | mediumPropeller | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| miniIntake | miniIntake | Stock | — | N/A | — |  |
| MK1IntakeFuselage | MK1IntakeFuselage | Stock | ModuleAnimateHeat | Full | Yes |  |
| nacelleBody | nacelleBody | Stock | ModuleAnimateHeat | Full | Yes |  |
| noseCone | noseCone | Stock | — | N/A | — |  |
| noseconeTiny | noseconeTiny | Breaking Ground | — | N/A | — |  |
| noseconeVS | noseconeVS | Breaking Ground | — | N/A | — |  |
| pointyNoseConeA | pointyNoseConeA | Stock | ModuleAnimateHeat | Full | Yes |  |
| pointyNoseConeB | pointyNoseConeB | Stock | ModuleAnimateHeat | Full | Yes |  |
| R8winglet | R8winglet | Stock | ModuleControlSurface | Full | Yes |  |
| radialEngineBody | radialEngineBody | Stock | ModuleAnimateHeat | Full | Yes |  |
| ramAirIntake | ramAirIntake | Stock | ModuleAnimateHeat | Full | Yes |  |
| rocketNoseCone_v3 | rocketNoseCone.v3 | Stock | — | N/A | — |  |
| rocketNoseConeSize3 | rocketNoseConeSize3 | Stock | — | N/A | — |  |
| rocketNoseConeSize4 | rocketNoseConeSize4 | Making History | — | N/A | — |  |
| shockConeIntake | shockConeIntake | Stock | ModuleAnimateHeat | Full | Yes |  |
| Size_1_5_Cone | Size.1.5.Cone | Making History | — | N/A | — |  |
| smallCtrlSrf | smallCtrlSrf | Stock | ModuleControlSurface | Full | Yes |  |
| smallFanBlade | smallFanBlade | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| smallHeliBlade | smallHeliBlade | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| smallPropeller | smallPropeller | Breaking Ground | ModuleControlSurface | Full | Yes |  |
| StandardCtrlSrf | StandardCtrlSrf | Stock | ModuleControlSurface | Full | Yes |  |
| standardNoseCone | standardNoseCone | Stock | ModuleAnimateHeat | Full | Yes |  |
| structuralWing | structuralWing | Stock | — | N/A | — |  |
| structuralWing2 | structuralWing2 | Stock | — | N/A | — |  |
| structuralWing3 | structuralWing3 | Stock | — | N/A | — |  |
| structuralWing4 | structuralWing4 | Stock | — | N/A | — |  |
| sweptWing | sweptWing | Stock | — | N/A | — |  |
| sweptWing1 | sweptWing1 | Stock | — | N/A | — |  |
| sweptWing2 | sweptWing2 | Stock | — | N/A | — |  |
| tailfin | tailfin | Stock | ModuleControlSurface | Full | Yes |  |
| wingConnector | wingConnector | Stock | — | N/A | — |  |
| wingConnector2 | wingConnector2 | Stock | — | N/A | — |  |
| wingConnector3 | wingConnector3 | Stock | — | N/A | — |  |
| wingConnector4 | wingConnector4 | Stock | — | N/A | — |  |
| wingConnector5 | wingConnector5 | Stock | — | N/A | — |  |
| winglet | winglet | Stock | — | N/A | — |  |
| winglet3 | winglet3 | Stock | ModuleControlSurface | Full | Yes |  |
| wingShuttleDelta | wingShuttleDelta | Stock | — | N/A | — |  |
| wingShuttleElevon1 | wingShuttleElevon1 | Stock | ModuleControlSurface | Full | Yes |  |
| wingShuttleElevon2 | wingShuttleElevon2 | Stock | ModuleControlSurface | Full | Yes |  |
| wingShuttleRudder | wingShuttleRudder | Stock | ModuleControlSurface | Full | Yes |  |
| wingShuttleStrake | wingShuttleStrake | Stock | — | N/A | — |  |
| wingStrake | wingStrake | Stock | — | N/A | — |  |

### Structural

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| adapterEngines | adapterEngines | Stock | — | N/A | — |  |
| adapterLargeSmallBi | adapterLargeSmallBi | Stock | — | N/A | — |  |
| adapterLargeSmallQuad | adapterLargeSmallQuad | Stock | — | N/A | — |  |
| adapterLargeSmallTri | adapterLargeSmallTri | Stock | — | N/A | — |  |
| adapterSmallMiniShort | adapterSmallMiniShort | Stock | — | N/A | — |  |
| adapterSmallMiniTall | adapterSmallMiniTall | Stock | — | N/A | — |  |
| EquiTriangle0 | EquiTriangle0 | Making History | — | N/A | — |  |
| EquiTriangle1 | EquiTriangle1 | Making History | — | N/A | — |  |
| EquiTriangle1p5 | EquiTriangle1p5 | Making History | — | N/A | — |  |
| EquiTriangle2 | EquiTriangle2 | Making History | — | N/A | — |  |
| largeAdapter | largeAdapter | Stock | — | N/A | — |  |
| largeAdapter2 | largeAdapter2 | Stock | — | N/A | — |  |
| launchClamp1 | launchClamp1 | Stock | — | N/A | — |  |
| lGripPad | lGripPad | Breaking Ground | — | N/A | — |  |
| lGripStrip | lGripStrip | Breaking Ground | — | N/A | — |  |
| mGripPad | mGripPad | Breaking Ground | — | N/A | — |  |
| Mk1FuselageStructural | Mk1FuselageStructural | Stock | — | N/A | — |  |
| Panel0 | Panel0 | Making History | — | N/A | — |  |
| Panel1 | Panel1 | Making History | — | N/A | — |  |
| Panel1p5 | Panel1p5 | Making History | — | N/A | — |  |
| Panel2 | Panel2 | Making History | — | N/A | — |  |
| sGripPad | sGripPad | Breaking Ground | — | N/A | — |  |
| sGripStrip | sGripStrip | Breaking Ground | — | N/A | — |  |
| smallHardpoint | smallHardpoint | Stock | — | N/A | — |  |
| stackBiCoupler_v2 | stackBiCoupler.v2 | Stock | — | N/A | — |  |
| stackPoint1 | stackPoint1 | Stock | — | N/A | — |  |
| stackQuadCoupler | stackQuadCoupler | Stock | — | N/A | — |  |
| stackTriCoupler_v2 | stackTriCoupler.v2 | Stock | — | N/A | — |  |
| stationHub | stationHub | Stock | — | N/A | — |  |
| structuralIBeam1 | structuralIBeam1 | Stock | — | N/A | — |  |
| structuralIBeam2 | structuralIBeam2 | Stock | — | N/A | — |  |
| structuralIBeam3 | structuralIBeam3 | Stock | — | N/A | — |  |
| structuralMiniNode | structuralMiniNode | Stock | — | N/A | — |  |
| structuralPanel1 | structuralPanel1 | Stock | — | N/A | — |  |
| structuralPanel2 | structuralPanel2 | Stock | — | N/A | — |  |
| structuralPylon | structuralPylon | Stock | — | N/A | — |  |
| strutConnector | strutConnector | Stock | — | N/A | — |  |
| strutCube | strutCube | Stock | — | N/A | — |  |
| strutOcto | strutOcto | Stock | — | N/A | — |  |
| Triangle0 | Triangle0 | Making History | — | N/A | — |  |
| Triangle1 | Triangle1 | Making History | — | N/A | — |  |
| Triangle1p5 | Triangle1p5 | Making History | — | N/A | — |  |
| Triangle2 | Triangle2 | Making History | — | N/A | — |  |
| trussAdapter | trussAdapter | Stock | — | N/A | — |  |
| trussPiece1x | trussPiece1x | Stock | — | N/A | — |  |
| trussPiece3x | trussPiece3x | Stock | — | N/A | — |  |
| Tube1 | Tube1 | Making History | — | N/A | — |  |
| Tube1p5 | Tube1p5 | Making History | — | N/A | — |  |
| Tube2 | Tube2 | Making History | — | N/A | — |  |
| Tube3 | Tube3 | Making History | — | N/A | — |  |
| Tube4 | Tube4 | Making History | — | N/A | — |  |

### Coupling

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| Decoupler_0 | Decoupler.0 | Stock | — | N/A | — |  |
| Decoupler_1 | Decoupler.1 | Stock | — | N/A | — |  |
| Decoupler_1p5 | Decoupler.1p5 | Making History | — | N/A | — |  |
| Decoupler_2 | Decoupler.2 | Stock | — | N/A | — |  |
| Decoupler_3 | Decoupler.3 | Stock | — | N/A | — |  |
| Decoupler_4 | Decoupler.4 | Making History | — | N/A | — |  |
| dockingPort1 | dockingPort1 | Stock | ModuleDockingNode, ModuleAnimateGeneric, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| dockingPort2 | dockingPort2 | Stock | ModuleDockingNode, ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| dockingPort3 | dockingPort3 | Stock | ModuleDockingNode | Full | — |  |
| dockingPortLarge | dockingPortLarge | Stock | ModuleDockingNode, ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| dockingPortLateral | dockingPortLateral | Stock | ModuleDockingNode, ModuleAnimateGeneric, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| EnginePlate1p5 | EnginePlate1p5 | Making History | ModuleJettison | Full | Yes |  |
| EnginePlate2 | EnginePlate2 | Making History | ModuleJettison | Full | Yes |  |
| EnginePlate3 | EnginePlate3 | Making History | ModuleJettison | Full | Yes |  |
| EnginePlate4 | EnginePlate4 | Making History | ModuleJettison | Full | Yes |  |
| EnginePlate5 | EnginePlate5 | Making History | ModuleJettison | Full | Yes |  |
| GrapplingDevice | GrapplingDevice | Stock | ModuleAnimateGeneric | Full | Yes |  |
| InflatableAirlock | InflatableAirlock | Making History | ModuleDockingNode, ModuleAnimateGeneric | Full | Yes |  |
| mk2DockingPort | mk2DockingPort | Stock | ModuleDockingNode, ModuleAnimateGeneric, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| radialDecoupler | radialDecoupler | Stock | — | N/A | — |  |
| radialDecoupler1-2 | radialDecoupler1-2 | Stock | — | N/A | — |  |
| radialDecoupler2 | radialDecoupler2 | Stock | — | N/A | — |  |
| Separator_0 | Separator.0 | Stock | — | N/A | — |  |
| Separator_1 | Separator.1 | Stock | — | N/A | — |  |
| Separator_1p5 | Separator.1p5 | Making History | — | N/A | — |  |
| Separator_2 | Separator.2 | Stock | — | N/A | — |  |
| Separator_3 | Separator.3 | Stock | — | N/A | — |  |
| Separator_4 | Separator.4 | Making History | — | N/A | — |  |
| Size1p5_Strut_Decoupler | Size1p5.Strut.Decoupler | Making History | — | N/A | — |  |
| smallClaw | smallClaw | Stock | ModuleAnimateGeneric | Full | Yes |  |

### Payload

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| fairingSize1 | fairingSize1 | Stock | ModuleProceduralFairing, ModuleCargoBay | Full | Yes |  |
| fairingSize1p5 | fairingSize1p5 | Making History | ModuleProceduralFairing, ModuleCargoBay | Full | Yes |  |
| fairingSize2 | fairingSize2 | Stock | ModuleProceduralFairing, ModuleCargoBay | Full | Yes |  |
| fairingSize3 | fairingSize3 | Stock | ModuleProceduralFairing, ModuleCargoBay | Full | Yes |  |
| fairingSize4 | fairingSize4 | Making History | ModuleProceduralFairing, ModuleCargoBay | Full | Yes |  |
| mk2CargoBayL | mk2CargoBayL | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| mk2CargoBayS | mk2CargoBayS | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| mk3CargoBayL | mk3CargoBayL | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| mk3CargoBayM | mk3CargoBayM | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| mk3CargoBayS | mk3CargoBayS | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| mk3CargoRamp | mk3CargoRamp | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| ServiceBay_125_v2 | ServiceBay.125.v2 | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| ServiceBay_250_v2 | ServiceBay.250.v2 | Stock | ModuleAnimateGeneric, ModuleCargoBay | Full | Yes |  |
| ServiceModule18 | ServiceModule18 | Making History | ModuleCargoBay, ModuleJettison | Full | Yes |  |
| ServiceModule25 | ServiceModule25 | Making History | ModuleCargoBay, ModuleJettison | Full | Yes |  |
| Size1to0ServiceModule | Size1to0ServiceModule | Making History | ModuleCargoBay, ModuleJettison | Full | Yes |  |

### Utility

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| crewCabin | crewCabin | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| domeLight1 | domeLight1 | Stock | ModuleLight | Full | Yes |  |
| fireworksLauncherBig | fireworksLauncherBig | Stock | ModulePartFirework | Partial | — | Firework FX not tracked |
| fireworksLauncherSmall | fireworksLauncherSmall | Stock | ModulePartFirework | Partial | — | Firework FX not tracked |
| flagPartFlat | flagPartFlat | Stock | — | N/A | — |  |
| flagPartSize0 | flagPartSize0 | Stock | — | N/A | — |  |
| flagPartSize1 | flagPartSize1 | Stock | — | N/A | — |  |
| flagPartSize1p5 | flagPartSize1p5 | Making History | — | N/A | — |  |
| flagPartSize2 | flagPartSize2 | Stock | — | N/A | — |  |
| flagPartSize3 | flagPartSize3 | Stock | — | N/A | — |  |
| flagPartSize4 | flagPartSize4 | Making History | — | N/A | — |  |
| ISRU | ISRU | Stock | ModuleAnimationGroup | Full | — |  |
| ladder1 | ladder1 | Stock | — | N/A | — |  |
| LaunchEscapeSystem | LaunchEscapeSystem | Stock | ModuleEnginesFX | Full | — | Bug #32: LES plumes need verification |
| MiniDrill | MiniDrill | Stock | ModuleAnimationGroup | Full | Yes |  |
| MiniISRU | MiniISRU | Stock | — | N/A | — |  |
| MK1CrewCabin | MK1CrewCabin | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk2CrewCabin | mk2CrewCabin | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| mk3CrewCabin | mk3CrewCabin | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| navLight1 | navLight1 | Stock | ModuleLight | Full | Yes |  |
| parachuteDrogue | parachuteDrogue | Stock | ModuleParachute | Full | Yes |  |
| parachuteLarge | parachuteLarge | Stock | ModuleParachute | Full | Yes |  |
| parachuteRadial | parachuteRadial | Stock | ModuleParachute | Full | Yes |  |
| parachuteSingle | parachuteSingle | Stock | ModuleParachute | Full | Yes |  |
| RadialDrill | RadialDrill | Stock | ModuleAnimationGroup | Full | Yes |  |
| radialDrogue | radialDrogue | Stock | ModuleParachute | Full | Yes |  |
| spotLight1_v2 | spotLight1.v2 | Stock | ModuleLight | Full | Yes |  |
| spotLight2_v2 | spotLight2.v2 | Stock | ModuleLight | Full | Yes |  |
| spotLight3 | spotLight3 | Stock | ModuleLight | Full | Yes |  |
| stripLight1 | stripLight1 | Stock | ModuleLight | Full | Yes |  |
| telescopicLadder | telescopicLadder | Stock | RetractableLadder | Full | Yes |  |
| telescopicLadderBay | telescopicLadderBay | Stock | RetractableLadder | Full | Yes |  |

### Science

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| GooExperiment | GooExperiment | Stock | ModuleAnimateGeneric | Full | Yes |  |
| InfraredTelescope | InfraredTelescope | Stock | — | N/A | — |  |
| Large_Crewed_Lab | Large.Crewed.Lab | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| Magnetometer | Magnetometer | Stock | ModuleDeployablePart | Full | Yes |  |
| OrbitalScanner | OrbitalScanner | Stock | ModuleAnimationGroup | Full | — |  |
| RobotArmScanner_S1 | RobotArmScanner.S1 | Breaking Ground | ModuleRobotArmScanner | Full | Yes |  |
| RobotArmScanner_S2 | RobotArmScanner.S2 | Breaking Ground | ModuleRobotArmScanner | Full | Yes |  |
| RobotArmScanner_S3 | RobotArmScanner.S3 | Breaking Ground | ModuleRobotArmScanner | Full | Yes |  |
| science_module | science.module | Stock | ModuleAnimateGeneric | Full | Yes |  |
| ScienceBox | ScienceBox | Stock | — | N/A | — |  |
| sensorAccelerometer | sensorAccelerometer | Stock | — | N/A | — |  |
| sensorAtmosphere | sensorAtmosphere | Stock | — | N/A | — |  |
| sensorBarometer | sensorBarometer | Stock | — | N/A | — |  |
| sensorGravimeter | sensorGravimeter | Stock | — | N/A | — |  |
| sensorThermometer | sensorThermometer | Stock | — | N/A | — |  |
| SurfaceScanner | SurfaceScanner | Stock | — | N/A | — |  |
| SurveyScanner | SurveyScanner | Stock | ModuleAnimationGroup | Full | Yes |  |

### Electrical

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| batteryBank | batteryBank | Stock | — | N/A | — |  |
| batteryBankLarge | batteryBankLarge | Stock | — | N/A | — |  |
| batteryBankMini | batteryBankMini | Stock | — | N/A | — |  |
| batteryPack | batteryPack | Stock | — | N/A | — |  |
| FuelCell | FuelCell | Stock | — | N/A | — |  |
| FuelCellArray | FuelCellArray | Stock | — | N/A | — |  |
| ksp_r_largeBatteryPack | ksp.r.largeBatteryPack | Stock | — | N/A | — |  |
| largeSolarPanel | largeSolarPanel | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| LgRadialSolarPanel | LgRadialSolarPanel | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| rtg | rtg | Stock | — | N/A | — |  |
| solarPanelOX10C | solarPanelOX10C | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanelOX10L | solarPanelOX10L | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanels1 | solarPanels1 | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanels2 | solarPanels2 | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanels3 | solarPanels3 | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanels4 | solarPanels4 | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanels5 | solarPanels5 | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanelSP10C | solarPanelSP10C | Stock | ModuleDeployableSolarPanel | Full | Yes |  |
| solarPanelSP10L | solarPanelSP10L | Stock | ModuleDeployableSolarPanel | Full | Yes |  |

### Communication

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| commDish | commDish | Stock | ModuleDeployableAntenna | Full | Yes |  |
| HighGainAntenna | HighGainAntenna | Stock | ModuleDeployableAntenna | Full | Yes |  |
| HighGainAntenna5_v2 | HighGainAntenna5.v2 | Stock | ModuleDeployableAntenna | Full | Yes |  |
| longAntenna | longAntenna | Stock | ModuleDeployableAntenna | Full | Yes |  |
| mediumDishAntenna | mediumDishAntenna | Stock | ModuleDeployableAntenna | Full | Yes |  |
| RelayAntenna100 | RelayAntenna100 | Stock | — | N/A | — |  |
| RelayAntenna5 | RelayAntenna5 | Stock | — | N/A | — |  |
| RelayAntenna50 | RelayAntenna50 | Stock | — | N/A | — |  |
| SurfAntenna | SurfAntenna | Stock | — | N/A | — |  |

### Thermal

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| foldingRadLarge | foldingRadLarge | Stock | ModuleDeployableRadiator | Full | Yes |  |
| foldingRadMed | foldingRadMed | Stock | ModuleDeployableRadiator | Full | Yes |  |
| foldingRadSmall | foldingRadSmall | Stock | ModuleDeployableRadiator | Full | Yes |  |
| HeatShield0 | HeatShield0 | Stock | ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| HeatShield1 | HeatShield1 | Stock | ModuleJettison, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| HeatShield1p5 | HeatShield1p5 | Making History | ModuleJettison, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| HeatShield2 | HeatShield2 | Stock | ModuleJettison, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| HeatShield3 | HeatShield3 | Stock | ModuleJettison, ModuleColorChanger | Partial | Yes | ModuleColorChanger not tracked |
| InflatableHeatShield | InflatableHeatShield | Stock | ModuleAnimateGeneric, ModuleJettison | Full | Yes |  |
| radPanelEdge | radPanelEdge | Stock | — | N/A | — |  |
| radPanelLg | radPanelLg | Stock | — | N/A | — |  |
| radPanelSm | radPanelSm | Stock | — | N/A | — |  |

### Ground

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| GearFixed | GearFixed | Stock | ModuleWheelSuspension | Full | Yes |  |
| GearFree | GearFree | Stock | ModuleWheelSuspension, ModuleWheelSteering | Full | Yes |  |
| GearLarge | GearLarge | Stock | ModuleWheelSuspension, ModuleWheelDeployment, ModuleLight | Full | Yes |  |
| GearMedium | GearMedium | Stock | ModuleWheelSuspension, ModuleWheelDeployment, ModuleLight | Full | Yes |  |
| GearSmall | GearSmall | Stock | ModuleWheelSuspension, ModuleWheelSteering, ModuleWheelDeployment, ModuleLight | Full | Yes |  |
| landingLeg1 | landingLeg1 | Stock | ModuleWheelSuspension, ModuleWheelDeployment | Full | Yes |  |
| landingLeg1-2 | landingLeg1-2 | Stock | ModuleWheelSuspension, ModuleWheelDeployment | Full | Yes |  |
| miniLandingLeg | miniLandingLeg | Stock | ModuleWheelSuspension, ModuleWheelDeployment | Full | Yes |  |
| roverWheel1 | roverWheel1 | Stock | ModuleWheelSuspension, ModuleWheelSteering, ModuleWheelMotor | Full | Yes | Bug #29: may be missing on ghost |
| roverWheel2 | roverWheel2 | Stock | ModuleWheelSuspension, ModuleWheelSteering, ModuleWheelMotor | Full | Yes | Bug #29: may be missing on ghost |
| roverWheel3 | roverWheel3 | Stock | ModuleWheelSuspension, ModuleWheelMotorSteering | Full | Yes | Bug #29: may be missing on ghost |
| roverWheelM1-F | roverWheelM1-F | Making History | ModuleWheelSuspension, ModuleWheelSteering, ModuleWheelMotor, ModuleWheelDeployment | Full | Yes |  |
| SmallGearBay | SmallGearBay | Stock | ModuleWheelSuspension, ModuleWheelSteering, ModuleWheelDeployment, ModuleLight | Full | Yes | Bug #29: gear display issues |
| wheelMed | wheelMed | Stock | ModuleWheelSuspension, ModuleWheelSteering, ModuleWheelMotor | Full | Yes |  |

### Control

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| advSasModule | advSasModule | Stock | — | N/A | — |  |
| asasmodule1-2 | asasmodule1-2 | Stock | — | N/A | — |  |
| avionicsNoseCone | avionicsNoseCone | Stock | ModuleAnimateHeat | Full | Yes |  |
| linearRcs | linearRcs | Stock | ModuleRCSFX, FXModuleAnimateRCS | Partial | Yes | Bug #30: fires constantly; FXModuleAnimateRCS not tracked |
| RCSblock_01_small | RCSblock.01.small | Stock | ModuleRCSFX, FXModuleAnimateRCS | Partial | Yes | Bug #30: fires constantly; FXModuleAnimateRCS not tracked |
| RCSBlock_v2 | RCSBlock.v2 | Stock | ModuleRCSFX, FXModuleAnimateRCS | Partial | Yes | Bug #30: fires constantly; FXModuleAnimateRCS not tracked |
| RCSLinearSmall | RCSLinearSmall | Stock | ModuleRCSFX, FXModuleAnimateRCS | Partial | Yes | Bug #30: fires constantly; FXModuleAnimateRCS not tracked |
| sasModule | sasModule | Stock | — | N/A | — |  |
| vernierEngine | vernierEngine | Stock | ModuleRCSFX, FXModuleAnimateRCS | Partial | Yes | Bug #30: fires constantly; FXModuleAnimateRCS not tracked |

### Cargo

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| cargoContainer | cargoContainer | Stock | — | N/A | — |  |
| CargoStorageUnit | CargoStorageUnit | Stock | — | N/A | — |  |
| ConformalStorageUnit | ConformalStorageUnit | Stock | — | N/A | — |  |
| DeployedCentralStation | DeployedCentralStation | Breaking Ground | ModuleGroundExpControl, ModuleAnimationGroup | Full | Yes |  |
| DeployedGoExOb | DeployedGoExOb | Breaking Ground | ModuleGroundExperiment, ModuleAnimationGroup | Full | Yes |  |
| DeployedIONExp | DeployedIONExp | Breaking Ground | ModuleGroundExperiment, ModuleAnimationGroup | Full | Yes |  |
| DeployedRTG | DeployedRTG | Breaking Ground | ModuleGroundSciencePart, ModuleAnimationGroup | Full | Yes |  |
| DeployedSatDish | DeployedSatDish | Breaking Ground | ModuleGroundCommsPart, ModuleAnimationGroup | Full | Yes |  |
| DeployedSeismicSensor | DeployedSeismicSensor | Breaking Ground | ModuleGroundExperiment, ModuleAnimationGroup | Full | Yes |  |
| DeployedSolarPanel | DeployedSolarPanel | Breaking Ground | ModuleGroundSciencePart, ModuleAnimationGroup | Full | Yes |  |
| DeployedWeatherStn | DeployedWeatherStn | Breaking Ground | ModuleGroundExperiment, ModuleAnimationGroup | Full | Yes |  |
| evaChute | evaChute | Stock | — | N/A | — |  |
| evaCylinder | evaCylinder | Stock | — | N/A | — |  |
| evaJetpack | evaJetpack | Stock | — | N/A | — |  |
| evaRepairKit | evaRepairKit | Stock | — | N/A | — |  |
| evaScienceKit | evaScienceKit | Stock | — | N/A | — |  |
| groundAnchor | groundAnchor | Stock | ModuleAnimationGroup | Full | Yes |  |
| groundLight1 | groundLight1 | Stock | ModuleLight | Full | Yes |  |
| groundLight2 | groundLight2 | Stock | ModuleLight | Full | Yes |  |
| smallCargoContainer | smallCargoContainer | Stock | — | N/A | — |  |

### Robotics

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| controller1000 | controller1000 | Breaking Ground | — | N/A | — |  |
| hinge_01 | hinge.01 | Breaking Ground | ModuleRoboticServoHinge | Full | Yes |  |
| hinge_01_s | hinge.01.s | Breaking Ground | ModuleRoboticServoHinge | Full | Yes |  |
| hinge_03 | hinge.03 | Breaking Ground | ModuleRoboticServoHinge | Full | Yes |  |
| hinge_03_s | hinge.03.s | Breaking Ground | ModuleRoboticServoHinge | Full | Yes |  |
| hinge_04 | hinge.04 | Breaking Ground | ModuleRoboticServoHinge | Full | Yes |  |
| piston_01 | piston.01 | Breaking Ground | ModuleRoboticServoPiston | Full | Yes |  |
| piston_02 | piston.02 | Breaking Ground | ModuleRoboticServoPiston | Full | Yes |  |
| piston_03 | piston.03 | Breaking Ground | ModuleRoboticServoPiston | Full | Yes |  |
| piston_04 | piston.04 | Breaking Ground | ModuleRoboticServoPiston | Full | Yes |  |
| rotor_01 | rotor.01 | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| rotor_01s | rotor.01s | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| rotor_02 | rotor.02 | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| rotor_02s | rotor.02s | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| rotor_03 | rotor.03 | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| rotor_03s | rotor.03s | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| RotorEngine_02 | RotorEngine.02 | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| RotorEngine_03 | RotorEngine.03 | Breaking Ground | ModuleRoboticServoRotor | Full | Yes |  |
| rotoServo_00 | rotoServo.00 | Breaking Ground | ModuleRoboticRotationServo | Full | Yes |  |
| rotoServo_02 | rotoServo.02 | Breaking Ground | ModuleRoboticRotationServo | Full | Yes |  |
| rotoServo_03 | rotoServo.03 | Breaking Ground | ModuleRoboticRotationServo | Full | Yes |  |
| rotoServo_04 | rotoServo.04 | Breaking Ground | ModuleRoboticRotationServo | Full | Yes |  |

### EVA Kerbals

Prebuilt EVA parts, not available in the VAB/SPH editor.

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| kerbalEVA | kerbalEVA | Stock | — | N/A | Yes | BG overrides cfg but base part is stock |
| kerbalEVAfemale | kerbalEVAfemale | Stock | — | N/A | — | BG overrides cfg but base part is stock |
| kerbalEVAfemaleFuture | kerbalEVAfemaleFuture | Breaking Ground | KerbalEVA, ModuleEvaChute, ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| kerbalEVAfemaleVintage | kerbalEVAfemaleVintage | Making History | — | N/A | — |  |
| kerbalEVAFuture | kerbalEVAFuture | Breaking Ground | KerbalEVA, ModuleEvaChute, ModuleColorChanger | Partial | — | ModuleColorChanger not tracked |
| kerbalEVASlimSuit | kerbalEVASlimSuit | Stock | — | N/A | — | BG overrides cfg but base part is stock |
| kerbalEVASlimSuitFemale | kerbalEVASlimSuitFemale | Stock | — | N/A | — | BG overrides cfg but base part is stock |
| kerbalEVAVintage | kerbalEVAVintage | Making History | — | N/A | — | BG also overrides this cfg |

### Uncategorized (category=none)

These parts have `category = none` in their cfg. Most are deprecated v1 parts superseded
by v2 equivalents, plus special objects (flag, asteroids, comets). They are hidden from
the editor part list but still exist at runtime.

| Part (cfg) | Runtime Name | Source | Visual Modules | Support | Showcase | Notes |
|------------|-------------|--------|----------------|---------|----------|-------|
| flag | flag | Stock | — | N/A | — | Planted flag object |
| HighGainAntenna5 | HighGainAntenna5 | Stock | ModuleDeployableAntenna | Full | Yes | Deprecated, superseded by HighGainAntenna5_v2 |
| liquidEngine | liquidEngine | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Deprecated v1 LV-T30 |
| liquidEngine2 | liquidEngine2 | Stock | ModuleEngines, ModuleJettison, FXModuleAnimateThrottle | Partial | Yes | Deprecated v1 LV-T45 |
| mk2LanderCabin | mk2LanderCabin | Stock | ModuleColorChanger | Partial | — | Deprecated v1; ModuleColorChanger not tracked |
| PotatoComet | PotatoComet | Stock | — | N/A | — | Procedural comet object |
| PotatoRoid | PotatoRoid | Stock | — | N/A | — | Procedural asteroid object |
| probeCoreHex | probeCoreHex | Stock | — | N/A | — | Deprecated v1 HECS |
| rocketNoseCone_v2 | rocketNoseCone.v2 | Stock | — | N/A | — | Deprecated (misleading _v2 name) |
| roverBody | roverBody | Stock | — | N/A | — | Deprecated v1 rover body |
| Size2LFB | Size2LFB | Stock | ModuleEnginesFX, FXModuleAnimateThrottle | Partial | — | Deprecated v1 Twin-Boar |
| smallRadialEngine | smallRadialEngine | Stock | ModuleEnginesFX | Full | Yes | Deprecated v1 Spider |
| spotLight1 | spotLight1 | Stock | ModuleLight | Full | Yes | Deprecated v1 spotlight |
| spotLight2 | spotLight2 | Stock | ModuleLight | Full | Yes | Deprecated v1 spotlight |

## Coverage Gaps

### Parts with Visual Modules but No Showcase Entry

These parts have dynamic visual modules that Parsek handles but have not been visually validated via showcase recordings.

| Part (cfg) | Runtime Name | Source | Visual Modules | Support |
|------------|-------------|--------|----------------|---------|
| HeatShield0 | HeatShield0 | Stock | ModuleColorChanger | Partial |
| ISRU | ISRU | Stock | ModuleAnimationGroup | Full |
| Large_Crewed_Lab | Large.Crewed.Lab | Stock | ModuleColorChanger | Partial |
| LaunchEscapeSystem | LaunchEscapeSystem | Stock | ModuleEnginesFX | Full |
| MEMLander | MEMLander | Making History | ModuleColorChanger, ModuleRCSFX | Partial |
| MK1CrewCabin | MK1CrewCabin | Stock | ModuleColorChanger | Partial |
| Mark1Cockpit | Mark1Cockpit | Stock | ModuleColorChanger | Partial |
| Mark2Cockpit | Mark2Cockpit | Stock | ModuleColorChanger | Partial |
| Mk2Pod | Mk2Pod | Making History | ModuleColorChanger | Partial |
| OrbitalScanner | OrbitalScanner | Stock | ModuleAnimationGroup | Full |
| Size1p5_Tank_05 | Size1p5.Tank.05 | Making History | ModuleEngines | Full |
| Size2LFB | Size2LFB | Stock | FXModuleAnimateThrottle, ModuleEnginesFX | Partial |
| crewCabin | crewCabin | Stock | ModuleColorChanger | Partial |
| cupola | cupola | Stock | ModuleColorChanger | Partial |
| dockingPort2 | dockingPort2 | Stock | ModuleColorChanger, ModuleDockingNode | Partial |
| dockingPort3 | dockingPort3 | Stock | ModuleDockingNode | Full |
| dockingPortLarge | dockingPortLarge | Stock | ModuleColorChanger, ModuleDockingNode | Partial |
| fireworksLauncherBig | fireworksLauncherBig | Stock | ModulePartFirework | Partial |
| fireworksLauncherSmall | fireworksLauncherSmall | Stock | ModulePartFirework | Partial |
| kerbalEVAFuture | kerbalEVAFuture | Breaking Ground | KerbalEVA, ModuleEvaChute, ModuleColorChanger | Partial |
| kerbalEVAfemaleFuture | kerbalEVAfemaleFuture | Breaking Ground | KerbalEVA, ModuleEvaChute, ModuleColorChanger | Partial |
| kv1Pod | kv1Pod | Making History | ModuleColorChanger | Partial |
| kv2Pod | kv2Pod | Making History | ModuleColorChanger | Partial |
| kv3Pod | kv3Pod | Making History | ModuleColorChanger | Partial |
| landerCabinSmall | landerCabinSmall | Stock | ModuleColorChanger | Partial |
| mk1-3pod | mk1-3pod | Stock | ModuleColorChanger, ModuleRCSFX | Partial |
| mk1pod_v2 | mk1pod.v2 | Stock | ModuleColorChanger | Partial |
| mk2Cockpit_Inline | mk2Cockpit.Inline | Stock | ModuleColorChanger | Partial |
| mk2Cockpit_Standard | mk2Cockpit.Standard | Stock | ModuleColorChanger | Partial |
| mk2CrewCabin | mk2CrewCabin | Stock | ModuleColorChanger | Partial |
| mk2LanderCabin | mk2LanderCabin | Stock | ModuleColorChanger | Partial |
| mk3Cockpit_Shuttle | mk3Cockpit.Shuttle | Stock | ModuleColorChanger | Partial |
| mk3CrewCabin | mk3CrewCabin | Stock | ModuleColorChanger | Partial |

### Parts with Unsupported Visual Modules (No Showcase Needed)

Parts where the only visual gap is ModuleColorChanger (cabin lights), FXModuleAnimateThrottle (nozzle glow), or FXModuleAnimateRCS — these are cosmetic and don't need showcase validation, but are listed for completeness.

| Part (cfg) | Runtime Name | Source | Unsupported Modules |
|------------|-------------|--------|---------------------|
| Clydesdale | Clydesdale | Stock | FXModuleAnimateThrottle |
| HeatShield1 | HeatShield1 | Stock | ModuleColorChanger |
| HeatShield1p5 | HeatShield1p5 | Making History | ModuleColorChanger |
| HeatShield2 | HeatShield2 | Stock | ModuleColorChanger |
| HeatShield3 | HeatShield3 | Stock | ModuleColorChanger |
| MassiveBooster | MassiveBooster | Stock | FXModuleAnimateThrottle |
| Mite | Mite | Stock | FXModuleAnimateThrottle |
| Pollux | Pollux | Making History | FXModuleAnimateThrottle |
| RAPIER | RAPIER | Stock | FXModuleAnimateThrottle |
| RCSBlock_v2 | RCSBlock.v2 | Stock | FXModuleAnimateRCS |
| RCSLinearSmall | RCSLinearSmall | Stock | FXModuleAnimateRCS |
| RCSblock_01_small | RCSblock.01.small | Stock | FXModuleAnimateRCS |
| SSME | SSME | Stock | FXModuleAnimateThrottle |
| Shrimp | Shrimp | Stock | FXModuleAnimateThrottle |
| Size2LFB_v2 | Size2LFB.v2 | Stock | FXModuleAnimateThrottle |
| Size3AdvancedEngine | Size3AdvancedEngine | Stock | FXModuleAnimateThrottle |
| Size3EngineCluster | Size3EngineCluster | Stock | FXModuleAnimateThrottle |
| Thoroughbred | Thoroughbred | Stock | FXModuleAnimateThrottle |
| dockingPort1 | dockingPort1 | Stock | ModuleColorChanger |
| dockingPortLateral | dockingPortLateral | Stock | ModuleColorChanger |
| engineLargeSkipper_v2 | engineLargeSkipper.v2 | Stock | FXModuleAnimateThrottle |
| ionEngine | ionEngine | Stock | FXModuleAnimateThrottle |
| linearRcs | linearRcs | Stock | FXModuleAnimateRCS |
| liquidEngine | liquidEngine | Stock | FXModuleAnimateThrottle |
| liquidEngine2 | liquidEngine2 | Stock | FXModuleAnimateThrottle |
| liquidEngine2-2_v2 | liquidEngine2-2.v2 | Stock | FXModuleAnimateThrottle |
| liquidEngine2_v2 | liquidEngine2.v2 | Stock | FXModuleAnimateThrottle |
| liquidEngine3_v2 | liquidEngine3.v2 | Stock | FXModuleAnimateThrottle |
| liquidEngineMainsail_v2 | liquidEngineMainsail.v2 | Stock | FXModuleAnimateThrottle |
| liquidEngineMini_v2 | liquidEngineMini.v2 | Stock | FXModuleAnimateThrottle |
| liquidEngine_v2 | liquidEngine.v2 | Stock | FXModuleAnimateThrottle |
| microEngine_v2 | microEngine.v2 | Stock | FXModuleAnimateThrottle |
| mk2DockingPort | mk2DockingPort | Stock | ModuleColorChanger |
| mk2LanderCabin_v2 | mk2LanderCabin.v2 | Stock | ModuleColorChanger |
| omsEngine | omsEngine | Stock | FXModuleAnimateThrottle |
| radialEngineMini_v2 | radialEngineMini.v2 | Stock | FXModuleAnimateThrottle |
| radialLiquidEngine1-2 | radialLiquidEngine1-2 | Stock | FXModuleAnimateThrottle |
| smallRadialEngine_v2 | smallRadialEngine.v2 | Stock | FXModuleAnimateThrottle |
| solidBooster1-1 | solidBooster1-1 | Stock | FXModuleAnimateThrottle |
| solidBooster_v2 | solidBooster.v2 | Stock | FXModuleAnimateThrottle |
| toroidalAerospike | toroidalAerospike | Stock | FXModuleAnimateThrottle |
| turboFanEngine | turboFanEngine | Stock | FXModuleAnimateThrottle |
| turboFanSize2 | turboFanSize2 | Stock | FXModuleAnimateThrottle |
| turboJet | turboJet | Stock | FXModuleAnimateThrottle |
| vernierEngine | vernierEngine | Stock | FXModuleAnimateRCS |

### Module Types Not Yet Supported

| Module | Part Count | Description | Priority |
|--------|-----------|-------------|----------|
| ModuleColorChanger | 33 | Cabin interior lights, ablator color | Low — cosmetic only |
| ~~FXModuleAnimateThrottle~~ | ~~33~~ | ~~Engine nozzle glow animation~~ | ~~Fixed (bug #38)~~ |
| FXModuleAnimateRCS | 5 | RCS thruster response animation | Low — subtle effect |
| ModulePartFirework | 2 | Firework launch effects | None — novelty |
| ModuleControlSurface (continuous) | 24 | Continuous deflection angle — won't implement (binary deploy/retract is sufficient) | Closed |
| ModuleAnimateHeat (continuous) | 16 | Continuous thermal intensity (binary hot/cold IS supported) | Priority 2 |
| ModulePartVariants (TEXTURE/MATERIAL) | many | Variant texture/color not applied to ghost (bug #37) | Medium |

### DLC Parts Summary

| DLC | Total Parts | With Visual Modules | Showcased | Coverage |
|-----|-------------|--------------------|-----------|---------|
| Making History | 71 | 27 | 21 | 78% |
| Breaking Ground | 54 | 43 | 41 | 95% |

## Roadmap

Consolidated from `next-parts-event-support-priority.md` (2026-02-22, now obsolete).

### Supported Module Baseline (as of 2026-03-15)

Recording/playback handles 17 per-physics-frame polling checks plus 4 event-driven sources.
212 parts have been visually validated via showcase recordings. Full list of supported modules:
parachute, jettison/fairing, deployable (solar/antenna/radiator), ladder, animation-group,
standalone animate-generic, lights/blink, gear deployment, wheel/leg dynamics
(suspension/steering/motor), engine + RCS particle FX, robotics motion, aero surface
deploy/retract, control surface deploy/retract (binary), robot arm scanner, animate-heat
(binary hot/cold), inventory placement/removal, docking chain boundaries.

### Priority 1: ModuleControlSurface deploy/retract (binary only)

Binary deploy/retract endpoints are already supported and showcased for all 24 control surface
parts. This records the player's toggle action (e.g., flaps locked extended) — same model as
landing gear. Continuous in-flight deflection angle will NOT be tracked.

**Design decision:** No continuous deflection sampling. Rationale: performance and storage
budget is better spent on vehicle trajectory fidelity than per-part deflection angles. The
visual difference between a static deployed flap and a continuously-animated one is marginal
in replay. This matches the existing binary approach for all other deployable modules.

**Note:** 9 of the 24 parts are propeller/helicopter blades (largeFanBlade, largeHeliBlade,
largePropeller, mediumFanBlade, mediumHeliBlade, mediumPropeller, smallFanBlade,
smallHeliBlade, smallPropeller). Their "deploy" is blade pitch — the visual change is
negligible. The 15 remaining parts (elevons, canards, rudders, winglets) show a visible
flap position change on deploy/retract.

**Status:** Already implemented — binary deploy/retract works today. No further work needed.
This item is effectively complete.

### Priority 2: ModuleAnimateHeat continuous intensity

Binary hot/cold endpoint transitions are supported and showcased for all 16 thermal-animation
parts. The gap is continuous heat scalar — the ghost jumps between cold and hot states instead
of smoothly ramping.

**Work required:** Continuous heat-scalar sampling/playback, material emission lerp at
playback time.

### ~~Priority 3: FXModuleAnimateThrottle (engine nozzle glow) — bug #38~~ DONE

Fixed. Ghost builder now detects `FXModuleAnimateThrottle` as a fallback heat source and
builds `HeatGhostInfo` from its animation. Engine events drive the heat state (binary
hot/cold). Name-based heuristic disambiguates multi-instance parts. 33 engine parts now
have correct nozzle glow behavior on ghost playback.

### Priority 4: ModulePartVariants TEXTURE/MATERIAL rules (bug #37)

Ghost parts with non-default texture variants show the prefab's default texture. Only
GAMEOBJECTS geometry rules are currently applied.

**Work required:** Read TEXTURE sub-nodes from selected VARIANT config, apply
`renderer.material.SetTexture` overrides during ghost build. Similarly for MATERIAL rules.

### Lower Priority

- **ModuleColorChanger** (33 parts) — cabin interior lights. Low visual impact since these
  are mostly visible through small windows. Would need color animation sampling.
- **FXModuleAnimateRCS** (5 parts) — subtle thruster housing animation on RCS fire. Very
  low visual impact.
- **ModulePartFirework** (2 parts) — novelty firework launchers. Not worth implementing.

### Showcase Gaps (parts needing validation)

6 non-cosmetic parts with supported visual modules that lack showcase entries:

| Part | Module | Notes |
|------|--------|-------|
| LaunchEscapeSystem | ModuleEnginesFX | Bug #32 — plume FX needs in-game verification |
| ISRU | ModuleAnimationGroup | Intentionally excluded — no visible deploy change |
| OrbitalScanner | ModuleAnimationGroup | Intentionally excluded — no visible deploy change |
| Size1p5_Tank_05 | ModuleEngines | MH engine-tank hybrid, needs showcase |
| Size2LFB | ModuleEnginesFX | Deprecated v1 Twin-Boar, low priority |
| dockingPort3 | ModuleDockingNode | Chain boundary only, no visual event |

The remaining 27 parts without showcase entries are command pods/crew cabins whose only
visual gap is ModuleColorChanger (cabin lights) — these don't need showcase validation
until ModuleColorChanger support is implemented.
