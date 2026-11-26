# Flight profiles (harness lane catalog)

GENERATED FILE - do not hand-edit. Regenerate with `python harness/tools/lane_catalog.py` after changing a lane spec (`harness/scenarios/*.toml`), `harness/coverage/registry.toml`, an `[InGameTest]` category, or a lane's row in `docs/dev/autotest-status.md`; `harness/lib/test_lanecatalog.py` reds while this file is stale.

One row per harness lane, derived from its spec, for finding gaps in what the lanes fly. Status is QUOTED from `docs/dev/autotest-status.md`, which stays the status authority; the hand-written design catalog is `docs/dev/automated-testing-scenario-catalog.md`. Derivation rules: `harness/lib/lanecatalog.py` (module docstring). Two limits to keep in mind: bodies are a NAME scan (a lane that flies at Kerbin without naming it derives no body), and timeline operations come from seam verbs only (an autopilot mission's own kRPC warps are not counted; the first `LoadGame` is the boot, not a reload). `python harness/tools/lane_catalog.py --json -` prints the same data as JSON.

## Overview

| what | lanes |
|---|---|
| lanes | 706 |
| tier daily | 34 |
| tier nightly | 313 |
| tier operator | 354 |
| tier longhaul | 5 |
| profile stock-minimal | 638 |
| profile stock-tree | 56 |
| profile modded-compat | 10 |
| profile modded-restock | 2 |
| autopilot (mission) lanes | 165 |
| seam-only lanes | 541 |
| LIVE-PROVEN date in status row | 588 |
| no LIVE-PROVEN date | 118 |
| no status row | 0 |
| expectedFail armed | 0 |

## Never used by any lane

- Seam verbs (of 65 implemented): none
- Dimension values (2 of 251 registry values): D1 `sub-2-point-drop`, D13 `pid-dedup`
- In-game test categories (4 of 153 declared; a lane running `RunTests` with no category runs them all, see `(all)` below): `GuiMock`, `LadderGhost`, `LiveGhostReentryFx`, `PartParity`

## Lanes per family

| family | lanes | daily | nightly | operator | longhaul | autopilot | live-proven |
|---|---|---|---|---|---|---|---|
| AH | 1 | - | 1 | - | - | - | 1 |
| AIR | 3 | - | - | 3 | - | 3 | 3 |
| AJ | 1 | - | - | 1 | - | - | 1 |
| AP | 1 | - | 1 | - | - | - | 1 |
| ASR | 9 | 4 | 5 | - | - | 5 | 9 |
| AST | 4 | - | 4 | - | - | 4 | 4 |
| AT | 1 | - | 1 | - | - | - | 1 |
| B | 41 | 1 | 12 | 23 | 5 | 40 | 31 |
| BAY | 1 | - | - | 1 | - | 1 | 1 |
| BDOCK | 2 | - | 1 | 1 | - | 2 | 2 |
| BOAT | 1 | - | - | 1 | - | 1 | 1 |
| CA | 1 | - | 1 | - | - | 1 | 1 |
| CI | 11 | 1 | 7 | 3 | - | 2 | 10 |
| CL | 2 | - | 1 | 1 | - | 2 | 2 |
| CLK | 1 | - | 1 | - | - | - | 1 |
| CN | 6 | - | 4 | 2 | - | - | 6 |
| COM | 1 | - | 1 | - | - | 1 | 1 |
| CPR | 1 | - | - | 1 | - | - | 1 |
| CS | 13 | - | - | 13 | - | 8 | 13 |
| CT | 3 | - | - | 3 | - | - | 3 |
| CTW | 1 | - | - | 1 | - | - | 1 |
| CX | 6 | - | 6 | - | - | - | 6 |
| DV | 1 | - | 1 | - | - | 1 | 1 |
| EVA | 17 | 1 | 15 | 1 | - | 1 | 16 |
| EX | 2 | - | 2 | - | - | - | 2 |
| FAR | 6 | - | - | 6 | - | - | 6 |
| FFC | 1 | - | - | 1 | - | - | 1 |
| FGV | 1 | - | - | 1 | - | - | 1 |
| FLG | 2 | - | 2 | - | - | - | 2 |
| FLY | 5 | - | 5 | - | - | - | 5 |
| FORGE | 10 | - | - | 10 | - | 9 | 5 |
| FXR | 1 | - | - | 1 | - | - | 1 |
| GC | 2 | - | 1 | 1 | - | - | 2 |
| GM | 1 | - | - | 1 | - | - | 1 |
| GPOS | 2 | - | - | 2 | - | 2 | 2 |
| GS | 11 | - | 4 | 7 | - | 10 | 7 |
| GUI | 28 | - | - | 28 | - | - | 4 |
| H | 75 | 5 | 69 | 1 | - | - | 60 |
| HC | 2 | - | 2 | - | - | 1 | 2 |
| HV | 2 | - | 2 | - | - | - | 2 |
| IR | 8 | - | 7 | 1 | - | - | 8 |
| KB | 5 | 1 | 4 | - | - | - | 5 |
| KD | 1 | - | - | 1 | - | 1 | 1 |
| KSS | 2 | - | - | 2 | - | 1 | 2 |
| L | 22 | 6 | 13 | 3 | - | 6 | 14 |
| LAB | 18 | - | 1 | 17 | - | 1 | 18 |
| LABX | 1 | - | - | 1 | - | 1 | 1 |
| LAY | 1 | - | - | 1 | - | 1 | 1 |
| LF | 3 | - | 2 | 1 | - | - | 3 |
| LS | 1 | - | 1 | - | - | - | 1 |
| LT | 5 | - | 5 | - | - | - | 4 |
| LX | 2 | - | - | 2 | - | - | 2 |
| M | 3 | 2 | 1 | - | - | 1 | 3 |
| MC | 7 | - | 3 | 4 | - | 1 | 6 |
| MINE | 14 | - | 14 | - | - | - | 14 |
| MS | 1 | - | 1 | - | - | - | 1 |
| OC | 1 | - | 1 | - | - | - | 1 |
| OSC | 1 | - | 1 | - | - | - | 1 |
| PAD | 1 | - | 1 | - | - | - | 1 |
| PADG | 1 | - | 1 | - | - | - | 1 |
| PADX | 3 | - | - | 3 | - | - | 3 |
| PBR | 1 | - | - | 1 | - | - | 1 |
| PP | 10 | - | - | 10 | - | - | 10 |
| PPB | 3 | - | - | 3 | - | - | 3 |
| PSP | 1 | - | - | 1 | - | - | 1 |
| PWR | 3 | - | 3 | - | - | 3 | 3 |
| QL | 23 | - | 23 | - | - | 10 | 23 |
| QR | 1 | - | 1 | - | - | - | 1 |
| QTM | 1 | - | 1 | - | - | - | 1 |
| QWS | 1 | - | 1 | - | - | - | 1 |
| R | 3 | - | 2 | 1 | - | 1 | 3 |
| RB | 5 | - | 2 | 3 | - | 5 | 3 |
| RC | 3 | - | 2 | 1 | - | - | 3 |
| RCN | 1 | - | 1 | - | - | - | 1 |
| REV | 1 | - | - | 1 | - | - | 1 |
| RF | 32 | - | 2 | 30 | - | 15 | 30 |
| RFK | 2 | - | - | 2 | - | - | 2 |
| RFN | 2 | - | - | 2 | - | - | 2 |
| RFP | 1 | - | 1 | - | - | - | 1 |
| RFQ | 2 | - | 2 | - | - | - | 2 |
| RH | 1 | - | - | 1 | - | - | - |
| RL | 1 | - | 1 | - | - | - | 1 |
| RR | 1 | - | 1 | - | - | - | 1 |
| RS | 1 | - | 1 | - | - | - | 1 |
| RSC | 3 | - | 3 | - | - | - | 3 |
| RSF | 1 | - | 1 | - | - | - | 1 |
| RSQ | 4 | - | - | 4 | - | - | 4 |
| RVC | 1 | - | - | 1 | - | - | 1 |
| RVR | 19 | 1 | 18 | - | - | - | 18 |
| RW | 25 | - | - | 25 | - | 1 | 25 |
| S | 20 | 12 | 3 | 5 | - | - | 14 |
| SB | 1 | - | 1 | - | - | - | 1 |
| SCI | 1 | - | - | 1 | - | 1 | 1 |
| SD | 1 | - | - | 1 | - | 1 | 1 |
| SE | 1 | - | 1 | - | - | - | 1 |
| SEAT | 4 | - | 3 | 1 | - | - | 4 |
| SH | 1 | - | - | 1 | - | - | 1 |
| SLM | 3 | - | - | 3 | - | - | 3 |
| SN | 1 | - | 1 | - | - | - | 1 |
| SOI | 3 | - | - | 3 | - | 2 | 3 |
| SPW | 11 | - | - | 11 | - | - | 11 |
| SRV | 3 | - | - | 3 | - | - | 3 |
| SS | 1 | - | 1 | - | - | - | - |
| SSC | 1 | - | - | 1 | - | - | 1 |
| ST | 4 | - | 4 | - | - | 1 | 4 |
| STR | 4 | - | - | 4 | - | 2 | 4 |
| SUB | 3 | - | - | 3 | - | 3 | 3 |
| SURV | 2 | - | 2 | - | - | - | 2 |
| SWC | 2 | - | - | 2 | - | - | 2 |
| SWS | 1 | - | - | 1 | - | - | 1 |
| TL | 1 | - | 1 | - | - | - | 1 |
| TOUR | 1 | - | 1 | - | - | - | 1 |
| TP | 9 | - | - | 9 | - | - | 9 |
| TSS | 3 | - | - | 3 | - | - | 3 |
| UIS | 1 | - | 1 | - | - | - | 1 |
| V | 67 | - | 7 | 60 | - | 6 | 34 |
| VB | 1 | - | 1 | - | - | - | 1 |
| VN | 14 | - | 14 | - | - | 6 | 14 |
| W | 1 | - | - | 1 | - | - | - |
| WDK | 1 | - | - | 1 | - | - | - |
| WF | 1 | - | - | 1 | - | - | 1 |
| WR | 1 | - | - | 1 | - | 1 | 1 |
| WTT | 2 | - | - | 2 | - | - | 2 |
| ZF | 1 | - | 1 | - | - | - | 1 |

## Lanes per host save

The host is the leaf of `fixture.saveTemplate`. 154 hosts serve 706 lanes.

| host save | lanes | families |
|---|---|---|
| gloops-airshow | 92 | H 40, S 12, QL 7, CI 5, CN 5, MC 4, VN 3, EVA 2, FLG 2, GUI 2, LT 2 +7 |
| eva2-lko-crewed | 44 | PP 7, SPW 7, S 6, CI 3, EVA 3, FLY 3, RSC 2, SWC 2, VN 2, ASR 1 +8 |
| gs1-two-stage-pad | 41 | GS 8, VN 7, RF 6, ASR 4, QL 4, AIR 3, H 3, RB 2, BAY 1, CA 1 +2 |
| cs-career-mun4 | 32 | RW 20, RSQ 4, RFK 2, RFN 2, CS 1, REV 1, SWS 1, WTT 1 |
| rover-route-recorded | 23 | RVR 7, IR 6, H 5, GUI 2, LX 1, SSC 1, V 1 |
| b2-lko-craft | 20 | B 10, PWR 3, H 2, DV 1, KSS 1, R 1, RF 1, V 1 |
| bdock-recorded | 19 | RF 5, GUI 4, CI 2, H 2, BDOCK 1, MS 1, RC 1, RH 1, ST 1, WDK 1 |
| career-pad-craft | 19 | QL 6, PADX 3, CL 2, L 2, CT 1, FFC 1, H 1, KD 1, R 1, STR 1 |
| rover-relay-c-recorded | 17 | RVR 10, GUI 3, IR 1, RCN 1, RS 1, RSF 1 |
| fresh-career | 14 | L 5, AH 1, B 1, CS 1, GUI 1, H 1, HV 1, LAB 1, R 1, SN 1 |
| kerbin-splashdown-recorded | 13 | EVA 4, V 3, CX 2, FLY 1, RR 1, SE 1, W 1 |
| gs2-orbital-stack | 11 | RF 3, GS 2, H 2, SURV 2, PAD 1, ST 1 |
| bdock-forge-base | 10 | FORGE 9, LABX 1 |
| b1-pad-craft | 9 | PP 3, B 1, EVA 1, GPOS 1, GUI 1, H 1, M 1 |
| pad-runway-pair | 9 | SLM 3, LF 2, EX 1, LS 1, OC 1, RL 1 |
| career-earned-pad | 8 | L 4, CLK 1, H 1, LT 1, ST 1 |
| jool-orbit-recorded | 8 | B 6, V 2 |
| lab-lko-science | 8 | LAB 7, QL 1 |
| mun-park-kerbalx | 8 | MC 2, SOI 2, B 1, SPW 1, TSS 1, VB 1 |
| duna-direct-recorded | 7 | V 5, B 1, GUI 1 |
| miner-rover-watched | 7 | MINE 7 |
| career-earned-ksc | 6 | GUI 2, H 1, KB 1, QL 1, ST 1 |
| eva-construct-pad | 6 | EVA 5, RC 1 |
| fresh-sandbox | 6 | M 2, GUI 1, L 1, LT 1, S 1 |
| interbody-route-recorded | 6 | GUI 2, IR 1, QL 1, TP 1, V 1 |
| jool-park-nerv | 6 | B 5, S 1 |
| lab-lko-seeded-transmit | 6 | LAB 3, RF 2, RC 1 |
| mun-orbit-recorded | 6 | TP 2, V 2, GUI 1, H 1 |
| b18-dres-pad | 5 | B 5 |
| career-gs1-two-stage-pad | 5 | RF 2, L 1, QL 1, STR 1 |
| depot-route-recorded | 5 | H 1, LT 1, TP 1, V 1, VN 1 |
| duna-park-recorded | 5 | TP 2, AT 1, FAR 1, V 1 |
| laythe-park-nerv | 5 | B 3, LAY 1, SPW 1 |
| logi-cargo-pad | 5 | H 4, HV 1 |
| miner-rover-landed | 5 | MINE 3, SB 1, TSS 1 |
| minmus-orbit-recorded | 5 | V 3, AP 1, TP 1 |
| refly-autopilot-recorded | 5 | RF 5 |
| bdock-second-dock-recorded | 4 | CX 2, CI 1, SD 1 |
| c1-gui | 4 | GUI 4 |
| career-science-pad | 4 | RB 3, L 1 |
| cs-career-new | 4 | CPR 1, CS 1, WR 1, WTT 1 |
| duna-ike-recorded | 4 | FAR 2, V 2 |
| eve-orbit-recorded | 4 | V 3, B 1 |
| fresh-science | 4 | CS 1, GUI 1, L 1, SH 1 |
| jool-laythe-recorded | 4 | V 2, B 1, FAR 1 |
| kerbin-return-recorded | 4 | V 3, FLY 1 |
| mun-landing-recorded | 4 | V 2, EVA 1, TP 1 |
| refly-a-recorded | 4 | RF 2, GUI 1, VN 1 |
| seat-rover-pad | 4 | SEAT 2, CX 1, PADG 1 |
| career-pad-craft-purchase | 3 | RW 2, STR 1 |
| duna-park-probe | 3 | B 1, CN 1, SPW 1 |
| eve-gilly-recorded | 3 | V 2, FAR 1 |
| lab-pad-seeded | 3 | LAB 3 |
| psub-splashed | 3 | SUB 3 |
| refly-split-crewed-recorded | 3 | RF 2, QL 1 |
| stock-screen-census | 3 | KB 2, GUI 1 |
| vall-transfer-recorded | 3 | V 2, TP 1 |
| ast-klaw-lko | 2 | AST 2 |
| b17-duna-pad | 2 | B 1, V 1 |
| career-contract-pad | 2 | GUI 1, L 1 |
| career-gs1-leadership-pad | 2 | QL 1, STR 1 |
| career-pad-craft-hard | 2 | HC 1, L 1 |
| career-same-name-pad | 2 | L 2 |
| cs-career-mun1 | 2 | CS 1, RW 1 |
| cs-career-mun2 | 2 | CS 1, RW 1 |
| cs-career-mun3 | 2 | CS 1, CT 1 |
| cs-science-new | 2 | CS 1, SCI 1 |
| dres-orbit-recorded | 2 | V 2 |
| duna-one-recorded | 2 | H 1, V 1 |
| eeloo-orbit-recorded | 2 | V 2 |
| eva3-pad-3crew | 2 | EVA 1, H 1 |
| gilly-orbit-recorded | 2 | V 2 |
| gloops-airshow-career | 2 | H 1, WF 1 |
| gloops-airshow-hard | 2 | RF 2 |
| gs1-armed-chute-drop | 2 | ASR 1, LF 1 |
| ike-orbit-recorded | 2 | V 2 |
| jool-return-recorded | 2 | V 2 |
| lab-pad-career | 2 | LAB 1, RW 1 |
| laythe-orbit-recorded | 2 | V 2 |
| moho-orbit-recorded | 2 | V 2 |
| mun-minmus-recorded | 2 | V 2 |
| pbr-break-rover | 2 | PBR 1, PSP 1 |
| refly-split-crewed-merged | 2 | FXR 1, RF 1 |
| rover-relay-c-recorded-career | 2 | H 2 |
| rover-relay-recorded | 2 | RVR 2 |
| rover-route-recorded-career | 2 | H 1, LX 1 |
| seat-pad | 2 | SEAT 2 |
| tylo-park-nerv | 2 | B 1, SPW 1 |
| ast-miner-lko | 1 | AST 1 |
| ast-mothership-lko | 1 | AST 1 |
| bdock-recorded-career | 1 | H 1 |
| bdock-station-pad | 1 | BDOCK 1 |
| bop-orbit-recorded | 1 | V 1 |
| career-earned-ksc-retired-milestone | 1 | TL 1 |
| career-earned-pad-oldgen | 1 | GC 1 |
| career-idless-same-name-pad | 1 | L 1 |
| career-new-goal-twin | 1 | CTW 1 |
| career-pad-craft-zero-funds | 1 | ZF 1 |
| career-requirements-loss-pad | 1 | L 1 |
| career-science-pad-hard | 1 | HC 1 |
| coalescer-pad | 1 | H 1 |
| com-klaw-lko | 1 | COM 1 |
| cs-career-contracts | 1 | CS 1 |
| cs-career-mc1 | 1 | CS 1 |
| cs-career-mun4-vessel-repair | 1 | CT 1 |
| cs-career-orbit | 1 | CS 1 |
| cs-career-ts1 | 1 | CS 1 |
| cs-science-flea | 1 | CS 1 |
| depot-route-recorded-career | 1 | H 1 |
| duna-ike-recorded-career | 1 | FAR 1 |
| eve-park-kerbalx | 1 | B 1 |
| gm-scenario-modes | 1 | GM 1 |
| graze-high-probe-stack | 1 | ASR 1 |
| graze-low-probe-stack | 1 | ASR 1 |
| hitchhiker-pad | 1 | CX 1 |
| jool-bop-recorded | 1 | V 1 |
| jool-laythe-bop-recorded | 1 | V 1 |
| jool-pol-recorded | 1 | V 1 |
| jool-tour-recorded | 1 | V 1 |
| jool-tylo-recorded | 1 | V 1 |
| jool-vall-recorded | 1 | V 1 |
| k2-held-kerbal-pad | 1 | KB 1 |
| kerbin-escape-probe-stack | 1 | ASR 1 |
| lab-lko-relay | 1 | LAB 1 |
| lab-lko-seeded | 1 | LAB 1 |
| lab-lko-tender-rcs | 1 | LAB 1 |
| laythe-tylo-recorded | 1 | V 1 |
| logi-cargo-career | 1 | H 1 |
| mallard-splashed | 1 | BOAT 1 |
| miner-rover-depot | 1 | MINE 1 |
| miner-rover-full | 1 | MINE 1 |
| miner-rover-relay | 1 | MINE 1 |
| miner-rover-starved | 1 | MINE 1 |
| minmus-mun-recorded | 1 | V 1 |
| minmus-park-kerbalx | 1 | B 1 |
| mun-escape-probe-stack | 1 | SOI 1 |
| mun-orbit-recorded-v3 | 1 | GS 1 |
| pol-orbit-recorded | 1 | V 1 |
| preparsek-brandnew-career | 1 | PPB 1 |
| preparsek-hold-career | 1 | PPB 1 |
| preparsek-untouched-career | 1 | PPB 1 |
| recording-quarantine-ksc | 1 | QR 1 |
| refly-midsession-saved | 1 | RFP 1 |
| rover-crash-watched | 1 | RVC 1 |
| srv-hinge-rover | 1 | SRV 1 |
| srv-mirror-hinge-rover | 1 | SRV 1 |
| srv-rotor-rover | 1 | SRV 1 |
| stock-screen-census-repair | 1 | KB 1 |
| strategy-career | 1 | L 1 |
| tourist-splashdown-career | 1 | TOUR 1 |
| tylo-bop-recorded | 1 | V 1 |
| tylo-orbit-recorded | 1 | V 1 |
| vall-park-nerv | 1 | B 1 |
| vall-tylo-recorded | 1 | V 1 |

Injected recordings (`fixture.injectedRecordings`, lanes not on `none`):

| injected set | lanes |
|---|---|
| all-synthetic | 18 |
| rewind-b9 | 17 |
| chain-tip-recovery | 5 |
| ghost-commnet-relay | 5 |
| refly-world-preservation | 5 |
| chain-tip-dock | 4 |
| part-showcase | 4 |
| rewind-readback | 3 |
| spawn-control-target | 3 |
| standalone-loop-map | 3 |
| looped-interplanetary | 2 |
| pending-limbo-tree | 2 |
| rewind-crew-loss | 2 |
| spawn-safety | 2 |
| background-claim | 1 |
| drill-harvest-route | 1 |
| ghost-commnet-lab | 1 |
| ghost-commnet-live | 1 |
| ghost-commnet-timeline | 1 |
| launch-site-claim | 1 |
| leaves-departure | 1 |
| overlap-cap | 1 |
| relative-loop | 1 |
| rescan-substitute | 1 |
| single-point-hold | 1 |
| single-point-hold-wide | 1 |
| spawn-room | 1 |
| spawn-stack | 1 |
| vessel-budget | 1 |

## Lanes per body

| body | lanes | autopilot | live-proven | families |
|---|---|---|---|---|
| Kerbin | 453 | 134 | 384 | RF 29, V 26, RW 25, B 24, QL 22, GUI 19, LAB 18, EVA 17 +97 |
| Mun | 72 | 15 | 62 | RW 22, V 8, B 5, CS 5, RSQ 4, TP 4, SOI 3, CT 2, MC 2 +15 |
| Minmus | 13 | 5 | 9 | V 6, B 5, AP 1, TP 1 |
| Moho | 3 | 1 | 2 | V 2, B 1 |
| Eve | 12 | 4 | 6 | V 7, B 4, FAR 1 |
| Gilly | 7 | 2 | 5 | V 4, B 2, FAR 1 |
| Duna | 30 | 10 | 20 | V 13, B 4, FAR 4, TP 3, AT 1, CN 1, FORGE 1, GUI 1, H 1 +1 |
| Ike | 10 | 3 | 10 | V 4, FAR 3, B 2, RF 1 |
| Dres | 8 | 6 | 2 | B 5, V 2, FORGE 1 |
| Jool | 48 | 18 | 42 | V 25, B 18, SPW 2, FAR 1, S 1, TP 1 |
| Laythe | 29 | 15 | 29 | B 14, V 11, FAR 1, LAY 1, SPW 1, TP 1 |
| Vall | 11 | 5 | 11 | B 5, V 5, TP 1 |
| Tylo | 13 | 6 | 13 | B 6, V 6, SPW 1 |
| Bop | 8 | 4 | 8 | B 4, V 4 |
| Pol | 4 | 2 | 4 | B 2, V 2 |
| Eeloo | 3 | 1 | - | V 2, B 1 |
| Sun | 11 | - | 7 | TP 6, V 4, ASR 1 |
| (none derived) | 166 | 6 | 141 | H 62, RVR 19, S 9, CS 8, GUI 7, IR 7, VN 6, L 5, CN 4 +27 |

## Lanes per timeline operation

Derived from seam verbs: rewind-to-launch `InvokeRewindToLaunch`; re-fly `InvokeRewind` / `ReFlyRevert`; revert `RevertToLaunch` / `ReFlyRevert`; reload a non-boot `LoadGame`; save `SaveGame`; warp `WarpToUT` (physics-warp with `ladder`); time-jump `TimeJump`; scene-change `ExitToSpaceCenter` / `GoToEditor` / `LaunchFromEditor` / a reload with `scene`; vessel-switch `SimulateStockSwitchClick` / `CycleVessel` / `AlarmJump`; new-game `NewGame`.

| operation | lanes | families |
|---|---|---|
| rewind-to-launch | 131 | RW 24, EVA 11, MINE 10, SPW 9, L 4, RSQ 4, PADX 3, SEAT 3, SRV 3 +42 |
| re-fly | 71 | RF 24, QL 6, RB 5, RW 5, S 5, CI 3, VN 3, LAB 2, RFK 2, RFN 2 +13 |
| revert | 23 | QL 4, RW 4, IR 3, VN 3, LAB 2, FAR 1, FXR 1, LX 1, REV 1, RF 1 +2 |
| reload | 196 | V 30, QL 21, RF 17, LAB 10, EVA 9, SPW 9, MINE 7, RW 6, VN 6, RB 5 +46 |
| save | 309 | RW 25, QL 22, V 21, RVR 15, LAB 14, RF 14, CS 13, EVA 12, MINE 12 +72 |
| warp | 292 | RW 25, V 21, LAB 18, MINE 14, EVA 12, QL 12, SPW 11, ASR 8, CI 8 +74 |
| physics-warp | 20 | QL 5, ASR 4, PWR 3, RF 2, CA 1, FAR 1, L 1, SPW 1, STR 1, V 1 |
| time-jump | 133 | V 44, RVR 14, RF 9, TP 8, QL 6, S 6, GUI 5, H 5, IR 4, MINE 4 +22 |
| scene-change | 213 | V 30, RW 25, QL 14, RF 13, CS 11, LAB 8, IR 5, S 5, ASR 4, CI 4 +61 |
| vessel-switch | 27 | FLY 5, S 5, ASR 2, CI 2, EVA 2, MINE 2, SWC 2, AJ 1, FAR 1, GC 1 +4 |
| new-game | 3 | CS 2, SWS 1 |
| (none) | 224 | H 70, B 36, GUI 22, L 16, V 13, GS 7, S 7, MC 5, EVA 4, LT 4 +22 |

## Body x timeline operation

Lanes per body (rows) per timeline operation (columns). `-` is zero: a body never combined with that operation.

| body | lanes | rewind-to-launch | re-fly | revert | reload | save | warp | physics-warp | time-jump | scene-change | vessel-switch | new-game |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Kerbin | 453 | 118 | 57 | 17 | 161 | 234 | 240 | 18 | 53 | 174 | 22 | - |
| Mun | 72 | 33 | 11 | 6 | 21 | 50 | 51 | - | 13 | 46 | - | 1 |
| Minmus | 13 | - | - | - | 2 | 2 | 5 | 1 | 7 | 2 | - | - |
| Moho | 3 | - | - | - | - | 1 | - | - | 1 | - | - | - |
| Eve | 12 | - | - | - | 4 | 3 | - | - | 6 | 4 | 1 | - |
| Gilly | 7 | - | - | - | 3 | 2 | - | - | 3 | 3 | 1 | - |
| Duna | 30 | 3 | - | 1 | 8 | 10 | 8 | - | 10 | 9 | - | - |
| Ike | 10 | 1 | 1 | 1 | 5 | 5 | 4 | - | 3 | 5 | - | - |
| Dres | 8 | - | - | - | - | 6 | 1 | - | 1 | - | - | - |
| Jool | 48 | 2 | - | - | 15 | 9 | 16 | 1 | 15 | 13 | 1 | - |
| Laythe | 29 | 2 | - | - | 9 | 6 | 9 | 1 | 8 | 8 | - | - |
| Vall | 11 | - | - | - | 3 | 1 | 3 | - | 3 | 3 | - | - |
| Tylo | 13 | 1 | - | - | 3 | 1 | 2 | - | 1 | 2 | - | - |
| Bop | 8 | - | - | - | 2 | - | 1 | - | 1 | 2 | - | - |
| Pol | 4 | - | - | - | 1 | - | 1 | - | 1 | 1 | - | - |
| Eeloo | 3 | - | - | - | - | 1 | - | - | 1 | - | - | - |
| Sun | 11 | - | - | - | 3 | 3 | 9 | - | 9 | 3 | - | - |
| (none derived) | 166 | 7 | 14 | 5 | 18 | 49 | 24 | - | 45 | 19 | 3 | 2 |

## Family x timeline operation

Lanes per family (rows) per timeline operation (columns).

| family | lanes | rewind-to-launch | re-fly | revert | reload | save | warp | physics-warp | time-jump | scene-change | vessel-switch | new-game |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| AH | 1 | - | - | - | - | - | - | - | - | - | - | - |
| AIR | 3 | 2 | - | - | - | - | 2 | - | - | 1 | - | - |
| AJ | 1 | - | 1 | - | - | - | - | - | 1 | - | 1 | - |
| AP | 1 | - | - | - | - | - | 1 | - | 1 | - | - | - |
| ASR | 9 | - | - | - | 1 | 4 | 8 | 4 | - | 4 | 2 | - |
| AST | 4 | 2 | 1 | - | 3 | 4 | 4 | - | 1 | 1 | - | - |
| AT | 1 | - | - | - | - | - | - | - | 1 | - | - | - |
| B | 41 | - | - | - | - | 5 | - | - | - | - | - | - |
| BAY | 1 | 1 | - | - | - | - | 1 | - | - | - | - | - |
| BDOCK | 2 | - | - | - | - | - | - | - | - | - | - | - |
| BOAT | 1 | 1 | - | - | - | 1 | 1 | - | - | - | - | - |
| CA | 1 | - | - | - | - | - | 1 | 1 | - | - | - | - |
| CI | 11 | - | 3 | - | 1 | 1 | 8 | - | 1 | 4 | 2 | - |
| CL | 2 | - | - | - | - | - | - | - | 1 | 1 | - | - |
| CLK | 1 | - | - | - | - | - | - | - | - | - | - | - |
| CN | 6 | 1 | - | - | 1 | 1 | 6 | - | - | 1 | - | - |
| COM | 1 | 1 | - | - | 1 | 1 | 1 | - | - | - | - | - |
| CPR | 1 | 1 | - | - | - | 1 | 1 | - | - | 1 | - | - |
| CS | 13 | - | - | - | - | 13 | - | - | - | 11 | - | 2 |
| CT | 3 | 2 | - | - | 2 | 2 | 2 | - | - | 2 | - | - |
| CTW | 1 | 1 | - | - | - | 1 | 1 | - | - | 1 | - | - |
| CX | 6 | 1 | - | - | 3 | 6 | 4 | - | - | 4 | - | - |
| DV | 1 | - | - | - | - | 1 | - | - | - | 1 | - | - |
| EVA | 17 | 11 | - | - | 9 | 12 | 12 | - | - | 2 | 2 | - |
| EX | 2 | 1 | - | - | 1 | 1 | 2 | - | - | - | - | - |
| FAR | 6 | 2 | - | 1 | 3 | 4 | 4 | 1 | - | 3 | 1 | - |
| FFC | 1 | 1 | - | - | - | 1 | 1 | - | 1 | 1 | - | - |
| FGV | 1 | - | - | - | - | - | 1 | - | - | - | - | - |
| FLG | 2 | 2 | - | - | 1 | 1 | 2 | - | - | 1 | - | - |
| FLY | 5 | - | - | - | - | - | - | - | - | - | 5 | - |
| FORGE | 10 | - | 1 | - | - | 10 | - | - | 1 | - | - | - |
| FXR | 1 | - | 1 | 1 | 1 | 1 | 1 | - | - | - | - | - |
| GC | 2 | - | - | - | 1 | 1 | 1 | - | - | - | 1 | - |
| GM | 1 | - | - | - | 1 | 1 | - | - | - | - | - | - |
| GPOS | 2 | 2 | - | - | - | - | 2 | - | - | 2 | - | - |
| GS | 11 | - | - | - | - | - | 1 | - | - | 3 | 1 | - |
| GUI | 28 | - | - | - | - | - | 1 | - | 5 | 1 | - | - |
| H | 75 | 2 | - | - | 1 | 5 | 1 | - | 5 | 1 | - | - |
| HC | 2 | 2 | - | - | - | - | - | - | - | 1 | - | - |
| HV | 2 | - | - | - | - | - | 1 | - | - | - | - | - |
| IR | 8 | 2 | 1 | 3 | 4 | 7 | 5 | - | 4 | 5 | - | - |
| KB | 5 | - | - | - | 1 | 1 | 1 | - | - | 1 | 1 | - |
| KD | 1 | 1 | - | - | - | 1 | 1 | - | - | 1 | - | - |
| KSS | 2 | 2 | - | - | 1 | 2 | 2 | - | - | 1 | - | - |
| L | 22 | 4 | 1 | - | 3 | 6 | 6 | 1 | - | 2 | - | - |
| LAB | 18 | 1 | 2 | 2 | 10 | 14 | 18 | - | 2 | 8 | - | - |
| LABX | 1 | - | - | - | - | 1 | - | - | - | - | - | - |
| LAY | 1 | 1 | - | - | 1 | 1 | 1 | - | - | 1 | - | - |
| LF | 3 | 2 | - | - | 2 | 2 | 3 | - | 2 | 3 | - | - |
| LS | 1 | - | - | - | - | - | - | - | - | 1 | - | - |
| LT | 5 | - | - | - | - | - | - | - | 1 | - | - | - |
| LX | 2 | - | 1 | 1 | 2 | 2 | 2 | - | - | 2 | - | - |
| M | 3 | - | - | - | - | - | - | - | - | - | - | - |
| MC | 7 | - | 1 | - | - | - | 1 | - | 2 | - | - | - |
| MINE | 14 | 10 | - | - | 7 | 12 | 14 | - | 4 | 2 | 2 | - |
| MS | 1 | - | - | - | - | - | - | - | - | - | - | - |
| OC | 1 | - | - | - | - | - | 1 | - | - | - | - | - |
| OSC | 1 | - | - | - | - | - | - | - | - | - | - | - |
| PAD | 1 | - | - | - | - | - | - | - | - | 1 | - | - |
| PADG | 1 | 1 | - | - | - | - | 1 | - | - | 1 | - | - |
| PADX | 3 | 3 | - | - | 1 | 3 | 3 | - | - | 2 | - | - |
| PBR | 1 | 1 | - | - | 1 | 1 | 1 | - | - | - | - | - |
| PP | 10 | - | - | - | - | - | 7 | - | - | - | - | - |
| PPB | 3 | - | - | - | 1 | 1 | - | - | - | - | - | - |
| PSP | 1 | 1 | - | - | 1 | 1 | 1 | - | - | - | - | - |
| PWR | 3 | - | - | - | - | - | 3 | 3 | - | - | - | - |
| QL | 23 | 2 | 6 | 4 | 21 | 22 | 12 | 5 | 6 | 14 | - | - |
| QR | 1 | - | - | - | - | 1 | - | - | - | - | - | - |
| QTM | 1 | - | - | - | 1 | - | - | - | - | 1 | - | - |
| QWS | 1 | - | - | - | - | - | - | - | - | 1 | - | - |
| R | 3 | - | - | - | - | - | - | - | - | - | - | - |
| RB | 5 | 2 | 5 | - | 5 | 5 | - | - | 1 | 2 | - | - |
| RC | 3 | 1 | 1 | - | 2 | 2 | 2 | - | 1 | - | - | - |
| RCN | 1 | 1 | - | - | 1 | 1 | 1 | - | 1 | - | - | - |
| REV | 1 | 1 | - | 1 | - | 1 | 1 | - | - | 1 | - | - |
| RF | 32 | 2 | 24 | 1 | 17 | 14 | 7 | 2 | 9 | 13 | 1 | - |
| RFK | 2 | - | 2 | - | 2 | 2 | 2 | - | - | 2 | - | - |
| RFN | 2 | - | 2 | - | 1 | 1 | 2 | - | - | 2 | - | - |
| RFP | 1 | - | - | - | - | - | - | - | - | - | - | - |
| RFQ | 2 | - | 2 | - | 1 | - | - | - | 2 | 2 | - | - |
| RH | 1 | - | 1 | - | - | - | - | - | - | - | - | - |
| RL | 1 | - | - | - | - | - | 1 | - | - | - | - | - |
| RR | 1 | 1 | - | - | 1 | 1 | - | - | - | 1 | - | - |
| RS | 1 | - | - | - | - | 1 | 1 | - | 1 | 1 | - | - |
| RSC | 3 | - | - | - | - | - | 3 | - | - | - | - | - |
| RSF | 1 | - | - | - | 1 | 1 | 1 | - | 1 | - | - | - |
| RSQ | 4 | 4 | 1 | 1 | 4 | 4 | 4 | - | 3 | 4 | - | - |
| RVC | 1 | 1 | - | - | 1 | 1 | 1 | - | - | 1 | - | - |
| RVR | 19 | - | - | - | - | 15 | - | - | 14 | - | - | - |
| RW | 25 | 24 | 5 | 4 | 6 | 25 | 25 | - | - | 25 | - | - |
| S | 20 | - | 5 | - | 3 | 3 | 6 | - | 6 | 5 | 5 | - |
| SB | 1 | - | - | - | - | - | - | - | - | - | - | - |
| SCI | 1 | 1 | - | - | - | 1 | 1 | - | - | 1 | - | - |
| SD | 1 | - | - | - | - | - | - | - | - | - | - | - |
| SE | 1 | - | - | - | 1 | 1 | - | - | 1 | 1 | - | - |
| SEAT | 4 | 3 | - | - | 2 | 3 | 4 | - | - | 2 | 1 | - |
| SH | 1 | 1 | - | - | - | 1 | 1 | - | - | 1 | - | - |
| SLM | 3 | - | - | - | 1 | 1 | 3 | - | - | 1 | - | - |
| SN | 1 | - | - | - | - | - | - | - | - | - | - | - |
| SOI | 3 | - | - | - | - | 1 | 1 | - | - | - | - | - |
| SPW | 11 | 9 | - | - | 9 | 10 | 11 | 1 | - | 1 | - | - |
| SRV | 3 | 3 | - | - | 3 | 3 | 3 | - | - | - | - | - |
| SS | 1 | - | - | - | - | 1 | 1 | - | - | - | - | - |
| SSC | 1 | - | - | - | - | 1 | 1 | - | - | 1 | - | - |
| ST | 4 | - | - | - | 1 | 1 | - | - | - | 1 | - | - |
| STR | 4 | 2 | 1 | 1 | 3 | 4 | 3 | 1 | - | 3 | - | - |
| SUB | 3 | 2 | - | - | - | 3 | 2 | - | - | 1 | - | - |
| SURV | 2 | - | - | - | - | - | 2 | - | - | 2 | - | - |
| SWC | 2 | - | - | - | 2 | 2 | 2 | - | - | 2 | 2 | - |
| SWS | 1 | - | - | - | 1 | 1 | - | - | - | 1 | - | 1 |
| TL | 1 | - | - | - | - | 1 | - | - | - | - | - | - |
| TOUR | 1 | 1 | - | - | - | 1 | 1 | - | - | - | - | - |
| TP | 9 | - | - | - | 2 | 2 | 7 | - | 8 | 2 | - | - |
| TSS | 3 | 2 | - | - | 3 | 3 | 3 | - | - | 2 | - | - |
| UIS | 1 | - | - | - | - | - | - | - | - | - | - | - |
| V | 67 | - | - | - | 30 | 21 | 21 | 1 | 44 | 30 | - | - |
| VB | 1 | - | - | - | - | 1 | 1 | - | - | - | - | - |
| VN | 14 | 2 | 3 | 3 | 6 | 8 | 8 | - | - | 3 | - | - |
| W | 1 | - | - | - | - | - | - | - | 1 | - | - | - |
| WDK | 1 | - | - | - | - | - | 1 | - | 1 | - | - | - |
| WF | 1 | 1 | - | - | - | 1 | 1 | - | - | 1 | - | - |
| WR | 1 | - | - | - | - | 1 | - | - | - | 1 | - | - |
| WTT | 2 | - | - | - | - | 2 | 2 | - | - | 2 | - | - |
| ZF | 1 | - | - | - | - | 1 | 1 | - | - | 1 | - | - |

## Lanes per mission machine

165 autopilot lanes fly 58 mission machines.

| mission | lanes | lane ids |
|---|---|---|
| gs1_auto_chute_booster | 16 | ASR-2-chute-stage-ends-landed +15 |
| kx_rewind_watch | 16 | ASR-1-ignored-stages-fall-to-ground, ASR-5-far-switch-commits-predicted-fall +14 |
| science_bench_recover | 14 | CS-1-science-first-hop, CS-2-science-engineering101 +12 |
| b25_laythe_orbit | 9 | B25-laythe-orbit, B33-tylo-orbit, B34-bop-orbit, B35-pol-orbit +5 |
| forge_station | 7 | FORGE-b17-duna-pad, FORGE-b18-dres-pad, FORGE-bdock-station, FORGE-eva3-pad +3 |
| gs1_auto_chute_booster_career | 7 | L13-career-ground-truth-after-refly-and-f9 +6 |
| ast_klaw_grab | 5 | AST-1-klaw-grab-rewind, AST-2-klaw-release, AST-3-klaw-mine-route +2 |
| b2_lko_ascent | 5 | B18-dres-lko-ascent, DV-1-ascent-deltav-snapshots, PWR-1-physwarp-ascent-low +2 |
| gs2_orbital_probe_deploy | 5 | GS-2-orbital-probe-deploy, GS-3-switch-nudge-deployed +3 |
| b29_jool_kerbin | 4 | B29-jool-kerbin-return, B43-vall-tylo-transfer, B44-laythe-tylo-transfer +1 |
| m3_loop_arrival_dwell | 4 | V2-loop-arrival-dwell, V24W-duna-one-warp-stair, V3F-flight-arrival-faithful +1 |
| b1_pad_hop_career | 3 | QL-2-quickload-career-recovery-not-paid +2 |
| b23_ike_orbit | 3 | B23-ike-orbit, B36-duna-ike-continuation, RF-21-refly-sibling-crosses-mun-soi |
| ci9_tip_dock | 3 | CI-9-chain-tip-dock, VN-10-dock-undock-dominant-rename, VN-4-dock-undock-rename |
| eva4_atmo_chute | 3 | EVA-4-atmo-chute, GPOS-2-eva-chute-relative-ghost-position +1 |
| forge_lko | 3 | FORGE-eva2-lko, FORGE-gs2-orbital-stack, LABX-1-lab-orbit-ascent |
| psub_dive | 3 | SUB-1-submarine-dive-ksc-end-spawn, SUB-2-submarine-dive-flight-ghost-spawn +1 |
| rf12s_refly_orbit_insert | 3 | RF-12S-refly-saves-pre-rewind-boarded-crew +2 |
| air1_runway_circuit | 2 | AIR-1-runway-circuit-splashdown, AIR-2-runway-circuit-ksc-ghost |
| b13_mun_landing | 2 | B13-mun-landing, KSS-1-mun-lander-ksc-end-spawn |
| b17_duna_direct | 2 | B17-duna-direct-orbit, V3C-flight-arrival-companion |
| b24_gilly_orbit | 2 | B24-gilly-orbit, B37-eve-gilly-continuation |
| b26_laythe_vall | 2 | B26-laythe-vall-transfer, B48-jool-laythe-bop-continuation |
| b30_mun_minmus | 2 | B30-mun-minmus-transfer, B46-minmus-mun-transfer |
| b4_reentry | 2 | B4-reentry-splashdown, LAY-1-laythe-reentry-splashdown |
| cl1_pod_impact | 2 | CL-2-pod-impact-ledger, KD-1-career-death-rewind |
| cl3_refly_crew_tombstone | 2 | CL-4-refly-crew-standin, QL-3-quickload-before-refly-merge |
| d5_redock | 2 | LAB-13b-lab-dock-undock, SD-1-same-tree-redock |
| r1_rewind_loop | 2 | R1-rewind-loop-flown, RF-12L-rewind-batch-after-landing |
| soi1_mun_exit_burn | 2 | SOI-1-mun-exit-physics-burn, SOI-2-mun-exit-formation-wingman |
| air3_drone_intercept | 1 | AIR-3-plane-drone-midair-collision |
| b11_mun_orbit | 1 | B11-mun-orbit |
| b11_mun_orbit_return | 1 | CS-6-career-mun-orbit |
| b11_mun_science_return | 1 | CS-6c-career-mun-science |
| b12_minmus_orbit | 1 | B12-minmus-orbit |
| b13_mun_landing_return | 1 | CS-7-career-mun-landing |
| b14_minmus_landing | 1 | B14-minmus-landing |
| b15_eve_flyby | 1 | B15-eve-flyby |
| b16_eve_orbit | 1 | B16-eve-orbit |
| b19_dres_orbit | 1 | B19-dres-orbit |
| b1_pad_hop | 1 | B1-pad-hop |
| b20_moho_orbit | 1 | B20-moho-orbit |
| b21_eeloo_orbit | 1 | B21-eeloo-orbit |
| b22_jool_orbit | 1 | B22-jool-orbit |
| b28_laythe_jool | 1 | B28-laythe-jool-return |
| b47_jool_moon_tour | 1 | B47-jool-moon-tour |
| b4_reentry_career | 1 | CS-4-career-orbit-return |
| b5_mun_flyby | 1 | B5-mun-flyby |
| b5_mun_flyby_return | 1 | CS-5-career-mun-flyby |
| b6_minmus_flyby | 1 | B6-minmus-flyby |
| b7_duna_flyby | 1 | B7-duna-flyby |
| bay1_runway_bays | 1 | BAY-1-runway-cargo-bays |
| bdock_dock_transfer | 1 | BDOCK-1-station-interceptor |
| bdock_second_dock | 1 | BDOCK-2-second-dock-harvest |
| boat1_water_taxi | 1 | BOAT-1-mallard-water-taxi |
| ci11_krpc_recover | 1 | CI-11-chain-tip-krpc-recover-no-respawn |
| record_climb_rewind | 1 | WR-1-world-record-reearned-after-rewind |
| v1_map_dwell | 1 | V1-map-dwell-mun-orbit |

## Lanes per seam verb

| verb | lanes | families |
|---|---|---|
| FlushAndQuit | 706 | H 75, V 67, B 41, RF 32, GUI 28, RW 25, QL 23, L 22, S 20, RVR 19 +114 |
| LoadGame | 706 | H 75, V 67, B 41, RF 32, GUI 28, RW 25, QL 23, L 22, S 20, RVR 19 +114 |
| SetSetting | 696 | H 75, V 67, B 41, RF 31, GUI 28, RW 25, QL 23, L 22, S 20, RVR 19 +113 |
| StopRecording | 397 | V 65, RF 25, RW 24, RVR 19, EVA 17, LAB 17, H 15, B 14, MINE 14 +74 |
| SaveGame | 309 | RW 25, QL 22, V 21, RVR 15, LAB 14, RF 14, CS 13, EVA 12, MINE 12 +72 |
| WarpToUT | 292 | RW 25, V 21, LAB 18, MINE 14, EVA 12, QL 12, SPW 11, ASR 8, CI 8 +74 |
| RecordingState | 267 | RF 27, V 21, QL 16, LAB 14, MINE 12, VN 12, CS 11, EVA 11, L 11 +60 |
| DiscardTree | 263 | V 65, RVR 19, LAB 17, H 15, B 14, MINE 14, EVA 12, SPW 9, TP 9 +46 |
| RunTests | 236 | H 71, RW 25, V 13, CS 10, SPW 9, L 8, CN 6, LT 5, S 5, FAR 4, RF 4 +47 |
| MissionMark | 230 | RW 25, LAB 17, EVA 14, MINE 12, SPW 11, CS 10, QL 8, IR 7, L 6 +67 |
| StartRecording | 228 | B 23, RW 23, EVA 15, LAB 14, MINE 12, SPW 9, VN 8, S 7, FAR 6 +59 |
| CommitTree | 209 | RW 24, EVA 15, MINE 11, QL 9, SPW 9, VN 9, CS 8, L 8, LAB 8, B 7 +56 |
| ListHandles | 186 | RF 23, QL 16, EVA 12, MINE 12, CI 11, SPW 11, VN 10, LAB 9, IR 7 +46 |
| TimeJump | 133 | V 44, RVR 14, RF 9, TP 8, QL 6, S 6, GUI 5, H 5, IR 4, MINE 4 +22 |
| InvokeRewindToLaunch | 131 | RW 24, EVA 11, MINE 10, SPW 9, L 4, RSQ 4, PADX 3, SEAT 3, SRV 3 +42 |
| ExitToSpaceCenter | 108 | RW 16, QL 10, RF 10, LAB 7, S 5, ASR 4, IR 4, RSQ 4, GS 3, LF 3 +29 |
| GoToEditor | 96 | RW 25, CS 11, CI 4, RSQ 4, IR 3, LAB 3, STR 3, CT 2, FAR 2, GPOS 2 +31 |
| LaunchFromEditor | 94 | RW 25, CS 11, CI 4, RSQ 4, IR 3, LAB 3, STR 3, CT 2, FAR 2, GPOS 2 +29 |
| MissionConfig | 84 | V 59, TP 7, GUI 3, LF 3, LX 2, AP 1, AT 1, EVA 1, FAR 1, GS 1, MC 1 +4 |
| PartAction | 83 | LAB 17, MINE 14, RW 9, VN 5, ASR 4, RF 4, CX 3, SRV 3, AST 2 +19 |
| StartLoopPlayback | 76 | V 58, TP 7, LF 2, AP 1, AT 1, EVA 1, FAR 1, GS 1, MC 1, SE 1, W 1 +1 |
| InvokeRewind | 71 | RF 24, QL 6, RB 5, RW 5, S 5, CI 3, VN 3, LAB 2, RFK 2, RFN 2 +13 |
| KscAction | 71 | RW 18, L 10, CS 7, KB 4, RSQ 4, PADX 3, QL 3, STR 3, CT 2, GUI 2 +12 |
| UiAction | 67 | GUI 28, VN 10, RW 7, KB 4, L 3, RSC 3, LF 2, WTT 2, EVA 1, KD 1 +6 |
| AnswerMergeDialog | 56 | RF 17, S 5, QL 4, RW 4, CI 3, RB 2, RFK 2, RFN 2, RFQ 2, AJ 1 +14 |
| CaptureScreenshot | 52 | GUI 28, TP 9, KB 4, RSC 3, L 2, RW 2, KD 1, LAB 1, SH 1, UIS 1 |
| EvaExit | 46 | EVA 17, CX 4, RW 4, SEAT 4, RF 3, FLG 2, QL 2, CI 1, FAR 1, GPOS 1 +7 |
| Recover | 44 | RW 11, CS 10, L 4, PADX 3, QL 3, CX 2, CI 1, CPR 1, FFC 1, LAB 1 +7 |
| SealSlot | 43 | RVR 15, IR 7, H 5, GUI 3, RF 3, MINE 2, AST 1, QL 1, RB 1, RCN 1 +4 |
| ExportRenderManifest | 42 | V 39, B 1, FAR 1, H 1 |
| RouteCommand | 40 | RVR 15, IR 8, H 5, GUI 4, MINE 2, AST 1, RCN 1, RS 1, RSF 1, SSC 1 +1 |
| KscMarkerRecover | 29 | RW 16, RSQ 4, CT 2, WTT 2, CI 1, CPR 1, EVA 1, RS 1, STR 1 |
| DumpGuiTree | 27 | GUI 26, RSC 1 |
| ReadVesselResources | 24 | MINE 13, IR 5, LAB 3, AST 1, RC 1, RCN 1 |
| EnterWatchMode | 20 | V 11, AP 1, FAR 1, GUI 1, LX 1, MC 1, MINE 1, RF 1, W 1, WDK 1 |
| SimulateStockSwitchClick | 20 | S 4, FLY 3, CI 2, EVA 2, MINE 2, ASR 1, FAR 1, GS 1, KB 1, RF 1 +2 |
| EnterMapView | 19 | TP 7, V 3, GUI 2, RF 2, SLM 2, GS 1, H 1, SPW 1 |
| EvaBoard | 17 | EVA 6, RF 3, RW 3, FLG 2, CX 1, LAB 1, WF 1 |
| StashSlot | 17 | RW 5, RF 3, LAB 2, RFK 2, RFN 2, IR 1, LX 1, RSQ 1 |
| StockScreen | 16 | RW 5, GUI 3, KB 3, L 2, KD 1, LS 1, SH 1 |
| EvaGroundScience | 15 | EVA 10, SEAT 2, CX 1, PADG 1, RC 1 |
| ReFlyRevert | 15 | QL 4, RW 3, VN 2, FXR 1, IR 1, LAB 1, LX 1, RF 1, RSQ 1 |
| RealSpawn | 14 | CI 6, MINE 4, VN 4 |
| RenameVessel | 13 | VN 13 |
| ExitMapView | 10 | GUI 2, RF 2, V 2, GS 1, H 1, SPW 1, W 1 |
| PartParitySweep | 10 | PP 10 |
| DumpMapGeometry | 9 | TP 9 |
| MapCamera | 9 | TP 9 |
| RevertToLaunch | 8 | IR 2, FAR 1, LAB 1, REV 1, RW 1, STR 1, VN 1 |
| TrackingStationRecover | 7 | RW 2, CI 1, EVA 1, L 1, RF 1, STR 1 |
| DriveVessel | 6 | MINE 2, RSF 1, RVC 1, RVR 1, TSS 1 |
| AlarmJump | 5 | FLY 2, AJ 1, ASR 1, S 1 |
| EvaSeatBoard | 5 | SEAT 4, CX 1 |
| PlantFlag | 5 | FLG 2, CX 1, EVA 1, WF 1 |
| CrewTransfer | 4 | CX 3, KB 1 |
| EvaSeatLeave | 3 | SEAT 3 |
| NewGame | 3 | CS 2, SWS 1 |
| CycleVessel | 2 | GC 1, SWC 1 |
| EvaChuteDeploy | 2 | EVA 1, GPOS 1 |
| ExitToMainMenu | 2 | QTM 1, RFQ 1 |
| PickUpFlag | 2 | FLG 1, WF 1 |
| EvaMove | 1 | EVA 1 |
| LaunchSiteProbe | 1 | LS 1 |
| SafeWriteCrash | 1 | ST 1 |
| SpinVessel | 1 | MC 1 |

## Lanes per in-game test category

Categories named by `RunTests` steps (`(all)`: a step with no category). 236 lanes run at least one batch.

| category | lanes | lane ids |
|---|---|---|
| LedgerGroundTruth | 58 | CPR-1-contract-parameter-paid-once-after-rewind, CS-2-science-engineering101 +56 |
| LeafVesselPlacement | 26 | AIR-2-runway-circuit-ksc-ghost, BOAT-1-mallard-water-taxi +24 |
| Logistics | 12 | H34-logistics-inter-body, H35-logistics-route-proof, H38-logistics-isolated +9 |
| MoonArrivalHold | 12 | V31M-duna-ike-continuation-player-loop +11 |
| Rewind | 6 | R7a-rewind-session-absent, R7c-rewind-spacecenter +4 |
| AutoRecord | 4 | H61-autorecord-isolated, H68-autorecord-orbiting, H69-autorecord-landed +1 |
| GhostCommNet | 4 | CN-1-ghost-commnet-relay, CN-1T-ghost-commnet-relay-ts +2 |
| FlightGhostPosition | 3 | EVA-17-orbit-jetpack-hop-rewind, GPOS-1-kerbalx-ascent-ghost-position +1 |
| Missions | 3 | H54-missions, LT-4-long-tail-route-flight, M1-mission-loop-unit |
| StandaloneLoopMap | 3 | SLM-1-standalone-loop-map-position, SLM-1T-standalone-loop-ts-position +1 |
| Contracts | 2 | CT-1-contracts-discard-economy, LT-3-long-tail-career-flight |
| GameActionsHealth | 2 | B10-career-passive-safety, L1-passive-sandbox |
| GhostMap | 2 | H44-ghost-map-trackstation, S1.6-render-parity |
| MissionPhasing | 2 | LT-1-long-tail-flight, M3-mission-phasing-airborne-anchor |
| PreParsekBackup | 2 | PPB-1-untouched-career-backup, PPB-2-brandnew-career-skip |
| RouteDockCapture | 2 | H55-route-dock-capture-isolated, H56-route-dock-capture-landed |
| RouteGhostPlacement | 2 | H59-surface-route-map-lines, V27M-rover-route-endpoint-substituted-map-lines |
| RouteRewindTimeline | 2 | H6-route-rewind-timeline, MC-6-route-rewind-timeline-modded |
| SceneAndPatch | 2 | H53-scene-and-patch, LT-2-long-tail-spacecenter |
| Storage | 2 | ST-1-storage-timeline-ingame, ST-2-rewind-point-quicksave |
| StrategyLifecycle | 2 | L3-strategy-currency-conversion, L3-strategy-exchanger-floor |
| SwitchIntentPatch | 2 | LT-1-long-tail-flight, LT-2-long-tail-spacecenter |
| TestRunnerIsolation | 2 | LT-1-long-tail-flight, LT-2-long-tail-spacecenter |
| AutoHireReservation | 1 | AH-1-auto-hire-reserved-applicant |
| AutoMergeCommit | 1 | H67-automerge-commit-isolated |
| BackgroundSeeder | 1 | LT-1-long-tail-flight |
| BgSoiCrossingPlayback | 1 | SOI-3-bg-probe-mun-exit-on-rails |
| Bug289 | 1 | LT-1-long-tail-flight |
| ChainTipBlockedGhost | 1 | LT-5-long-tail-playback-flight |
| ClawCouple | 1 | H42-claw-couple |
| ClockCauseScope | 1 | CLK-1-ground-station-contract-untagged |
| Coalescer | 1 | H62-coalescer-isolated |
| ContractReachedOutcome | 1 | L12-contract-reached-outcome-blocks-fail |
| ConverterJumpLab | 1 | LAB-1-converter-jump-lab-ledger |
| CrewReservation | 1 | H31-crew-reservation |
| CrewReservationLive | 1 | LT-4-long-tail-route-flight |
| DataHealth | 1 | H14-corpus-data-health |
| DeployedScienceGhost | 1 | LT-5-long-tail-playback-flight |
| Diagnostics | 1 | H27-diagnostics |
| DisabledHoverEcho | 1 | LT-1-long-tail-flight |
| EvaSpawnPosition | 1 | H20-eva-spawn-position |
| FairingGhostVisual | 1 | FGV-1-fairing-ghost-visual |
| FinalizeBackfill | 1 | H10-finalize-backfill |
| FinalizeLimbo | 1 | LT-1-long-tail-flight |
| Flight | 1 | LT-1-long-tail-flight |
| FlightIntegration | 1 | H17-flight-integration |
| ForwardRender | 1 | LT-1-long-tail-flight |
| FreeTechUnlock | 1 | AH-1-auto-hire-reserved-applicant |
| GhostAudio | 1 | H30-ghost-audio |
| GhostChains | 1 | H50-ghost-chains |
| GhostCommNetLive | 1 | CN-2-ghost-commnet-live-probe |
| GhostCommNetTimeline | 1 | CN-3-ghost-commnet-timeline-warp |
| GhostFxIntegrity | 1 | FXR-1-ghost-engine-fx-across-refly-and-retry |
| GhostLifecycle | 1 | LT-5-long-tail-playback-flight |
| GhostMapOrbits | 1 | LT-1-long-tail-flight |
| GhostPlayback | 1 | S1.4-injected-playback |
| GhostReapplyFrame | 1 | LT-5-long-tail-playback-flight |
| GhostVisuals | 1 | H15-corpus-ghost-visuals |
| GuiTree | 1 | GUI-1-census-ksc |
| HarvestCapScanLedger | 1 | HV-2-scan-ledger-drill-rates |
| IdentityLoss | 1 | LT-1-long-tail-flight |
| IncompleteBallistic | 1 | H9-incomplete-ballistic |
| JointBreakCheckCoroutine | 1 | LT-5-long-tail-playback-flight |
| KerbalInventorySpawn | 1 | H72-kerbal-inventory-spawn |
| KSP | 1 | H13-ksp-api-smoke |
| KspApiSanity | 1 | H24-ksp-api-sanity |
| Ledger | 1 | H48-ledger-drawdown |
| LocalizedName | 1 | H29-localized-name |
| LogContracts | 1 | H26-log-contracts |
| LogisticsGrapple | 1 | H41-logistics-grapple-isolated |
| MapPresence | 1 | H28-map-presence |
| MapRender | 1 | S1.7-maprender-parity |
| MapView | 1 | H47-map-view |
| MergeDialog | 1 | H63-merge-dialog-isolated |
| MiningHarvest | 1 | MINE-5a-rover-live-drill-harvest-cap |
| Optimizer | 1 | LT-2-long-tail-spacecenter |
| OrbitSpawnClearance | 1 | OSC-1-orbit-spawn-clearance-lko |
| PartEventFidelity | 1 | H37-part-event-fidelity |
| PartEventFX | 1 | LT-5-long-tail-playback-flight |
| PartEventTiming | 1 | LT-1-long-tail-flight |
| Periodicity | 1 | M2-periodicity-solver |
| Pipeline-Anchor | 1 | H11-pipeline-anchor |
| Pipeline-Anchor-BubbleEntry | 1 | LT-1-long-tail-flight |
| Pipeline-AnchorPropagate | 1 | LT-1-long-tail-flight |
| Pipeline-Frame | 1 | LT-1-long-tail-flight |
| Pipeline-Outlier | 1 | LT-1-long-tail-flight |
| Pipeline-Smoothing | 1 | H18-pipeline-smoothing |
| Pipeline-Terrain | 1 | LT-1-long-tail-flight |
| PlaybackControl | 1 | H66-playback-control-isolated |
| PlaybackFidelity | 1 | H36-playback-fidelity |
| PromotionConverterSeed | 1 | MINE-15-promotion-converter-seed |
| QuickloadResume | 1 | H65-quickload-resume-isolated |
| RecordedSignals | 1 | H33-recorded-signals |
| Recording | 1 | LT-2-long-tail-spacecenter |
| RecordingFinalization | 1 | H19-recording-finalization |
| RecordingInvariants | 1 | H5-invariants-corpus |
| RecordingStore | 1 | LT-1-long-tail-flight |
| ReentryFx | 1 | H52-reentry-fx |
| ReFlyWorldPreservation | 1 | S4.2-refly-world-preservation |
| RenderComposition | 1 | LT-1-long-tail-flight |
| ResourceManifest | 1 | LT-1-long-tail-flight |
| ResourceReconciliation | 1 | LT-2-long-tail-spacecenter |
| ResourceTopBar | 1 | H71-resource-topbar-ksc |
| ReStockCompat | 1 | MC-2-restock-compat |
| RevertFlow | 1 | H64-revert-flow-isolated |
| RevertVesselStrip | 1 | LT-1-long-tail-flight |
| RewindSaves | 1 | LT-1-long-tail-flight |
| RouteEscrowContention | 1 | H60-route-escrow-contention |
| RouteLifecycle | 1 | RVR-3-route-lifecycle |
| RouteLiveAnchor | 1 | LT-4-long-tail-route-flight |
| RouteStartDockedOrigin | 1 | H57-route-start-docked-origin-landed |
| SaveLoad | 1 | H51-save-load |
| SceneExitMerge | 1 | H21-scene-exit-merge-isolated |
| SeatGhostKerbal | 1 | SEAT-4-seat-replay-kerbal-visibility |
| Serialization | 1 | H25-serialization |
| ServoFollowers | 1 | SRV-1-hinge-followers-ghost |
| ServoMirroredPose | 1 | SRV-3-mirrored-hinge-pair-ghost |
| Settings | 1 | H46-settings |
| SnapshotBaseline | 1 | H32-snapshot-baseline |
| SnapshotSidecars | 1 | ST-3-snapshot-sidecars-bdock |
| SoiCrossingPlayback | 1 | S1.8-soi-crossing-playback |
| SpawnActiveVesselBlocker | 1 | SB-1-spawn-active-vessel-blocker |
| SpawnCollision | 1 | LT-1-long-tail-flight |
| Spawner | 1 | LT-1-long-tail-flight |
| SpawnHealth | 1 | H16-corpus-spawn-health |
| SpawnRotation | 1 | H8-spawn-rotation |
| SpawnTerminalOrbit | 1 | LT-1-long-tail-flight |
| StartupNotices | 1 | SN-1-startup-notices-clean-install |
| StockUiOverlay | 1 | H45-stock-ui-overlay |
| StockWarpLimits | 1 | LT-1-long-tail-flight |
| Structure | 1 | LT-4-long-tail-route-flight |
| SwitchSegment | 1 | H12-switch-segment |
| TerminalOrbit | 1 | LT-1-long-tail-flight |
| TerrainClearance | 1 | H43-terrain-clearance |
| TestCommands | 1 | LT-3-long-tail-career-flight |
| TestRunner | 1 | LT-1-long-tail-flight |
| Timeline | 1 | ST-1-storage-timeline-ingame |
| TrackingStation | 1 | H23-tracking-station |
| TrajectoryMath | 1 | H7-trajectory-math |
| TreeIntegrity | 1 | H49-tree-integrity |
| TreeRestoreCoroutine | 1 | LT-5-long-tail-playback-flight |
| UiComplexityMode | 1 | H22-ui-complexity-mode |
| UiScale | 1 | UIS-1-ui-scale |
| Unity | 1 | LT-1-long-tail-flight |
| VesselBudget | 1 | VB-1-ghost-vessel-budget |
| VesselNames | 1 | VN-1-stock-rename-staging-f9 |
| WarpToTime | 1 | LT-2-long-tail-spacecenter |
| Watch | 1 | LT-1-long-tail-flight |
| WaterfallCompat | 1 | MC-1-waterfall-compat |

## Expectation blocks

`[expectations.*]` tables present, and the tables that declare `gating = true` (dotted path).

| block | lanes present |
|---|---|
| logContracts | 705 |
| recordings | 691 |
| rewind | 175 |
| renderComposition | 42 |
| routes | 29 |
| unityExceptions | 29 |
| ledger | 25 |
| ghostLifecycle | 11 |
| partParity | 10 |
| analyzer | 4 |
| world | 1 |

| gating table | lanes |
|---|---|
| recordings.structure | 101 |
| rewind | 88 |
| routes | 19 |
| ghostLifecycle | 9 |
| renderComposition | 7 |
| recordings.points | 5 |
| partParity | 4 |
| recordings.sampling | 3 |

## Dimension coverage (`dimensionsCovered`)

Per registry dimension: values claimed by at least one lane, then the claimed values with their lane counts, thinnest first. Unclaimed values are listed under "Never used by any lane". 25 lanes declare no `dimensionsCovered`.

| dimension | claimed | values by lane count (thinnest first) |
|---|---|---|
| D1 | 17/18 | ballistic-extrapolation 1, commit-abort 1, commit-far-switch 1 +14 |
| D2 | 7/7 | physics-warp-high 1, physics-warp-low 1, physics-warp-medium 1, proximity-cadence-bg 1 +3 |
| D3 | 7/7 | boundary-seam 1, relative-anchored-nonloop 1, relative-loop 1, surface-body-fixed 1 +3 |
| D4 | 13/13 | env-body-split 1, hysteresis 1, persistence-graze-suppression 1, seed-event-split 1 +9 |
| D5 | 12/12 | staging-debris-promotion 1, bg-on-rails 2, chain-continuation-switch 2, single-node 2 +8 |
| D6 | 19/19 | attitude-preservation 1, chain-segment-handoff 1, ghost-audio 1 +16 |
| D7 | 16/16 | bays 1, chute-cut 1, engine-fx-effects 1, engine-fx-legacy 1 +12 |
| D8 | 18/18 | epoch-isolation 1, recalc-from-ut0 1, ers-els-routing 2, route 3, tombstones 3 +13 |
| D9 | 18/18 | rewind-to-launch-repeat 1, world-preservation 1, seal-stash-fly 2 +15 |
| D10 | 22/22 | candidate-detection 1, docked-depot-origin 1, inter-body-nth-window 1 +19 |
| D11 | 17/17 | default-mission 1, eccentric-inclined-targets 1, land-dock-dual-constraint 1 +14 |
| D12 | 9/9 | crew-swap 1, dead-crew-strip 1, missed-endut-auto-free 1, rescue-marker 1 +5 |
| D13 | 9/10 | ksc-exclusion 1, situation-correction 1, surface-orbit-reseed 1, terrain-correction 1 +5 |
| D14 | 35/35 | dres 1, eeloo 1, moho 1, reward-multipliers 1, vessel-budget 1, zero-starting-funds 1 +29 |
| D15 | 1/1 | timeline-projection 1 |
| D16 | 12/12 | alias-mode 1, deflate-snapshots 1, path-validation 1, rp-quicksaves 1, safe-write 1 +7 |
| D17 | 5/5 | better-time-warp 1, making-history 1, persistent-rotation 1, restock 1 +1 |
| D18 | 12/12 | chain-state-rederived 1, cross-tree-chain-linking 1, ghost-conversion-quicksave 1 +9 |

## Lanes by family

Columns: tier (`xfail` when `expectedFail` is armed), host save (`+inj` with injected recordings), driver (`seam` or the autopilot mission), bodies, timeline operations, in-game categories, status (quoted).

### AH (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| AH-1-auto-hire-reserved-applicant | nightly | fresh-career | seam | Kerbin | - | AutoHireReservation, FreeTechUnlock | LIVE-PROVEN 2026-09-27 |

### AIR (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| AIR-1-runway-circuit-splashdown | operator | gs1-two-stage-pad | air1_runway_circuit | Kerbin | - | - | LIVE-PROVEN 2026-11-16 |
| AIR-2-runway-circuit-ksc-ghost | operator | gs1-two-stage-pad | air1_runway_circuit | Kerbin | rewind-to-launch, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| AIR-3-plane-drone-midair-collision | operator | gs1-two-stage-pad | air3_drone_intercept | Kerbin | rewind-to-launch, warp, scene-change | - | LIVE-PROVEN 2026-11-20 |

### AJ (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| AJ-1-alarm-jump-refused-in-refly | operator | gloops-airshow +inj refly-world... | seam | - | re-fly, time-jump, vessel-switch | - | LIVE-PROVEN 2026-10-25 |

### AP (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| AP-1-minmus-attitude-residual | nightly | minmus-orbit-recorded | seam | Minmus | warp, time-jump | - | LIVE-PROVEN 2026-09-26 |

### ASR (9)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| ASR-1-ignored-stages-fall-to-ground | nightly | gs1-two-stage-pad | kx_rewind_watch | Kerbin | save, warp, physics-warp, scene-change | - | LIVE-PROVEN 2026-11-22 |
| ASR-2-chute-stage-ends-landed | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | save, scene-change | - | LIVE-PROVEN 2026-11-22 |
| ASR-3-graze-above-1kpa-stays-orbiting | daily | graze-high-probe-stack | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-22 |
| ASR-4-graze-below-1kpa-destroyed | daily | graze-low-probe-stack | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-22 |
| ASR-5-far-switch-commits-predicted-fall | nightly | eva2-lko-crewed | kx_rewind_watch | Kerbin | warp, physics-warp, vessel-switch | - | LIVE-PROVEN 2026-11-22 |
| ASR-6-takeover-drops-predicted-fall | nightly | gs1-two-stage-pad | kx_rewind_watch | Kerbin | reload, save, warp, physics-warp +2 | - | LIVE-PROVEN 2026-11-22 |
| ASR-7-chute-stage-lands-after-in-flight-commit | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | warp, physics-warp | - | LIVE-PROVEN 2026-11-22 |
| ASR-8-armed-chute-stage-falls-drag-free | daily | gs1-armed-chute-drop | seam | Kerbin | save, warp, scene-change | - | LIVE-PROVEN 2026-11-22 |
| ASR-9-escape-stage-ends-orbiting-sun | daily | kerbin-escape-probe-stack | seam | Kerbin, Sun | warp | - | LIVE-PROVEN 2026-11-22 |

### AST (4)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| AST-1-klaw-grab-rewind | nightly | ast-klaw-lko | ast_klaw_grab | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-15 |
| AST-2-klaw-release | nightly | ast-klaw-lko | ast_klaw_grab | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-15 |
| AST-3-klaw-mine-route | nightly | ast-miner-lko | ast_klaw_grab | Kerbin | save, warp, time-jump | - | LIVE-PROVEN 2026-11-15 |
| AST-4-klaw-refly | nightly | ast-mothership-lko | ast_klaw_grab | Kerbin | re-fly, reload, save, warp, scene-change | - | LIVE-PROVEN 2026-11-15 |

### AT (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| AT-1-duna-atmospheric-descent | nightly | duna-park-recorded | seam | Duna | time-jump | - | LIVE-PROVEN 2026-09-26 |

### B (41)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| B1-pad-hop | nightly | b1-pad-craft | b1_pad_hop | Kerbin | - | - | LIVE-PROVEN 2026-07-29 |
| B10-career-passive-safety | daily | fresh-career | seam | Kerbin | - | GameActionsHealth | RE-PROVEN 2026-07-26 |
| B11-mun-orbit | nightly | b2-lko-craft | b11_mun_orbit | Kerbin, Mun | - | - | LIVE-PROVEN 2026-07-25 |
| B12-minmus-orbit | nightly | b2-lko-craft | b12_minmus_orbit | Kerbin, Minmus | - | - | LIVE-PROVEN 2026-07-25 |
| B13-mun-landing | nightly | b2-lko-craft | b13_mun_landing | Kerbin, Mun | - | - | LIVE-PROVEN 2026-07-25 |
| B14-minmus-landing | nightly | b2-lko-craft | b14_minmus_landing | Kerbin, Minmus | - | - | LIVE-PROVEN 2026-07-25 |
| B15-eve-flyby | nightly | b2-lko-craft | b15_eve_flyby | Kerbin, Eve | - | - | SECOND interplanetary destination, and th... |
| B16-eve-orbit | longhaul | b2-lko-craft | b16_eve_orbit | Kerbin, Eve | - | - | LIVE-PROVEN 2026-07-29 |
| B17-duna-direct-orbit | nightly | b17-duna-pad | b17_duna_direct | Kerbin, Duna | - | - | THE OPTION-3 PRECONDITION-(a) FIXTURE PRO... |
| B18-dres-lko-ascent | operator | b18-dres-pad | b2_lko_ascent | Kerbin, Dres | save | - | FLOWN GREEN 2026-08-11 |
| B19-dres-orbit | longhaul | b18-dres-pad | b19_dres_orbit | Kerbin, Dres | save | - | FLOWN GREEN 2026-08-12 |
| B20-moho-orbit | longhaul | b18-dres-pad | b20_moho_orbit | Kerbin, Moho, Dres | save | - | LIVE-PROVEN 2026-08-12 |
| B21-eeloo-orbit | longhaul | b18-dres-pad | b21_eeloo_orbit | Kerbin, Dres, Eeloo | save | - | FLOWN GREEN TWICE 2026-08-12 |
| B22-jool-orbit | longhaul | b18-dres-pad | b22_jool_orbit | Kerbin, Dres, Jool | save | - | LIVE-PROVEN 2026-08-17 |
| B23-ike-orbit | operator | duna-park-probe | b23_ike_orbit | Duna, Ike | - | - | LIVE-PROVEN 2026-08-18 |
| B24-gilly-orbit | operator | eve-park-kerbalx | b24_gilly_orbit | Eve, Gilly | - | - | LIVE-PROVEN 2026-08-19 |
| B25-laythe-orbit | operator | jool-park-nerv | b25_laythe_orbit | Jool, Laythe | - | - | LIVE-PROVEN 2026-08-19 |
| B26-laythe-vall-transfer | operator | laythe-park-nerv | b26_laythe_vall | Jool, Laythe, Vall | - | - | LIVE-PROVEN 2026-08-20 |
| B28-laythe-jool-return | operator | laythe-park-nerv | b28_laythe_jool | Jool, Laythe | - | - | LIVE-PROVEN 2026-08-20 |
| B29-jool-kerbin-return | nightly | jool-park-nerv | b29_jool_kerbin | Kerbin, Jool | - | - | FLIGHT 3 PASS ATTEMPT 1 2026-08-27, MISSI... |
| B30-mun-minmus-transfer | operator | mun-park-kerbalx | b30_mun_minmus | Kerbin, Mun, Minmus | - | - | LIVE-PROVEN 2026-08-24 |
| B33-tylo-orbit | operator | jool-park-nerv | b25_laythe_orbit | Jool, Laythe, Tylo | - | - | LIVE-PROVEN 2026-09-26 |
| B34-bop-orbit | operator | jool-park-nerv | b25_laythe_orbit | Jool, Laythe, Bop | - | - | LIVE-PROVEN 2026-09-26 |
| B35-pol-orbit | operator | jool-park-nerv | b25_laythe_orbit | Jool, Laythe, Pol | - | - | LIVE-PROVEN 2026-09-26 |
| B36-duna-ike-continuation | operator | duna-direct-recorded | b23_ike_orbit | Duna, Ike | - | - | LIVE-PROVEN 2026-11-07 |
| B37-eve-gilly-continuation | operator | eve-orbit-recorded | b24_gilly_orbit | Eve, Gilly | - | - | LIVE-PROVEN 2026-11-07 |
| B38-jool-laythe-continuation | operator | jool-orbit-recorded | b25_laythe_orbit | Jool, Laythe | - | - | LIVE-PROVEN 2026-11-07 |
| B39-jool-vall-continuation | operator | jool-orbit-recorded | b25_laythe_orbit | Jool, Laythe, Vall | - | - | LIVE-PROVEN 2026-11-14 |
| B4-reentry-splashdown | nightly | b2-lko-craft | b4_reentry | Kerbin | - | - | 2026-09-25 (coverage wave 1b): D7 chute-c... |
| B40-jool-tylo-continuation | operator | jool-orbit-recorded | b25_laythe_orbit | Jool, Laythe, Tylo | - | - | LIVE-PROVEN 2026-11-14 |
| B41-jool-bop-continuation | operator | jool-orbit-recorded | b25_laythe_orbit | Jool, Laythe, Bop | - | - | LIVE-PROVEN 2026-11-14 |
| B42-jool-pol-continuation | operator | jool-orbit-recorded | b25_laythe_orbit | Jool, Laythe, Pol | - | - | LIVE-PROVEN 2026-11-14 |
| B43-vall-tylo-transfer | operator | vall-park-nerv | b29_jool_kerbin | Kerbin, Jool, Vall, Tylo | - | - | LIVE-PROVEN 2026-11-16 |
| B44-laythe-tylo-transfer | operator | laythe-park-nerv | b29_jool_kerbin | Kerbin, Jool, Laythe, Tylo | - | - | LIVE-PROVEN 2026-11-15 |
| B45-tylo-bop-transfer | operator | tylo-park-nerv | b29_jool_kerbin | Kerbin, Jool, Tylo, Bop | - | - | LIVE-PROVEN 2026-11-16 |
| B46-minmus-mun-transfer | operator | minmus-park-kerbalx | b30_mun_minmus | Kerbin, Mun, Minmus | - | - | LIVE-PROVEN 2026-11-16 |
| B47-jool-moon-tour | operator | jool-orbit-recorded | b47_jool_moon_tour | Jool, Laythe, Vall, Tylo | - | - | LIVE-PROVEN 2026-11-15 |
| B48-jool-laythe-bop-continuation | operator | jool-laythe-recorded | b26_laythe_vall | Jool, Laythe, Vall, Bop | - | - | LIVE-PROVEN 2026-11-16 |
| B5-mun-flyby | nightly | b2-lko-craft | b5_mun_flyby | Kerbin, Mun | - | - | Cross-SOI cohesive coast recording (Kerbi... |
| B6-minmus-flyby | nightly | b2-lko-craft | b6_minmus_flyby | Kerbin, Minmus | - | - | LIVE-PROVEN 2026-10-01 |
| B7-duna-flyby | nightly | b2-lko-craft | b7_duna_flyby | Kerbin, Duna | - | - | Multi-SOI interplanetary recording (Kerbi... |

### BAY (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| BAY-1-runway-cargo-bays | operator | gs1-two-stage-pad | bay1_runway_bays | Kerbin | rewind-to-launch, warp | - | LIVE-PROVEN 2026-09-26 |

### BDOCK (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| BDOCK-1-station-interceptor | nightly | bdock-station-pad | bdock_dock_transfer | Kerbin | - | - | LIVE-PROVEN 2026-10-16 |
| BDOCK-2-second-dock-harvest | operator | bdock-recorded | bdock_second_dock | Kerbin | - | - | LIVE-PROVEN 2026-09-23 |

### BOAT (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| BOAT-1-mallard-water-taxi | operator | mallard-splashed | boat1_water_taxi | Kerbin | rewind-to-launch, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |

### CA (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CA-1-commit-abort-booster-live | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | warp, physics-warp | - | LIVE-PROVEN 2026-09-26 |

### CI (11)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CI-1-eva-switch-bg-member | daily | eva2-lko-crewed | seam | Kerbin | warp, vessel-switch | - | LIVE-PROVEN 2026-09-08 |
| CI-10-chain-tip-ksc-marker-recover-no-respawn | nightly | gloops-airshow +inj chain-tip-r... | seam | Kerbin | warp, scene-change | - | LIVE-PROVEN 2026-10-04 |
| CI-11-chain-tip-krpc-recover-no-respawn | nightly | gloops-airshow +inj chain-tip-r... | ci11_krpc_recover | Kerbin | warp, scene-change | - | LIVE-PROVEN 2026-10-04 |
| CI-2-refly-claim-tip-pid | operator | bdock-recorded | seam | Kerbin | re-fly, time-jump | - | LIVE-PROVEN 2026-09-08 |
| CI-3-chain-rederive-readback | operator | bdock-recorded | seam | Kerbin | re-fly, reload, save | - | LIVE-PROVEN 2026-09-22 |
| CI-4-cross-tree-chain-pooled | operator | bdock-second-dock-recorded | seam | Kerbin | re-fly | - | LIVE-PROVEN 2026-09-23 |
| CI-5-background-event-claim | nightly | eva2-lko-crewed +inj background... | seam | Kerbin | warp | - | LIVE-PROVEN 2026-09-26 |
| CI-6-chain-tip-recover-no-respawn | nightly | gloops-airshow +inj chain-tip-r... | seam | Kerbin | warp, scene-change, vessel-switch | - | RE-FLOWN 2026-10-16 2026-10-16_2307 PASS... |
| CI-7-chain-tip-ts-recover-no-respawn | nightly | gloops-airshow +inj chain-tip-r... | seam | Kerbin | warp, scene-change | - | LIVE-PROVEN 2026-10-03 |
| CI-8-chain-tip-original-pid | nightly | gloops-airshow +inj chain-tip-r... | seam | Kerbin | warp | - | LIVE-PROVEN 2026-10-03 |
| CI-9-chain-tip-dock | nightly | eva2-lko-crewed +inj chain-tip-... | ci9_tip_dock | Kerbin | warp | - | LIVE-PROVEN 2026-10-03 |

### CL (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CL-2-pod-impact-ledger | nightly | career-pad-craft | cl1_pod_impact | Kerbin | scene-change | - | LIVE-PROVEN 2026-07-30 |
| CL-4-refly-crew-standin | operator | career-pad-craft +inj rewind-cr... | cl3_refly_crew_tombstone | Kerbin | time-jump | - | LIVE-PROVEN 2026-09-09 |

### CLK (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CLK-1-ground-station-contract-untagged | nightly | career-earned-pad | seam | - | - | ClockCauseScope | LIVE-PROVEN 2026-10-25 |

### CN (6)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CN-1-ghost-commnet-relay | nightly | gloops-airshow +inj ghost-commn... | seam | - | warp | GhostCommNet | LIVE-PROVEN 2026-09-26 |
| CN-1T-ghost-commnet-relay-ts | nightly | gloops-airshow +inj ghost-commn... | seam | Kerbin | warp | GhostCommNet | LIVE-PROVEN 2026-09-26 |
| CN-2-ghost-commnet-live-probe | nightly | duna-park-probe +inj ghost-comm... | seam | Duna | warp | GhostCommNetLive | LIVE-PROVEN 2026-09-26 |
| CN-3-ghost-commnet-timeline-warp | nightly | gloops-airshow +inj ghost-commn... | seam | - | warp | GhostCommNetTimeline | LIVE-PROVEN 2026-09-26 |
| CN-4-ghost-commnet-relay-across-rewind | operator | gloops-airshow +inj ghost-commn... | seam | - | rewind-to-launch, warp, scene-change | GhostCommNet | LIVE-PROVEN 2026-11-01 |
| CN-5-ghost-relay-after-mid-replay-save-load | operator | gloops-airshow +inj ghost-commn... | seam | - | reload, save, warp | GhostCommNet | LIVE-PROVEN 2026-11-01 |

### COM (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| COM-1-comet-klaw-grab | nightly | com-klaw-lko | ast_klaw_grab | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-15 |

### CPR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CPR-1-contract-parameter-paid-once-after-rewind | operator | cs-career-new | seam | Kerbin | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-11-23 |

### CS (13)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CS-0-new-career-game | operator | fresh-career | seam | - | save, new-game | - | LIVE-PROVEN 2026-10-25 |
| CS-0-new-science-game | operator | fresh-science | seam | - | save, new-game | - | LIVE-PROVEN 2026-10-25 |
| CS-1-science-first-hop | operator | cs-science-new | science_bench_recover | - | save, scene-change | - | LIVE-PROVEN 2026-10-26 |
| CS-2-science-engineering101 | operator | cs-science-flea | science_bench_recover | - | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-26 |
| CS-3-career-first-contracts | operator | cs-career-new | science_bench_recover | - | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-26 |
| CS-3b-career-hire-upgrade | operator | cs-career-contracts | seam | - | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-26 |
| CS-4-career-orbit-return | operator | cs-career-mc1 | b4_reentry_career | - | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-26 |
| CS-4b-career-tracking-station | operator | cs-career-orbit | seam | - | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-27 |
| CS-5-career-mun-flyby | operator | cs-career-ts1 | b5_mun_flyby_return | Mun | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-27 |
| CS-6-career-mun-orbit | operator | cs-career-mun1 | b11_mun_orbit_return | Mun | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-27 |
| CS-6b-career-astronaut-complex | operator | cs-career-mun2 | seam | Mun | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| CS-6c-career-mun-science | operator | cs-career-mun3 | b11_mun_science_return | Mun | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| CS-7-career-mun-landing | operator | cs-career-mun4 | b13_mun_landing_return | Mun | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-11-07 |

### CT (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CT-1-contracts-discard-economy | operator | career-pad-craft | seam | - | - | Contracts | LIVE-PROVEN 2026-10-26 |
| CT-2-parttest-rebuilt-experimental-part | operator | cs-career-mun3 | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-22 |
| CT-3-vessel-repair-rebuilt-at-space-center | operator | cs-career-mun4-vessel-repair | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-22 |

### CTW (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CTW-1-contract-goal-twin-withdrawn | operator | career-new-goal-twin | seam | Kerbin | rewind-to-launch, save, warp, scene-change | - | LIVE-PROVEN 2026-11-07 |

### CX (6)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| CX-1-docked-crew-transfer-eva-hop | nightly | bdock-second-dock-recorded | seam | Kerbin | reload, save | - | LIVE-PROVEN 2026-10-17 |
| CX-2-hitchhiker-decouple-crew | nightly | hitchhiker-pad | seam | Kerbin | rewind-to-launch, save, warp, scene-change | - | LIVE-PROVEN 2026-10-22 |
| CX-3-crew-swapped-into-station-at-dock | nightly | bdock-second-dock-recorded | seam | Kerbin | save | - | LIVE-PROVEN 2026-10-24 |
| CX-4-foreign-tree-recording-closes-hold | nightly | kerbin-splashdown-recorded | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-25 |
| CX-5-idle-visit-frees-held-kerbal | nightly | kerbin-splashdown-recorded | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-25 |
| CX-6-idle-flight-seat-foreign-rover-kept | nightly | seat-rover-pad | seam | Kerbin | save, warp, scene-change | - | LIVE-PROVEN 2026-10-25 |

### DV (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| DV-1-ascent-deltav-snapshots | nightly | b2-lko-craft | b2_lko_ascent | Kerbin | save, scene-change | - | LIVE-PROVEN 2026-11-22 |

### EVA (17)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| EVA-1-pad-flag | nightly | gloops-airshow | seam | Kerbin | - | - | LIVE-PROVEN 2026-07-24 |
| EVA-10-mun-ground-science-cluster | nightly | mun-landing-recorded | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-06 |
| EVA-11-construction-foreign-claim | nightly | eva-construct-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-15 |
| EVA-12-cargo-from-foreign-kerbal-rewind | nightly | eva-construct-pad | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-23 |
| EVA-13-construction-cross-tree-adopted-hold | nightly | eva-construct-pad | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-24 |
| EVA-14-foreign-board-claim-rewind | nightly | eva-construct-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-25 |
| EVA-15-orbiting-eva-deferred-spawn | nightly | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-10-24 |
| EVA-16-construction-drop-foreign-part | nightly | eva-construct-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-01 |
| EVA-17-orbit-jetpack-hop-rewind | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp | FlightGhostPosition | LIVE-PROVEN 2026-11-24 |
| EVA-2-orbital-board | daily | eva2-lko-crewed | seam | Kerbin | - | - | LIVE-PROVEN 2026-07-24 |
| EVA-3-multi-kerbal | nightly | eva3-pad-3crew | seam | Kerbin | - | - | LIVE-PROVEN 2026-07-24 |
| EVA-4-atmo-chute | nightly | b1-pad-craft | eva4_atmo_chute | Kerbin | - | - | LIVE-PROVEN 2026-07-24 |
| EVA-5-ground-science-place-pickup | nightly | gloops-airshow | seam | Kerbin | warp | - | RE-FLOWN 2026-10-16 2026-10-16_2208 PASS... |
| EVA-6-placed-part-spawn-after-rewind | nightly | kerbin-splashdown-recorded | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-09-27 |
| EVA-7-crew-inventory-spawn-after-rewind | nightly | kerbin-splashdown-recorded | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-09-27 |
| EVA-8-ground-science-cluster-place | nightly | kerbin-splashdown-recorded | seam | Kerbin | save | - | LIVE-PROVEN 2026-09-28 |
| EVA-9-ground-science-cluster-spawn-flight | nightly | kerbin-splashdown-recorded | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-06 |

### EX (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| EX-1-ghost-extension-past-endut | nightly | pad-runway-pair | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-09-23 |
| EX-2-single-point-held-ghost | nightly | eva2-lko-crewed +inj single-poi... | seam | Kerbin | warp | - | LIVE-PROVEN 2026-09-24 |

### FAR (6)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FAR-1-ike-rewind-to-launch-career | operator | duna-ike-recorded-career | seam | Duna, Ike | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-11-14 |
| FAR-2-duna-relaunch-rewind-keeps-landed | operator | duna-park-recorded | seam | Duna | rewind-to-launch, reload, save, scene-change | LeafVesselPlacement | LIVE-PROVEN 2026-11-14 |
| FAR-3-ike-revert-to-launch | operator | duna-ike-recorded | seam | Duna, Ike | revert, save, warp, scene-change | LeafVesselPlacement | LIVE-PROVEN 2026-11-14 |
| FAR-4-gilly-eva-switch-bg-member | operator | eve-gilly-recorded | seam | Eve, Gilly | vessel-switch | - | LIVE-PROVEN 2026-11-14 |
| FAR-5-laythe-physics-warp-record | operator | jool-laythe-recorded | seam | Jool, Laythe | warp, physics-warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-14 |
| FAR-6-ike-loop-watch-quickload | operator | duna-ike-recorded | seam | Duna, Ike | reload, save, warp | - | LIVE-PROVEN 2026-11-14 |

### FFC (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FFC-1-time-jump-across-committed-ksc-rows | operator | career-pad-craft | seam | Kerbin | rewind-to-launch, save, warp, time-jump +1 | - | LIVE-PROVEN 2026-11-20 |

### FGV (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FGV-1-fairing-ghost-visual | operator | eva2-lko-crewed | seam | Kerbin | warp | FairingGhostVisual | LIVE-PROVEN 2026-11-07 |

### FLG (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FLG-1-pickup-rewind | nightly | gloops-airshow | seam | Kerbin | rewind-to-launch, warp | - | LIVE-PROVEN 2026-11-04 |
| FLG-2-flag-window-passed-without-ghost | nightly | gloops-airshow | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-24 |

### FLY (5)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FLY-1-ts-fly-committed-clone | nightly | kerbin-return-recorded | seam | Kerbin | vessel-switch | - | LIVE-PROVEN 2026-11-20 |
| FLY-2-ksc-marker-fly-surface-defer | nightly | kerbin-splashdown-recorded | seam | Kerbin | vessel-switch | - | LIVE-PROVEN 2026-11-20 |
| FLY-3-ts-fly-standalone | nightly | eva2-lko-crewed | seam | Kerbin | vessel-switch | - | LIVE-PROVEN 2026-11-20 |
| FLY-4-alarm-jump-from-ts | nightly | eva2-lko-crewed | seam | Kerbin | vessel-switch | - | LIVE-PROVEN 2026-11-20 |
| FLY-5-alarm-jump-from-ksc | nightly | eva2-lko-crewed | seam | Kerbin | vessel-switch | - | LIVE-PROVEN 2026-11-20 |

### FORGE (10)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FORGE-b17-duna-pad | operator | bdock-forge-base | forge_station | Kerbin, Duna | save | - | FLOWN 2026-08-05 |
| FORGE-b18-dres-pad | operator | bdock-forge-base | forge_station | Kerbin, Dres | save | - | : run _2125 PASS attempt 1 (105 s) ... |
| FORGE-bdock-station | operator | bdock-forge-base | forge_station | Kerbin | save | - | LIVE-PROVEN 2026-07-23 |
| FORGE-eva2-lko | operator | bdock-forge-base | forge_lko | Kerbin | save | - | LIVE-PROVEN 2026-07-24 |
| FORGE-eva3-pad | operator | bdock-forge-base | forge_station | Kerbin | save | - | LIVE-PROVEN 2026-07-24 |
| FORGE-gs1-two-stage | operator | bdock-forge-base | forge_station | Kerbin | save | - | FLOWN 2026-08-05 |
| FORGE-gs2-orbital-stack | operator | bdock-forge-base | forge_lko | Kerbin | save | - | FIXTURE-FORGE, not a coverage flight. The... |
| FORGE-logi-pad | operator | bdock-forge-base | forge_station | Kerbin | save | - | forge mechanism |
| FORGE-psub-pad | operator | bdock-forge-base | forge_station | Kerbin | save | - | LIVE-PROVEN 2026-11-18 |
| FORGE-refly-midsession | operator | gloops-airshow +inj rewind-b9 | seam | - | re-fly, save, time-jump | - | LIVE-PROVEN 2026-11-23 |

### FXR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| FXR-1-ghost-engine-fx-across-refly-and-retry | operator | refly-split-crewed-merged | seam | Kerbin | re-fly, revert, reload, save, warp | GhostFxIntegrity | LIVE-PROVEN 2026-11-17 |

### GC (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| GC-3-other-generation-tree-kept-inert | operator | career-earned-pad-oldgen | seam | Kerbin | reload, save | - | LIVE-PROVEN 2026-10-22 |
| GC-4-vessel-cycle-skips-ghost-marker | nightly | eva2-lko-crewed +inj chain-tip-... | seam | Kerbin | warp, vessel-switch | - | LIVE-PROVEN 2026-10-22 |

### GM (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| GM-1-scenario-saves-inert | operator | gm-scenario-modes | seam | - | reload, save | - | LIVE-PROVEN 2026-11-23 |

### GPOS (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| GPOS-1-kerbalx-ascent-ghost-position | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | rewind-to-launch, warp, scene-change | FlightGhostPosition | LIVE-PROVEN 2026-11-20 |
| GPOS-2-eva-chute-relative-ghost-position | operator | b1-pad-craft | eva4_atmo_chute | Kerbin | rewind-to-launch, warp, scene-change | FlightGhostPosition | LIVE-PROVEN 2026-11-22 |

### GS (11)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| GS-1-auto-chute-booster | operator | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | scene-change | - | RE-FLOWN GREEN 2026-11-22 as an abandoned... |
| GS-10-kerbalx-debris-ttl | operator | gs1-two-stage-pad | kx_rewind_watch | - | - | - | LIVE-PROVEN 2026-09-23 |
| GS-11-kerbalx-debris-promotion | operator | gs1-two-stage-pad | kx_rewind_watch | - | - | - | LIVE-PROVEN 2026-09-23 |
| GS-12-kerbalx-loop-cycles | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | - | - | LIVE-PROVEN 2026-09-24 |
| GS-13-mun-v3-chain-loop-replay | nightly | mun-orbit-recorded-v3 | seam | Kerbin, Mun | warp | - | LIVE-PROVEN 2026-11-22 |
| GS-2-orbital-probe-deploy | operator | gs2-orbital-stack | gs2_orbital_probe_deploy | Kerbin | scene-change | - | 2026-09-27 (arming batch 2, branch arm-ba... |
| GS-3-switch-nudge-deployed | operator | gs2-orbital-stack | gs2_orbital_probe_deploy | Kerbin | scene-change, vessel-switch | - | RE-CUT AND RE-FLOWN 2026-10-24 (branch cl... |
| GS-6-part-event-applier-sweep | nightly | gs1-two-stage-pad | kx_rewind_watch | Kerbin | - | - | LIVE-PROVEN 2026-09-02 |
| GS-7-kerbalx-crash-watch-hold | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | - | - | LIVE-PROVEN 2026-09-08 |
| GS-8-kerbalx-zone-round-trip | nightly | gs1-two-stage-pad | kx_rewind_watch | Kerbin | - | - | JUMP GATE ADDED 2026-11-23, RE-FLOWN GREE... |
| GS-9-kerbalx-repeat-rewind | nightly | gs1-two-stage-pad | kx_rewind_watch | Kerbin | - | - | LIVE-PROVEN 2026-09-11 |

### GUI (28)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| GUI-1-census-ksc | operator | c1-gui | seam | Kerbin | - | GuiTree | LIVE-PROVEN 2026-10-06 |
| GUI-10-census-dialogs | operator | bdock-recorded | seam | Kerbin | - | - | LIVE-PROVEN 2026-09-15 |
| GUI-11-census-kerbals-crewed | operator | bdock-recorded | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-22 |
| GUI-12-census-testrunners | operator | career-earned-ksc | seam | Kerbin | - | - | FLOWN PASS 2026-09-15 |
| GUI-13-census-logistics-candidates | operator | rover-route-recorded | seam | Kerbin | - | - | RE-FLOWN PASS 2026-11-24: 2026-11-24_0722... |
| GUI-14-census-settings-and-facility | operator | fresh-career | seam | Kerbin | - | - | SPEC CHANGED 2026-09-26, RE-READ 2026-09-27: |
| GUI-15-census-career-contracts | operator | career-contract-pad | seam | Kerbin | - | - | REPOINTED 2026-09-27 TO THE TIMELINE'S CO... |
| GUI-17-census-missions-loop-mun | operator | mun-orbit-recorded | seam | Mun | - | - | RE-FLOWN PASS 2026-11-24: 2026-11-24_0722... |
| GUI-18-census-missions-loop-duna | operator | duna-direct-recorded | seam | Duna | - | - | RE-FLOWN PASS 2026-11-24: 2026-11-24_0724... |
| GUI-19-census-timeline-supersede | operator | refly-a-recorded | seam | - | - | - | FLOWN PASS 2026-09-21 |
| GUI-2-census-flight | operator | c1-gui | seam | - | - | - | LIVE-PROVEN 2026-10-06 |
| GUI-20-census-logistics-hold-second-cycle | operator | rover-relay-c-recorded | seam | - | time-jump | - | RE-FLOWN GREEN 2026-09-27 ON THE ENDPOINT... |
| GUI-21-census-logistics-hold-origin-empty | operator | rover-relay-c-recorded | seam | - | time-jump | - | FLOWN PASS 2026-09-21 |
| GUI-22-census-logistics-hold-destination-full | operator | rover-relay-c-recorded | seam | - | time-jump | - | FLOWN PASS 2026-09-21 |
| GUI-24-census-timeline-filters | operator | c1-gui | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-24 for the three-ro... |
| GUI-25-census-missions-state-sort-edit | operator | interbody-route-recorded | seam | Kerbin | - | - | RE-FLOWN PASS 2026-11-24: 2026-11-24_0646... |
| GUI-26-census-createroute-and-running-batch | operator | rover-route-recorded | seam | Kerbin | - | - | FLOWN PASS 2026-09-22 |
| GUI-27-census-missions-include | operator | bdock-recorded | seam | Kerbin | - | - | RE-FLOWN PASS 2026-11-24: 2026-11-24_0723... |
| GUI-28-census-stock-screens | operator | stock-screen-census | seam | Kerbin | scene-change | - | RE-FLOWN PASS 2026-09-29 |
| GUI-29-census-wide-windows-1280 | operator | c1-gui | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-27 on main after #1... |
| GUI-3-census-logistics-routes | operator | interbody-route-recorded | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-24 at 1920x1080 |
| GUI-30-census-stock-screens-sandbox | operator | fresh-sandbox | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-29 on the post-main... |
| GUI-4-census-missions-docked | operator | bdock-recorded | seam | Kerbin | - | - | RE-FLOWN PASS 2026-11-24 ON THE TIGHTENED... |
| GUI-5-census-career-ksc | operator | career-earned-ksc | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-27 for the Career w... |
| GUI-6-census-flight-playback | operator | gloops-airshow +inj part-showcase | seam | - | time-jump | - | RESHAPED AND RE-FLOWN PASS 2026-10-06 |
| GUI-7-census-flight-recording | operator | b1-pad-craft | seam | Kerbin | - | - | RE-FLOWN PASS 2026-10-04 |
| GUI-8-census-empty-states | operator | fresh-science | seam | Kerbin | - | - | RE-FLOWN PASS 2026-09-24 for the Career s... |
| GUI-9-playback-toggle-map-scope | operator | gloops-airshow +inj part-showcase | seam | - | warp, time-jump | - | LIVE-PROVEN 2026-09-14 |

### H (75)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| H10-finalize-backfill | nightly | gloops-airshow | seam | - | - | FinalizeBackfill | LIVE-PROVEN 2026-07-27 |
| H11-pipeline-anchor | nightly | gloops-airshow | seam | - | - | Pipeline-Anchor | LIVE-PROVEN 2026-07-27 |
| H12-switch-segment | nightly | gloops-airshow | seam | - | - | SwitchSegment | LIVE-PROVEN 2026-07-27 |
| H13-ksp-api-smoke | nightly | gloops-airshow | seam | - | - | KSP | LIVE-PROVEN 2026-07-27 |
| H14-corpus-data-health | nightly | gloops-airshow +inj all-synthetic | seam | - | - | DataHealth | LIVE-PROVEN 2026-07-27 |
| H15-corpus-ghost-visuals | nightly | gloops-airshow +inj all-synthetic | seam | - | - | GhostVisuals | LIVE-PROVEN 2026-07-27 |
| H16-corpus-spawn-health | nightly | gloops-airshow +inj all-synthetic | seam | - | - | SpawnHealth | LIVE-PROVEN 2026-07-27 |
| H17-flight-integration | nightly | gloops-airshow +inj all-synthetic | seam | - | - | FlightIntegration | LIVE-PROVEN 2026-07-27 |
| H18-pipeline-smoothing | daily | gloops-airshow | seam | - | - | Pipeline-Smoothing | LIVE-PROVEN 2026-07-27 |
| H19-recording-finalization | nightly | gloops-airshow | seam | - | - | RecordingFinalization | LIVE-PROVEN 2026-07-27 |
| H20-eva-spawn-position | nightly | gloops-airshow | seam | Kerbin | - | EvaSpawnPosition | LIVE-PROVEN 2026-07-27 |
| H21-scene-exit-merge-isolated | nightly | b2-lko-craft | seam | Kerbin | - | SceneExitMerge | LIVE-PROVEN 2026-07-27 |
| H22-ui-complexity-mode | daily | gloops-airshow | seam | - | - | UiComplexityMode | LIVE-PROVEN 2026-08-01 |
| H23-tracking-station | daily | gloops-airshow | seam | - | - | TrackingStation | LIVE-PROVEN 2026-07-30 |
| H24-ksp-api-sanity | nightly | gloops-airshow | seam | - | - | KspApiSanity | LIVE-PROVEN 2026-08-04 |
| H25-serialization | nightly | gloops-airshow | seam | - | - | Serialization | LIVE-PROVEN 2026-08-04 |
| H26-log-contracts | nightly | career-pad-craft | seam | - | - | LogContracts | LIVE-PROVEN 2026-08-04 |
| H27-diagnostics | nightly | gloops-airshow +inj all-synthetic | seam | - | - | Diagnostics | LIVE-PROVEN 2026-08-04 |
| H28-map-presence | nightly | gloops-airshow +inj all-synthetic | seam | - | - | MapPresence | RE-PINNED 2026-08-05 |
| H29-localized-name | nightly | gloops-airshow | seam | - | - | LocalizedName | LIVE-PROVEN 2026-08-04 |
| H30-ghost-audio | nightly | gloops-airshow +inj all-synthetic | seam | - | - | GhostAudio | LIVE-PROVEN 2026-08-04 |
| H31-crew-reservation | nightly | b2-lko-craft | seam | Kerbin | - | CrewReservation | LIVE-PROVEN 2026-08-04 |
| H32-snapshot-baseline | nightly | gloops-airshow | seam | - | - | SnapshotBaseline | LIVE-PROVEN 2026-08-11 |
| H33-recorded-signals | nightly | gloops-airshow +inj all-synthetic | seam | - | - | RecordedSignals | LIVE-PROVEN 2026-08-11 |
| H34-logistics-inter-body | nightly | gloops-airshow | seam | Kerbin | - | Logistics | LIVE-PROVEN 2026-08-11 |
| H35-logistics-route-proof | nightly | bdock-recorded | seam | - | - | Logistics | LIVE-PROVEN 2026-08-11 |
| H36-playback-fidelity | nightly | gloops-airshow | seam | - | - | PlaybackFidelity | LIVE-PROVEN 2026-08-12 |
| H37-part-event-fidelity | nightly | gloops-airshow | seam | - | - | PartEventFidelity | LIVE-PROVEN 2026-08-12 |
| H38-logistics-isolated | nightly | logi-cargo-pad | seam | - | - | Logistics | RE-FLOWN 2026-10-16 2026-10-16_2155 PASS... |
| H39-logistics-isolated-bdock | nightly | bdock-recorded | seam | - | - | Logistics | RE-FLOWN 2026-10-16 2026-10-16_2158 PASS... |
| H40-logistics-isolated-depot-route | nightly | depot-route-recorded | seam | - | - | Logistics | RE-FLOWN 2026-10-16 2026-10-16_2201 PASS... |
| H41-logistics-grapple-isolated | nightly | logi-cargo-pad | seam | - | - | LogisticsGrapple | RE-PINNED 2026-11-15 (ROCK-GHOST-PREFAB-M... |
| H42-claw-couple | nightly | gloops-airshow | seam | - | - | ClawCouple | LIVE-PROVEN 2026-08-28 |
| H43-terrain-clearance | nightly | gloops-airshow | seam | - | - | TerrainClearance | FLOWN 2026-08-28_2218, PASS attempt 1, 51... |
| H44-ghost-map-trackstation | nightly | gloops-airshow +inj all-synthetic | seam | Kerbin | - | GhostMap | FLOWN 2026-08-28_2218, PASS attempt 1, 68... |
| H45-stock-ui-overlay | nightly | career-earned-pad | seam | Kerbin | - | StockUiOverlay | 2026-11-23 (test-gap list batch 4, branch... |
| H46-settings | nightly | gloops-airshow | seam | - | - | Settings | RE-FLOWN PASS 2026-10-06 |
| H47-map-view | nightly | gloops-airshow | seam | Kerbin | - | MapView | LIVE-PROVEN 2026-08-28 |
| H48-ledger-drawdown | nightly | fresh-career | seam | Kerbin | - | Ledger | LIVE-PROVEN 2026-08-28 |
| H49-tree-integrity | nightly | gloops-airshow +inj all-synthetic | seam | - | - | TreeIntegrity | FLOWN 2026-08-28_2223, PASS attempt 1, 71... |
| H5-invariants-corpus | daily | gloops-airshow +inj all-synthetic | seam | - | - | RecordingInvariants | CORPUS RE-MEASURED 2026-08-11 |
| H50-ghost-chains | nightly | gloops-airshow +inj all-synthetic | seam | - | - | GhostChains | FLOWN 2026-08-28_2224, PASS attempt 1, 71... |
| H51-save-load | nightly | mun-orbit-recorded | seam | Mun | - | SaveLoad | LIVE-PROVEN 2026-08-28 |
| H52-reentry-fx | nightly | gloops-airshow | seam | - | - | ReentryFx | LIVE-PROVEN 2026-08-28 |
| H53-scene-and-patch | nightly | gloops-airshow +inj all-synthetic | seam | - | - | SceneAndPatch | FLOWN 2026-08-28_2227, PASS attempt 1, 70... |
| H54-missions | nightly | duna-one-recorded | seam | Duna | - | Missions | FLOWN 2026-08-28_2228, PASS attempt 1, 52... |
| H55-route-dock-capture-isolated | nightly | logi-cargo-pad | seam | - | - | RouteDockCapture | LIVE-PROVEN 2026-09-02 |
| H56-route-dock-capture-landed | nightly | rover-route-recorded | seam | - | - | RouteDockCapture | LIVE-PROVEN 2026-09-02 |
| H57-route-start-docked-origin-landed | nightly | rover-route-recorded | seam | - | - | RouteStartDockedOrigin | LIVE-PROVEN 2026-09-02 |
| H58-route-rewind-to-launch | nightly | rover-route-recorded | seam | - | rewind-to-launch, save, time-jump | - | LIVE-PROVEN 2026-09-02 |
| H59-surface-route-map-lines | operator | rover-route-recorded | seam | - | reload, save, warp, time-jump, scene-change | RouteGhostPlacement | RE-FLOWN GREEN 2026-11-17 WITH THE ROUTE... |
| H6-route-rewind-timeline | daily | gloops-airshow | seam | - | - | RouteRewindTimeline | LIVE-PROVEN 2026-07-24 |
| H60-route-escrow-contention | nightly | logi-cargo-pad | seam | - | - | RouteEscrowContention | LIVE-PROVEN 2026-11-16 |
| H61-autorecord-isolated | nightly | gs1-two-stage-pad | seam | - | - | AutoRecord | LIVE-PROVEN 2026-09-06 |
| H62-coalescer-isolated | nightly | coalescer-pad | seam | - | - | Coalescer | LIVE-PROVEN 2026-09-06 |
| H63-merge-dialog-isolated | nightly | gloops-airshow | seam | - | - | MergeDialog | LIVE-PROVEN 2026-09-06 |
| H64-revert-flow-isolated | nightly | gs1-two-stage-pad | seam | - | - | RevertFlow | LIVE-PROVEN 2026-09-06 |
| H65-quickload-resume-isolated | nightly | gs1-two-stage-pad | seam | - | - | QuickloadResume | LIVE-PROVEN 2026-09-06 |
| H66-playback-control-isolated | nightly | gloops-airshow | seam | - | - | PlaybackControl | LIVE-PROVEN 2026-09-06 |
| H67-automerge-commit-isolated | nightly | gs2-orbital-stack | seam | - | - | AutoMergeCommit | LIVE-PROVEN 2026-09-06 |
| H68-autorecord-orbiting | nightly | gs2-orbital-stack | seam | - | - | AutoRecord | LIVE-PROVEN 2026-09-07 |
| H69-autorecord-landed | nightly | rover-route-recorded | seam | - | - | AutoRecord | LIVE-PROVEN 2026-09-07 |
| H7-trajectory-math | nightly | gloops-airshow | seam | - | - | TrajectoryMath | LIVE-PROVEN 2026-07-27 |
| H70-autorecord-pad-crew | nightly | eva3-pad-3crew | seam | - | - | AutoRecord | LIVE-PROVEN 2026-09-07 |
| H71-resource-topbar-ksc | nightly | career-earned-ksc | seam | Kerbin | - | ResourceTopBar | LIVE-PROVEN 2026-09-07 |
| H72-kerbal-inventory-spawn | nightly | b1-pad-craft | seam | Kerbin | - | KerbalInventorySpawn | LIVE-PROVEN 2026-09-27 |
| H73-logistics-isolated-career | nightly | logi-cargo-career | seam | - | - | Logistics | LIVE-PROVEN 2026-10-21 |
| H74-rover-relay-c-dispatch-career | nightly | rover-relay-c-recorded-career | seam | - | save, time-jump | - | LIVE-PROVEN 2026-10-21 |
| H75-rover-relay-c-second-cycle-hold-career | nightly | rover-relay-c-recorded-career | seam | - | save, time-jump | - | LIVE-PROVEN 2026-10-21 |
| H76-logistics-isolated-depot-route-career | nightly | depot-route-recorded-career | seam | - | - | Logistics | LIVE-PROVEN 2026-10-21 |
| H77-logistics-inter-body-career | nightly | gloops-airshow-career | seam | Kerbin | - | Logistics | LIVE-PROVEN 2026-10-21 |
| H78-logistics-isolated-bdock-career | nightly | bdock-recorded-career | seam | - | - | Logistics | LIVE-PROVEN 2026-10-21 |
| H79-route-rewind-to-launch-career | nightly | rover-route-recorded-career | seam | - | rewind-to-launch, save, time-jump | - | LIVE-PROVEN 2026-10-21 |
| H8-spawn-rotation | nightly | gloops-airshow | seam | - | - | SpawnRotation | LIVE-PROVEN 2026-07-27 |
| H9-incomplete-ballistic | nightly | gloops-airshow | seam | - | - | IncompleteBallistic | LIVE-PROVEN 2026-07-27 |

### HC (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| HC-1-hard-career-ledger | nightly | career-pad-craft-hard | seam | - | rewind-to-launch | LedgerGroundTruth | LIVE-PROVEN 2026-09-29 |
| HC-2-hard-career-earn-spend | nightly | career-science-pad-hard | science_bench_recover | Kerbin | rewind-to-launch, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-03 |

### HV (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| HV-1-harvest-route-analysis | nightly | logi-cargo-pad +inj drill-harve... | seam | - | - | Logistics | LIVE-PROVEN 2026-09-25 |
| HV-2-scan-ledger-drill-rates | nightly | fresh-career | seam | Kerbin | warp | HarvestCapScanLedger | LIVE-PROVEN 2026-10-16 |

### IR (8)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| IR-1-route-f9-across-a-dispatch | nightly | rover-route-recorded | seam | - | reload, save, time-jump | - | LIVE-PROVEN 2026-10-24 |
| IR-12-multistop-rewind-between-windows | nightly | rover-relay-c-recorded | seam | - | rewind-to-launch, save, warp, time-jump | - | LIVE-PROVEN 2026-11-07 |
| IR-13-route-window-revert-retires-window-row | nightly | rover-route-recorded | seam | - | rewind-to-launch, revert, save, time-jump +1 | - | LIVE-PROVEN 2026-11-06 |
| IR-2-route-stock-revert-mid-cycle | nightly | rover-route-recorded | seam | - | revert, time-jump, scene-change | - | LIVE-PROVEN 2026-10-24 |
| IR-3-route-refly-discard-merge | nightly | rover-route-recorded | seam | - | re-fly, revert, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-31 |
| IR-5-mun-route-dispatch-delivery | nightly | interbody-route-recorded | seam | Mun | save, warp | - | LIVE-PROVEN 2026-10-16 |
| IR-6-route-warp-across-cycles | nightly | rover-route-recorded | seam | - | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-11-01 |
| IR-6T-route-warp-in-tracking-station | operator | rover-route-recorded | seam | - | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-11-23 |

### KB (5)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| KB-1-ksc-building-repair-ledger | daily | career-earned-ksc | seam | Kerbin | reload, save, scene-change | - | LIVE-PROVEN 2026-09-23 |
| KB-2-ksc-repair-block-after-rewind | nightly | stock-screen-census-repair | seam | Kerbin | - | - | LIVE-PROVEN 2026-09-27 |
| KB-3-ksc-click-blocks-after-rewind | nightly | stock-screen-census | seam | Kerbin | - | - | LIVE-PROVEN 2026-09-27 |
| KB-4-ksc-click-blocks-remaining | nightly | stock-screen-census | seam | Kerbin | - | - | LIVE-PROVEN 2026-09-28 |
| KB-5-flight-eva-block-held-kerbal | nightly | k2-held-kerbal-pad | seam | - | warp, vessel-switch | - | LIVE-PROVEN 2026-09-28 |

### KD (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| KD-1-career-death-rewind | operator | career-pad-craft | cl1_pod_impact | Kerbin | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-26 |

### KSS (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| KSS-1-mun-lander-ksc-end-spawn | operator | b2-lko-craft | b13_mun_landing | Kerbin, Mun | rewind-to-launch, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| KSS-2-orbit-deferred-ksc-then-ts | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-11-20 |

### L (22)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| L1-dismiss-kerbal-career | daily | fresh-career | seam | Kerbin | - | - | LIVE-PROVEN 2026-08-17 |
| L1-hire-kerbal-career | daily | fresh-career | seam | Kerbin | - | - | LIVE-PROVEN 2026-07-23 |
| L1-passive-sandbox | daily | fresh-sandbox | seam | Kerbin | - | GameActionsHealth | RE-PROVEN 2026-07-26 |
| L1-research-node-career | daily | fresh-career | seam | - | - | - | LIVE-PROVEN 2026-07-23 |
| L1-research-node-science | daily | fresh-science | seam | - | - | - | LIVE-PROVEN 2026-07-23 |
| L1-upgrade-facility-career | daily | fresh-career | seam | Kerbin | - | - | LIVE-PROVEN 2026-07-23 |
| L10-career-click-reserve-after-rewind | nightly | career-earned-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-22 |
| L11-career-part-upgrade-after-rewind | nightly | career-pad-craft-hard | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-10-23 |
| L12-contract-reached-outcome-blocks-fail | nightly | career-earned-pad | seam | - | - | ContractReachedOutcome | LIVE-PROVEN 2026-10-24 |
| L13-career-ground-truth-after-refly-and-f9 | operator | career-gs1-two-stage-pad | gs1_auto_chute_booster_ca... | Kerbin | re-fly, reload, save, warp, physics-warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-25 |
| L14-contract-requirements-loss-terminal-row | nightly | career-requirements-loss-pad | seam | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-24 |
| L2-ledger-groundtruth-career | nightly | career-pad-craft | seam | - | - | LedgerGroundTruth | RE-FLOWN GREEN 2026-10-22 for PR #2175: 2... |
| L3-career-science-recover | nightly | career-science-pad | science_bench_recover | Kerbin | - | - | RE-FLOWN 2026-10-16 2026-10-16_2221 PASS... |
| L3-strategy-currency-conversion | nightly | strategy-career | seam | Kerbin | - | StrategyLifecycle | RE-FLOWN 2026-10-16 2026-10-16_2228 PASS... |
| L3-strategy-exchanger-floor | nightly | fresh-career | seam | Kerbin | - | StrategyLifecycle | RE-FLOWN 2026-10-16 2026-10-16_2229 PASS... |
| L4-ledger-groundtruth-strict | nightly | career-earned-pad | seam | - | - | LedgerGroundTruth | CLAIMED 2026-08-20 AT LAST, AFTER TWO COR... |
| L5-career-contract-complete | nightly | career-contract-pad | science_bench_recover | Kerbin | - | - | RE-FLOWN 2026-10-16 2026-10-16_2230 PASS... |
| L6-career-same-name-natural-dwell | operator | career-same-name-pad | science_bench_recover | Kerbin | - | - | AUTHORED AND FLOWN GREEN THE SAME DAY, 20... |
| L6-career-same-name-recover | operator | career-same-name-pad | science_bench_recover | Kerbin | - | - | LIVE-PROVEN 2026-09-02 |
| L7-career-idless-same-name-xp-refusal | nightly | career-idless-same-name-pad | science_bench_recover | Kerbin | - | - | LIVE-PROVEN 2026-10-01 |
| L8-career-repeated-rewinds | nightly | career-pad-craft | seam | Kerbin | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-17 |
| L9-career-floor-debt-after-rewind | nightly | career-earned-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-22 |

### LAB (18)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LAB-1-converter-jump-lab-ledger | nightly | fresh-career | seam | Kerbin | warp | ConverterJumpLab | LIVE-PROVEN 2026-10-17 |
| LAB-10-lab-refly-supersede | operator | lab-lko-seeded-transmit | seam | Kerbin | re-fly, reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-18 |
| LAB-10b-lab-refly-discard | operator | lab-lko-seeded-transmit | seam | Kerbin | re-fly, revert, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-18 |
| LAB-11-lab-transmit-ghost-relay | operator | lab-lko-relay +inj ghost-commne... | seam | Kerbin | warp | - | LIVE-PROVEN 2026-10-18 |
| LAB-12-lab-transmit-strategy-take | operator | lab-pad-career | seam | Kerbin | reload, save, warp | LedgerGroundTruth | LIVE-PROVEN 2026-10-18 |
| LAB-13-lab-process-separate-transmit | operator | lab-lko-seeded-transmit | seam | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-17 |
| LAB-13b-lab-dock-undock | operator | lab-lko-tender-rcs | d5_redock | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-20 |
| LAB-14-lab-recover | operator | lab-pad-seeded | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-17 |
| LAB-2-lab-experiments-processed | operator | lab-lko-science | seam | Kerbin | reload, save, warp | - | LIVE-PROVEN 2026-10-17 |
| LAB-3-lab-research-over-time | operator | lab-lko-science | seam | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-17 |
| LAB-3b-lab-cap-stall-and-overflow | operator | lab-lko-seeded | seam | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-17 |
| LAB-4-lab-transmit | operator | lab-pad-seeded | seam | Kerbin | reload, warp, scene-change | - | LIVE-PROVEN 2026-10-18 |
| LAB-5-lab-time-jumps | operator | lab-lko-science | seam | Kerbin | reload, save, warp, time-jump, scene-change | - | LIVE-PROVEN 2026-10-17 |
| LAB-6-lab-background-catch-up | operator | lab-lko-science | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-18 |
| LAB-7-lab-rewind-spawn | operator | lab-lko-science | seam | Kerbin | rewind-to-launch, reload, save, warp +2 | - | LIVE-PROVEN 2026-10-18 |
| LAB-8-lab-eva-crew-rules | operator | lab-lko-science | seam | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-17 |
| LAB-9a-lab-quickload-mid-research | operator | lab-lko-science | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-18 |
| LAB-9b-lab-transmit-revert | operator | lab-pad-seeded | seam | Kerbin | revert, warp | - | LIVE-PROVEN 2026-10-17 |

### LABX (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LABX-1-lab-orbit-ascent | operator | bdock-forge-base | forge_lko | Kerbin | save | - | LIVE-PROVEN 2026-10-17 |

### LAY (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LAY-1-laythe-reentry-splashdown | operator | laythe-park-nerv | b4_reentry | Kerbin, Laythe | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |

### LF (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LF-1-loop-first-run-real | nightly | pad-runway-pair | seam | Kerbin | rewind-to-launch, reload, save, warp +2 | - | LIVE-PROVEN 2026-09-23 |
| LF-2-loop-armed-rewind-first-run-real | nightly | pad-runway-pair | seam | Kerbin | rewind-to-launch, reload, save, warp +2 | - | LIVE-PROVEN 2026-09-23 |
| LF-3-predicted-fall-outlasts-loop-period | operator | gs1-armed-chute-drop | seam | Kerbin | warp, scene-change | - | LIVE-PROVEN 2026-11-24 |

### LS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LS-1-launch-site-clear-claimed-vessel | nightly | pad-runway-pair +inj launch-sit... | seam | Kerbin | scene-change | - | LIVE-PROVEN 2026-10-25 |

### LT (5)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LT-1-long-tail-flight | nightly | gloops-airshow +inj all-synthetic | seam | - | - | BackgroundSeeder, Bug289 +28 | THIRTY-ONE in-game categories in ONE FLIG... |
| LT-2-long-tail-spacecenter | nightly | fresh-sandbox | seam | Kerbin | - | Optimizer, Recording +5 | LIVE-PROVEN 2026-09-07 |
| LT-3-long-tail-career-flight | nightly | career-earned-pad | seam | - | - | Contracts, TestCommands | LIVE-PROVEN 2026-09-07 |
| LT-4-long-tail-route-flight | nightly | depot-route-recorded | seam | - | - | RouteLiveAnchor, Structure, Missions +1 | LIVE-PROVEN 2026-09-07 |
| LT-5-long-tail-playback-flight | nightly | gloops-airshow +inj part-showcase | seam | - | time-jump | PartEventFX, GhostLifecycle +5 | LIVE-PROVEN 2026-09-07 |

### LX (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| LX-1-loop-armed-refly-discard-merge | operator | rover-route-recorded | seam | - | re-fly, revert, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-17 |
| LX-2-career-loop-watch-scene-change-reload | operator | rover-route-recorded-career | seam | - | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-11-17 |

### M (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| M1-mission-loop-unit | daily | fresh-sandbox | seam | Kerbin | - | Missions | LIVE-PROVEN 2026-07-26 |
| M2-periodicity-solver | daily | fresh-sandbox | seam | Kerbin | - | Periodicity | LIVE-PROVEN 2026-07-26 |
| M3-mission-phasing-airborne-anchor | nightly | b1-pad-craft | eva4_atmo_chute | Kerbin | - | MissionPhasing | LIVE-PROVEN 2026-09-07 |

### MC (7)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| MC-1-waterfall-compat | nightly | gloops-airshow | seam | - | - | WaterfallCompat | LIVE-PROVEN 2026-08-04 |
| MC-2-restock-compat | nightly | gloops-airshow | seam | - | - | ReStockCompat | LIVE-PROVEN 2026-08-04 |
| MC-3-better-time-warp | nightly | mun-park-kerbalx | seam | Mun | - | - | PROMOTED 2026-09-15 by operator decision;... |
| MC-4-making-history-desert | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | - | - | LIVE-PROVEN 2026-09-25 |
| MC-5-persistent-rotation | operator | mun-park-kerbalx | seam | Mun | warp, time-jump | - | LIVE-PROVEN 2026-09-26 |
| MC-6-route-rewind-timeline-modded | operator | gloops-airshow | seam | - | - | RouteRewindTimeline | LIVE-PROVEN 2026-11-23 |
| MC-7-rewind-merge-modded | operator | gloops-airshow +inj rewind-b9 | seam | - | re-fly, time-jump | - | LIVE-PROVEN 2026-11-23 |

### MINE (14)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| MINE-11a-rover-full-tanks | nightly | miner-rover-full | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-10-17 |
| MINE-11b-rover-ec-starved | nightly | miner-rover-starved | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-10-17 |
| MINE-12-rover-background-miner | nightly | miner-rover-watched | seam | Kerbin | save, warp, vessel-switch | - | LIVE-PROVEN 2026-10-17 |
| MINE-13-rover-rewind-and-mine-again | nightly | miner-rover-watched | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-17 |
| MINE-14-rover-radiators-off-overheat | nightly | miner-rover-watched | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-17 |
| MINE-15-promotion-converter-seed | nightly | miner-rover-landed | seam | Kerbin | warp | PromotionConverterSeed | LIVE-PROVEN 2026-10-24 |
| MINE-2-rover-mine-rewind-warp-jump | nightly | miner-rover-landed | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-17 |
| MINE-4-rover-docked-origin-route-rewind | nightly | miner-rover-relay | seam | Kerbin | rewind-to-launch, save, warp, time-jump | - | LIVE-PROVEN 2026-11-07 |
| MINE-5a-rover-live-drill-harvest-cap | nightly | miner-rover-landed | seam | Kerbin | warp | MiningHarvest | LIVE-PROVEN 2026-11-07 |
| MINE-5b-rover-dock-depot-harvest-route | nightly | miner-rover-depot | seam | Kerbin | save, warp, time-jump | - | LIVE-PROVEN 2026-11-07 |
| MINE-6-rover-manual-flight-replay | nightly | miner-rover-watched | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-17 |
| MINE-7-rover-spawn-then-mines-on | nightly | miner-rover-watched | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-17 |
| MINE-8-rover-space-center-round-trip | nightly | miner-rover-watched | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-17 |
| MINE-9-rover-real-spawn-and-loaded-jump | nightly | miner-rover-watched | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-17 |

### MS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| MS-1-mission-leg-trim-clone | nightly | bdock-recorded | seam | - | - | - | LIVE-PROVEN 2026-09-25 |

### OC (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| OC-1-overlap-cap-per-recording | nightly | pad-runway-pair +inj overlap-cap | seam | Kerbin | warp | - | LIVE-PROVEN 2026-09-27 |

### OSC (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| OSC-1-orbit-spawn-clearance-lko | nightly | eva2-lko-crewed | seam | Kerbin | - | OrbitSpawnClearance | LIVE-PROVEN 2026-11-22 |

### PAD (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PAD-1-one-sample-orbit-exit-kept | nightly | gs2-orbital-stack | seam | Kerbin | scene-change | - | LIVE-PROVEN 2026-10-22 |

### PADG (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PADG-1-pad-rocket-ghost-hidden | nightly | seat-rover-pad | seam | Kerbin | rewind-to-launch, warp, scene-change | - | LIVE-PROVEN 2026-10-25 |

### PADX (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PADX-1-pad-destroyed-by-flight-rewind | operator | career-pad-craft | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-11-20 |
| PADX-2-pad-destroyed-by-flight-f9 | operator | career-pad-craft | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-20 |
| PADX-3-pad-row-crossed-while-recording | operator | career-pad-craft | seam | Kerbin | rewind-to-launch, save, warp, scene-change | - | LIVE-PROVEN 2026-11-21 |

### PBR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PBR-1-panel-wheel-break-ghost | operator | pbr-break-rover | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-23 |

### PP (10)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PP-1-part-parity-stock | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-22 |
| PP-10-part-parity-fx-air-restock | operator | b1-pad-craft | seam | Kerbin | - | - | LIVE-PROVEN 2026-11-23 |
| PP-2-part-parity-modded | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-22 |
| PP-3-part-parity-states-stock | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-22 |
| PP-4-part-parity-states-modded | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-22 |
| PP-5-part-parity-chutes-stock | operator | b1-pad-craft | seam | Kerbin | - | - | LIVE-PROVEN 2026-11-23 |
| PP-6-part-parity-chutes-modded | operator | b1-pad-craft | seam | Kerbin | - | - | LIVE-PROVEN 2026-11-23 |
| PP-7-part-parity-fx-stock | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-23 |
| PP-8-part-parity-fx-modded | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-23 |
| PP-9-part-parity-fx-restock | operator | eva2-lko-crewed | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-23 |

### PPB (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PPB-1-untouched-career-backup | operator | preparsek-untouched-career | seam | - | - | PreParsekBackup | LIVE-PROVEN 2026-08-29 |
| PPB-2-brandnew-career-skip | operator | preparsek-brandnew-career | seam | Kerbin | - | PreParsekBackup | LIVE-PROVEN 2026-08-29 |
| PPB-3-backup-failure-hold | operator | preparsek-hold-career | seam | - | reload, save | - | LIVE-PROVEN 2026-11-06 |

### PSP (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PSP-1-part-state-across-in-place-resume | operator | pbr-break-rover | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-26 |

### PWR (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| PWR-1-physwarp-ascent-low | nightly | b2-lko-craft | b2_lko_ascent | Kerbin | warp, physics-warp | - | LIVE-PROVEN 2026-10-02 |
| PWR-2-physwarp-ascent-medium | nightly | b2-lko-craft | b2_lko_ascent | Kerbin | warp, physics-warp | - | LIVE-PROVEN 2026-10-02 |
| PWR-3-physwarp-ascent-high | nightly | b2-lko-craft | b2_lko_ascent | Kerbin | warp, physics-warp | - | LIVE-PROVEN 2026-10-03 |

### QL (23)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| QL-1-ksc-actions-then-earlier-quicksave | nightly | career-earned-ksc | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-16 |
| QL-10-quickload-restores-names-and-groups | nightly | interbody-route-recorded | seam | Kerbin | reload, save, scene-change | - | LIVE-PROVEN 2026-10-20 |
| QL-11-quickload-after-scene-change-resolves-version | nightly | gloops-airshow | seam | Kerbin | reload, save, warp | - | LIVE-PROVEN 2026-10-20 |
| QL-12-quickload-brings-back-sealed-rewind-point | nightly | gloops-airshow +inj rewind-b9 | seam | - | reload, save, time-jump | - | LIVE-PROVEN 2026-10-20 |
| QL-13-resumed-flight-survives-f5-f9 | nightly | lab-lko-science | seam | Kerbin | reload, save, warp | - | LIVE-PROVEN 2026-10-21 |
| QL-14-ksc-rows-crossed-in-flight | nightly | career-pad-craft | seam | Kerbin | rewind-to-launch, save, warp, scene-change | - | LIVE-PROVEN 2026-10-21 |
| QL-14T-ksc-rows-crossed-in-tracking-station | nightly | career-pad-craft | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-22 |
| QL-15-f5-in-refly-merge-then-f9 | nightly | refly-split-crewed-recorded | seam | Kerbin | re-fly, reload, save, warp | - | LIVE-PROVEN 2026-11-20 |
| QL-16-f5-in-refly-discard-then-f9 | nightly | gloops-airshow +inj rewind-b9 | seam | Kerbin | re-fly, revert, reload, save, time-jump | - | LIVE-PROVEN 2026-11-20 |
| QL-2-quickload-career-recovery-not-paid | nightly | career-pad-craft | b1_pad_hop_career | Kerbin | reload, save, scene-change | - | LIVE-PROVEN 2026-10-08 |
| QL-2b-quickload-booster-career-reward-not-paid | nightly | career-gs1-two-stage-pad | gs1_auto_chute_booster_ca... | Kerbin | reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-07 |
| QL-2c-quickload-strategy-share-not-kept | nightly | career-gs1-leadership-pad | gs1_auto_chute_booster_ca... | Kerbin | reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-16 |
| QL-3-quickload-before-refly-merge | nightly | career-pad-craft +inj rewind-cr... | cl3_refly_crew_tombstone | Kerbin | reload, save, time-jump, scene-change | - | LIVE-PROVEN 2026-10-07 |
| QL-4-quickload-booster-terminal | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-07 |
| QL-4b-quickload-from-space-center | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-07 |
| QL-4c-quickload-after-in-flight-save | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-07 |
| QL-5-discard-refly-keeps-unfinished-flight | nightly | gloops-airshow +inj rewind-b9 | seam | Kerbin | re-fly, revert, time-jump | - | LIVE-PROVEN 2026-10-07 |
| QL-5c-discard-refly-after-quickload | nightly | gloops-airshow +inj rewind-b9 | seam | Kerbin | re-fly, revert, reload, save, time-jump | - | LIVE-PROVEN 2026-10-15 |
| QL-5d-retry-refly-after-quickload | nightly | gloops-airshow +inj rewind-b9 | seam | Kerbin | re-fly, revert, reload, save, time-jump | - | LIVE-PROVEN 2026-10-15 |
| QL-6-discard-then-quickload-records-again | nightly | gloops-airshow | seam | Kerbin | reload, save, warp | - | LIVE-PROVEN 2026-10-16 |
| QL-7-quickload-prunes-post-save-refly-fork | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-10-15 |
| QL-8-quickload-drops-flight-committed-after-save | nightly | career-pad-craft | b1_pad_hop_career | Kerbin | reload, save, scene-change | - | LIVE-PROVEN 2026-10-20 |
| QL-9-load-older-save-keeps-newer-save-intact | nightly | career-pad-craft | b1_pad_hop_career | Kerbin | reload, save, scene-change | - | LIVE-PROVEN 2026-10-20 |

### QR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| QR-1-recording-quarantine-grace-and-expiry | nightly | recording-quarantine-ksc | seam | Kerbin | save | - | LIVE-PROVEN 2026-10-24 |

### QTM (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| QTM-1-quit-to-main-menu-commits-flight | nightly | eva2-lko-crewed | seam | Kerbin | reload, scene-change | - | LIVE-PROVEN 2026-11-23 |

### QWS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| QWS-1-quit-without-saving-drops-flight | nightly | eva2-lko-crewed | seam | Kerbin | scene-change | - | LIVE-PROVEN 2026-11-05 |

### R (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| R1-rewind-loop-flown | operator | b2-lko-craft +inj rewind-b9 | r1_rewind_loop | Kerbin | - | - | LIVE-PROVEN 2026-07-28 |
| R7a-rewind-session-absent | nightly | career-pad-craft | seam | - | - | Rewind | LIVE-PROVEN 2026-08-04 |
| R7c-rewind-spacecenter | nightly | fresh-career +inj rewind-b9 | seam | Kerbin | - | Rewind | LIVE-PROVEN 2026-08-20 |

### RB (5)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RB-1-rewind-readback-divergence | operator | career-science-pad +inj rewind-... | science_bench_recover | Kerbin | re-fly, reload, save | - | RESHAPED 2026-10-20 for design 2.12 Rule... |
| RB-2-rewind-readback-within-range | operator | career-science-pad +inj rewind-... | science_bench_recover | Kerbin | re-fly, reload, save | - | RESHAPED 2026-10-20 for design 2.12 Rule... |
| RB-3-refly-hold-redrawn-after-reload | operator | career-science-pad +inj rewind-... | science_bench_recover | Kerbin | re-fly, reload, save, time-jump | - | LIVE-PROVEN 2026-10-22 |
| RB-4-rewind-to-launch-keeps-open-refly | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | rewind-to-launch, re-fly, reload, save +1 | - | LIVE-PROVEN 2026-10-25 |
| RB-5-rewind-to-launch-keeps-sealed-retry | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | rewind-to-launch, re-fly, reload, save +1 | - | LIVE-PROVEN 2026-10-25 |

### RC (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RC-1-docked-transfer-rewind-tanks | operator | bdock-recorded | seam | Kerbin | re-fly, reload, time-jump | - | LIVE-PROVEN 2026-10-16 |
| RC-6-eva-cargo-from-foreign-container-rewind | nightly | eva-construct-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-16 |
| RC-7-restore-clone-discard-keeps-career-actions | nightly | lab-lko-seeded-transmit | seam | Kerbin | save, warp | - | LIVE-PROVEN 2026-10-24 |

### RCN (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RCN-1-route-conservation-across-loads | nightly | rover-relay-c-recorded | seam | - | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-07 |

### REV (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| REV-1-revert-to-vab-in-rewound-timeline | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, revert, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |

### RF (32)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RF-1-continuation-stays-open | operator | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-09-08 |
| RF-10-fixed-tip-loads-open | operator | refly-autopilot-recorded | seam | Kerbin | - | - | LIVE-PROVEN 2026-09-09 |
| RF-11-both-slots-in-sequence | operator | refly-autopilot-recorded | seam | Kerbin | re-fly, reload | - | LIVE-PROVEN 2026-09-09 |
| RF-12-concluded-refly-batch | operator | refly-autopilot-recorded | seam | Kerbin | re-fly, time-jump | Rewind | LIVE-PROVEN 2026-09-09 |
| RF-12L-rewind-batch-after-landing | operator | b2-lko-craft +inj rewind-b9 | r1_rewind_loop | Kerbin | - | Rewind | LIVE-PROVEN 2026-09-09 |
| RF-12S-refly-saves-pre-rewind-boarded-crew | operator | refly-autopilot-recorded | rf12s_refly_orbit_insert | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-09-22 |
| RF-12W-rewind-batch-after-warp-crash | operator | refly-autopilot-recorded | seam | Kerbin | re-fly, warp | Rewind | LIVE-PROVEN 2026-09-09 |
| RF-13-refly-split-crew-survives-reload | operator | refly-split-crewed-recorded | rf12s_refly_orbit_insert | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-09-23 |
| RF-13H-crewed-crash-refly-host | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | save, scene-change | - | FLOWN 2026-09-22, PASS attempt 1 (2026-09... |
| RF-13R-split-merge-cold-reload | operator | refly-split-crewed-merged | seam | Kerbin | - | - | LIVE-PROVEN 2026-09-23 |
| RF-14-rtl-refly-load-sweep | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | re-fly, reload, save | - | LIVE-PROVEN 2026-09-24 |
| RF-15-rtl-cancels-live-refly | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | rewind-to-launch, re-fly, reload, save | - | LIVE-PROVEN 2026-09-24 |
| RF-16-hard-preset-refly-exit-merge | nightly | gloops-airshow-hard +inj rewind-b9 | seam | - | re-fly, time-jump | - | LIVE-PROVEN 2026-09-27 |
| RF-17-hard-preset-refly-exit-discard | nightly | gloops-airshow-hard +inj rewind-b9 | seam | - | re-fly, time-jump | - | LIVE-PROVEN 2026-09-27 |
| RF-18-separation-slot-walks-own-eva | operator | gs2-orbital-stack | gs2_orbital_probe_deploy | Kerbin | scene-change | - | LIVE-PROVEN 2026-09-27 |
| RF-19-refly-session-eva-reboard-merges | operator | refly-split-crewed-recorded | rf12s_refly_orbit_insert | Kerbin | re-fly | - | LIVE-PROVEN 2026-09-27 |
| RF-2-two-reflies-in-sequence | operator | bdock-recorded | seam | Kerbin | re-fly, reload, time-jump | - | LIVE-PROVEN 2026-09-09 |
| RF-20-stashed-eva-slot-refly | operator | gs2-orbital-stack | gs2_orbital_probe_deploy | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-09-27 |
| RF-21-refly-sibling-crosses-mun-soi | operator | gs2-orbital-stack | b23_ike_orbit | Kerbin, Mun, Ike | re-fly, warp | - | LIVE-PROVEN 2026-11-14 |
| RF-3-refly-discard-then-commit | operator | bdock-recorded | seam | Kerbin | re-fly, reload, time-jump | - | LIVE-PROVEN 2026-09-09 |
| RF-4-rewind-to-launch-after-merge | operator | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | rewind-to-launch, re-fly, reload, save +1 | - | LIVE-PROVEN 2026-09-09 |
| RF-5-seal-closes-the-slot | operator | bdock-recorded | seam | Kerbin | reload | - | LIVE-PROVEN 2026-09-09 |
| RF-6-rewind-category-live-session | operator | bdock-recorded | seam | Kerbin | re-fly, time-jump | Rewind | RE-FLOWN 2026-10-16 WITH THE FLIGHT-READY... |
| RF-7M-predicted-tail-map-render | operator | refly-a-recorded | seam | Kerbin | time-jump | - | LIVE-PROVEN 2026-09-08 |
| RF-7T-predicted-tail-ts-render | operator | refly-a-recorded | seam | Kerbin | reload, save, warp, scene-change | - | LIVE-PROVEN 2026-09-08 |
| RF-8-ghost-during-refly | operator | bdock-recorded | seam | Kerbin | re-fly, time-jump | - | LIVE-PROVEN 2026-09-09 |
| RF-9-atmosphere-exit-split-stays-open | operator | gs1-two-stage-pad | kx_rewind_watch | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-09-09 |
| RF-C1-refly-hides-contract-accepted-later | operator | career-gs1-two-stage-pad | gs1_auto_chute_booster_ca... | Kerbin | re-fly, reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-25 |
| RF-L1-bank-then-retire-refused | operator | lab-lko-seeded-transmit | seam | Kerbin | re-fly, reload, save, warp, scene-change | - | LIVE-PROVEN 2026-10-20 |
| RF-L2-booster-refly-lands-recovery-added | operator | career-gs1-two-stage-pad | gs1_auto_chute_booster_ca... | Kerbin | re-fly, reload, save, warp, physics-warp +1 | - | LIVE-PROVEN 2026-10-20 |
| RF-L3-refly-switch-refused | operator | gloops-airshow +inj refly-world... | seam | - | re-fly, time-jump, vessel-switch | - | LIVE-PROVEN 2026-10-20 |
| RF-L4-refly-retry-after-earning-restores-ledger | operator | lab-lko-seeded-transmit | seam | Kerbin | re-fly, revert, reload, save, warp +1 | - | LIVE-PROVEN 2026-10-20 |

### RFK (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RFK-1-ksc-purchases-before-refly-merge | operator | cs-career-mun4 | seam | Kerbin, Mun | re-fly, reload, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-11-23 |
| RFK-2-ksc-purchases-before-refly-discard | operator | cs-career-mun4 | seam | Kerbin, Mun | re-fly, reload, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-11-23 |

### RFN (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RFN-1-refly-nested-separation-merge-f9 | operator | cs-career-mun4 | seam | Kerbin, Mun | re-fly, reload, save, warp, scene-change | - | LIVE-PROVEN 2026-11-23 |
| RFN-2-refly-nested-separation-discard | operator | cs-career-mun4 | seam | Kerbin, Mun | re-fly, warp, scene-change | - | LIVE-PROVEN 2026-11-23 |

### RFP (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RFP-1-refly-resumed-in-new-process | nightly | refly-midsession-saved | seam | Kerbin | - | - | LIVE-PROVEN 2026-11-23 |

### RFQ (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RFQ-1-refly-quit-without-saving-keeps-dialog | nightly | gloops-airshow +inj rewind-b9 | seam | Kerbin | re-fly, time-jump, scene-change | - | LIVE-PROVEN 2026-11-23 |
| RFQ-2-refly-quit-to-main-menu-merge | nightly | gloops-airshow +inj rewind-b9 | seam | - | re-fly, reload, time-jump, scene-change | - | LIVE-PROVEN 2026-11-23 |

### RH (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RH-1-live-rp-handle-rewind | operator | bdock-recorded | seam | Kerbin | re-fly | - | PROVEN 2026-09-08 |

### RL (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RL-1-relative-loop-live-anchor | nightly | pad-runway-pair +inj relative-loop | seam | - | warp | - | LIVE-PROVEN 2026-09-23 |

### RR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RR-1-relaunch-rewind-keeps-earlier-launch | nightly | kerbin-splashdown-recorded | seam | Kerbin | rewind-to-launch, reload, save, scene-change | - | LIVE-PROVEN 2026-10-06 |

### RS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RS-1-relay-route-endpoint-lost-rescan | nightly | rover-relay-c-recorded +inj res... | seam | Kerbin | save, warp, time-jump, scene-change | - | LIVE-PROVEN 2026-10-25 |

### RSC (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RSC-1-real-spawn-control-warp | nightly | gloops-airshow +inj spawn-contr... | seam | - | warp | - | LIVE-PROVEN 2026-09-25 |
| RSC-2-leaves-warp | nightly | eva2-lko-crewed +inj leaves-dep... | seam | Kerbin | warp | - | LIVE-PROVEN 2026-10-25 |
| RSC-3-ready-spawn-room | nightly | eva2-lko-crewed +inj spawn-room | seam | Kerbin | warp | - | LIVE-PROVEN 2026-10-25 |

### RSF (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RSF-1-route-source-fixed-across-transport-reflight | nightly | rover-relay-c-recorded | seam | - | reload, save, warp, time-jump | - | LIVE-PROVEN 2026-11-17 |

### RSQ (4)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RSQ-1-rescue-rebuilt-at-space-center | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-21 |
| RSQ-2-rescue-rebuilt-in-flight-deferred | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +2 | - | LIVE-PROVEN 2026-11-21 |
| RSQ-3-rescue-deferral-kept-across-rewind | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +2 | - | LIVE-PROVEN 2026-11-21 |
| RSQ-4-rescue-deferral-kept-across-discard-refly | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, re-fly, revert, reload +4 | - | LIVE-PROVEN 2026-11-21 |

### RVC (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RVC-1-two-rover-head-on-crash | operator | rover-crash-watched | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | - | LIVE-PROVEN 2026-11-17 |

### RVR (19)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RVR-1-rover-route-proof | nightly | rover-route-recorded | seam | - | - | Logistics | LIVE-PROVEN 2026-09-01 |
| RVR-10-rover-relay-c-origin-empty | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-11-rover-relay-c-origin-partial | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-12-rover-relay-c-origin-cargo-missing | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-13-rover-relay-c-destination-full | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-14-rover-relay-c-destination-partial | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-15-rover-relay-c-destination-empty | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-16-rover-route-destination-slots-full | nightly | rover-route-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-18-rover-route-endpoint-removed | nightly | rover-route-recorded | seam | - | save, time-jump | - | [expectations.routes] ARMED 2026-09-10 (g... |
| RVR-19-rover-route-endpoint-transport-only | nightly | rover-route-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-2-rover-route-create | nightly | rover-route-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-01 |
| RVR-20-rover-relay-c-destination-slots-full-tank-empty | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-06 |
| RVR-21-rover-drive-surface-hysteresis | nightly | rover-route-recorded | seam | - | - | - | LIVE-PROVEN 2026-10-15 |
| RVR-3-route-lifecycle | daily | rover-route-recorded | seam | - | - | RouteLifecycle | LIVE-PROVEN 2026-09-01 |
| RVR-5-rover-relay-eligibility | nightly | rover-relay-recorded | seam | - | save | - | LIVE-PROVEN 2026-09-02 |
| RVR-6-rover-relay-logistics-host | nightly | rover-relay-recorded | seam | - | - | Logistics | LIVE-PROVEN 2026-09-02 |
| RVR-7-rover-relay-c-dispatch | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-02 |
| RVR-8-rover-relay-c-second-cycle-hold | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |
| RVR-9-rover-relay-c-surface-cadence | nightly | rover-relay-c-recorded | seam | - | save, time-jump | - | LIVE-PROVEN 2026-09-03 |

### RW (25)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| RW-1-ksc-economy | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-10-lead-window-ksc-actions | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-11-refly-cheap-debris-recovery | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, re-fly, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-12-hire-across-rewind | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-13-tech-research-across-rewind | operator | cs-career-mun2 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-14-dismissal-across-rewind | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-15-earlier-milestone | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-16-transmit-then-recover-earlier | operator | cs-career-mun4 | science_bench_recover | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-17-contract-completed-earlier | operator | cs-career-mun1 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-18-ksc-warp-across-committed-future | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-19-tight-funds-launch | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-2-crewed-relaunch-revert | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, revert, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-20-strategy-and-deadline-after-rewind | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-21-earlier-science | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-22-patents-licensing-recovery | operator | lab-pad-career | seam | Kerbin | reload, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-23-strategy-part-purchase-rebuy | operator | career-pad-craft-purchase | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-24-strategy-slot-needed-after-rewind | operator | career-pad-craft-purchase | seam | Kerbin | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-25-refly-recover-pressed | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, re-fly, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |
| RW-3-save-reload-takeback | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-4-claimed-vessel-disposal | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-5-rewind-refly-discard | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, re-fly, revert, save +2 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-6-refly-science-discard-merge | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, re-fly, revert, reload +3 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-7-refly-kerbal-death | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, re-fly, revert, reload +3 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-8-free-tech-gift-rewind | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |
| RW-9-strategy-repair-rebuy | operator | cs-career-mun4 | seam | Kerbin, Mun | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-28 |

### S (20)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| S0.10-automerge-limbo-warm-exit | operator | eva2-lko-crewed +inj pending-li... | seam | Kerbin | scene-change | - | LIVE-PROVEN 2026-08-29 |
| S0.12-switch-noop-discard | daily | eva2-lko-crewed | seam | Kerbin | warp, scene-change, vessel-switch | - | LIVE-PROVEN 2026-09-11 |
| S0.13-switch-auto-resolve | daily | eva2-lko-crewed | seam | Kerbin | warp, scene-change, vessel-switch | - | LIVE-PROVEN 2026-10-24 |
| S0.14-far-switch-merges-before-reload | daily | jool-park-nerv | seam | Jool | warp, vessel-switch | - | LIVE-PROVEN 2026-11-15 |
| S0.16-switch-segment-across-f5-f9-scoped-discard | daily | eva2-lko-crewed | seam | Kerbin | reload, save, warp, scene-change +1 | - | LIVE-PROVEN 2026-11-20 |
| S0.5-live-record-discard | daily | gloops-airshow | seam | Kerbin | - | - | Live record start/stop marker pairing + ... |
| S0.6-live-record-commit | daily | gloops-airshow +inj all-synthetic | seam | Kerbin | - | - | Commit on top of the injected ... |
| S0.7-exit-auto-commit | daily | eva2-lko-crewed | seam | Kerbin | scene-change | - | LIVE-PROVEN 2026-07-30 |
| S0.8-switch-click-segment | daily | eva2-lko-crewed | seam | Kerbin | warp, vessel-switch | - | LIVE-PROVEN 2026-07-30 |
| S0.9-automerge-pending-limbo-cold-load | operator | fresh-sandbox +inj pending-limb... | seam | Kerbin | - | - | LIVE-PROVEN 2026-08-29 |
| S1.4-injected-playback | daily | gloops-airshow +inj all-synthetic | seam | - | - | GhostPlayback | 272-tree corpus injection, load, ghost ma... |
| S1.5-rewind-loop | nightly | gloops-airshow +inj rewind-b9 | seam | - | re-fly, time-jump | - | LIVE-PROVEN 2026-07-29 |
| S1.6-render-parity | daily | gloops-airshow +inj all-synthetic | seam | - | - | GhostMap | LIVE-PROVEN 2026-07-26 |
| S1.7-maprender-parity | daily | gloops-airshow +inj all-synthetic | seam | - | - | MapRender | LIVE-PROVEN 2026-07-26 |
| S1.8-soi-crossing-playback | daily | gloops-airshow +inj looped-inte... | seam | - | - | SoiCrossingPlayback | LIVE-PROVEN 2026-08-06 |
| S1.9-part-showcase-render | operator | gloops-airshow +inj part-showcase | seam | Kerbin | reload, save, warp, time-jump | - | RE-PINNED 2026-11-24, RE-FLOWN GREEN 2026... |
| S4.1-rewind-merge | nightly | gloops-airshow +inj rewind-b9 | seam | - | re-fly, time-jump | - | LIVE-PROVEN 2026-07-31 |
| S4.2-refly-world-preservation | nightly | gloops-airshow +inj refly-world... | seam | - | re-fly, time-jump | ReFlyWorldPreservation | SPEC CHANGED 2026-10-25 (branch claude/pr... |
| S4.3-refly-discard-with-ghosts | operator | gloops-airshow +inj refly-world... | seam | - | re-fly, time-jump | - | 2026-10-07: _2241 PARSEK-FAIL on a stale... |
| S4.4-refly-quicksave-mid-session | operator | gloops-airshow +inj refly-world... | seam | - | re-fly, reload, save, time-jump | - | LIVE-PROVEN 2026-09-09 |

### SB (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SB-1-spawn-active-vessel-blocker | nightly | miner-rover-landed | seam | Kerbin | - | SpawnActiveVesselBlocker | LIVE-PROVEN 2026-10-24 |

### SCI (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SCI-1-science-reearned-after-rewind | operator | cs-science-new | science_bench_recover | Kerbin | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-27 |

### SD (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SD-1-same-tree-redock | operator | bdock-second-dock-recorded | d5_redock | Kerbin | - | - | LIVE-PROVEN 2026-09-24 |

### SE (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SE-1-editor-round-trip | nightly | kerbin-splashdown-recorded | seam | Kerbin | reload, save, time-jump, scene-change | - | LIVE-PROVEN 2026-09-26 |

### SEAT (4)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SEAT-1-seat-dismount-eva | nightly | seat-pad | seam | Kerbin | save, warp, scene-change | - | LIVE-PROVEN 2026-10-24 |
| SEAT-2-seat-occupant-survives-spawn | nightly | seat-rover-pad | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-10-25 |
| SEAT-3-foreign-seat-claim-rewind | nightly | seat-rover-pad | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-10-25 |
| SEAT-4-seat-replay-kerbal-visibility | operator | seat-pad | seam | Kerbin | rewind-to-launch, warp, scene-change | SeatGhostKerbal | LIVE-PROVEN 2026-11-23 |

### SH (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SH-1-science-held-for-committed-tech | operator | fresh-science | seam | Kerbin | rewind-to-launch, save, warp, scene-change | - | LIVE-PROVEN 2026-10-26 |

### SLM (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SLM-1-standalone-loop-map-position | operator | pad-runway-pair +inj standalone... | seam | Kerbin | warp | StandaloneLoopMap | LIVE-PROVEN 2026-11-01 |
| SLM-1T-standalone-loop-ts-position | operator | pad-runway-pair +inj standalone... | seam | Kerbin | reload, save, warp, scene-change | StandaloneLoopMap | LIVE-PROVEN 2026-11-01 |
| SLM-2-standalone-loop-map-pause | operator | pad-runway-pair +inj standalone... | seam | Kerbin | warp | StandaloneLoopMap | LIVE-PROVEN 2026-11-01 |

### SN (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SN-1-startup-notices-clean-install | nightly | fresh-career | seam | Kerbin | - | StartupNotices | LIVE-PROVEN 2026-10-04 |

### SOI (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SOI-1-mun-exit-physics-burn | operator | mun-park-kerbalx | soi1_mun_exit_burn | Kerbin, Mun | - | - | LIVE-PROVEN 2026-11-04 |
| SOI-2-mun-exit-formation-wingman | operator | mun-park-kerbalx | soi1_mun_exit_burn | Kerbin, Mun | - | - | LIVE-PROVEN 2026-11-17 |
| SOI-3-bg-probe-mun-exit-on-rails | operator | mun-escape-probe-stack | seam | Kerbin, Mun | save, warp | BgSoiCrossingPlayback | LIVE-PROVEN 2026-11-21 |

### SPW (11)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SPW-1-laythe-orbit-end-spawn-flight | operator | laythe-park-nerv | seam | Jool, Laythe | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| SPW-10-mun-orbit-end-spawn-placement | operator | mun-park-kerbalx | seam | Mun | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| SPW-11-rsc-warp-orbital-held-target | operator | eva2-lko-crewed +inj single-poi... | seam | Kerbin | warp | - | LIVE-PROVEN 2026-11-20 |
| SPW-2-duna-orbit-end-spawn-runway | operator | duna-park-probe | seam | Duna | rewind-to-launch, save, warp, scene-change | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| SPW-3-tylo-orbit-end-spawn-flight | operator | tylo-park-nerv | seam | Jool, Tylo | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| SPW-4-max-rails-warp-two-ends-flight | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-20 |
| SPW-5-physics-warp-end-spawn-near-active | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| SPW-6-map-view-end-spawn-max-warp | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-20 |
| SPW-7-end-spawn-while-recording | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |
| SPW-8-quickload-while-spawn-held | operator | eva2-lko-crewed +inj spawn-safety | seam | Kerbin | reload, save, warp | - | LIVE-PROVEN 2026-11-17 |
| SPW-9-cold-load-past-unspawned-end | operator | eva2-lko-crewed | seam | Kerbin | rewind-to-launch, reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-17 |

### SRV (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SRV-1-hinge-followers-ghost | operator | srv-hinge-rover | seam | Kerbin | rewind-to-launch, reload, save, warp | ServoFollowers | LIVE-PROVEN 2026-11-21 |
| SRV-2-rotor-spin-ghost | operator | srv-rotor-rover | seam | Kerbin | rewind-to-launch, reload, save, warp | - | LIVE-PROVEN 2026-11-21 |
| SRV-3-mirrored-hinge-pair-ghost | operator | srv-mirror-hinge-rover | seam | Kerbin | rewind-to-launch, reload, save, warp | ServoMirroredPose | LIVE-PROVEN 2026-11-21 |

### SS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SS-1-spawn-safety-corrections | nightly | eva2-lko-crewed +inj spawn-safety | seam | Kerbin | save, warp | LeafVesselPlacement | SPEC CHANGED 2026-11-17 (todo END-SPAWN-S... |

### SSC (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SSC-1-sealed-split-chain-route-gate | operator | rover-route-recorded | seam | - | save, warp, scene-change | - | LIVE-PROVEN 2026-11-23 |

### ST (4)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| ST-1-storage-timeline-ingame | nightly | career-earned-pad | seam | - | - | Timeline, Storage | LIVE-PROVEN 2026-09-25 |
| ST-2-rewind-point-quicksave | nightly | gs2-orbital-stack | gs2_orbital_probe_deploy | Kerbin | - | Storage | LIVE-PROVEN 2026-09-25 |
| ST-3-snapshot-sidecars-bdock | nightly | bdock-recorded | seam | - | - | SnapshotSidecars | LIVE-PROVEN 2026-09-26 |
| ST-4-safe-write-crash-after-temp | nightly | career-earned-ksc | seam | Kerbin | reload, save, scene-change | - | LIVE-PROVEN 2026-09-26 |

### STR (4)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| STR-1-strategies-across-rewind-and-f9 | operator | career-pad-craft | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-25 |
| STR-2-strategy-crossed-in-refly | operator | career-gs1-two-stage-pad | gs1_auto_chute_booster_ca... | Kerbin | re-fly, reload, save, warp, physics-warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-10-25 |
| STR-3-revert-takes-back-strategy-share | operator | career-gs1-leadership-pad | gs1_auto_chute_booster_ca... | Kerbin | revert, save | LedgerGroundTruth | LIVE-PROVEN 2026-10-25 |
| STR-4-transponders-and-negotiations-across-rewind | operator | career-pad-craft-purchase | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LedgerGroundTruth | LIVE-PROVEN 2026-11-07 |

### SUB (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SUB-1-submarine-dive-ksc-end-spawn | operator | psub-splashed | psub_dive | Kerbin | rewind-to-launch, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-18 |
| SUB-2-submarine-dive-flight-ghost-spawn | operator | psub-splashed | psub_dive | Kerbin | rewind-to-launch, save, warp, scene-change | LeafVesselPlacement | LIVE-PROVEN 2026-11-18 |
| SUB-3-submarine-dive-and-rise-separations | operator | psub-splashed | psub_dive | Kerbin | save | - | LIVE-PROVEN 2026-11-20 |

### SURV (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SURV-1-explode-between-cores-both-live | nightly | gs2-orbital-stack | seam | Kerbin | warp, scene-change | - | LIVE-PROVEN 2026-11-20 |
| SURV-2-recorded-half-destroyed-survivor-recorded | nightly | gs2-orbital-stack | seam | Kerbin | warp, scene-change | - | LIVE-PROVEN 2026-11-20 |

### SWC (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SWC-1-map-switch-own-committed-case-c | operator | eva2-lko-crewed | seam | Kerbin | reload, save, warp, scene-change +1 | - | LIVE-PROVEN 2026-11-23 |
| SWC-2-cycle-own-committed-case-c | operator | eva2-lko-crewed | seam | Kerbin | reload, save, warp, scene-change +1 | - | LIVE-PROVEN 2026-11-23 |

### SWS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| SWS-1-save-switch-through-main-menu | operator | cs-career-mun4 | seam | Mun | reload, save, scene-change, new-game | LedgerGroundTruth | LIVE-PROVEN 2026-10-31 |

### TL (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| TL-1-timeline-retired-milestone-copy | nightly | career-earned-ksc-retired-miles... | seam | Kerbin | save | - | LIVE-PROVEN 2026-10-25 |

### TOUR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| TOUR-1-tourist-spawn-stays-tourist | nightly | tourist-splashdown-career | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-10-25 |

### TP (9)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| TP-1-mun-loop-map-pictures | operator | mun-orbit-recorded | seam | Kerbin, Mun, Sun | warp, time-jump | - | LIVE-PROVEN 2026-11-11 |
| TP-2-mun-loop-ts-pictures | operator | mun-orbit-recorded | seam | Kerbin, Mun, Sun | reload, save, time-jump, scene-change | - | LIVE-PROVEN 2026-11-11 |
| TP-3-minmus-loop-map-pictures | operator | minmus-orbit-recorded | seam | Kerbin, Minmus, Sun | warp, time-jump | - | LIVE-PROVEN 2026-11-11 |
| TP-4-duna-reaim-landing-map-pictures | operator | duna-park-recorded | seam | Kerbin, Duna, Sun | warp, time-jump | - | LIVE-PROVEN 2026-11-11 |
| TP-5-laythe-vall-map-pictures | operator | vall-transfer-recorded | seam | Jool, Laythe, Vall, Sun | warp, time-jump | - | LIVE-PROVEN 2026-11-11 |
| TP-6-mun-landing-map-pictures | operator | mun-landing-recorded | seam | Kerbin, Mun | warp, time-jump | - | LIVE-PROVEN 2026-11-11 |
| TP-7-depot-route-ts-pictures | operator | depot-route-recorded | seam | Kerbin | reload, save, time-jump, scene-change | - | LIVE-PROVEN 2026-11-11 |
| TP-8-interbody-route-map-pictures | operator | interbody-route-recorded | seam | Kerbin, Mun, Duna, Sun | warp | - | LIVE-PROVEN 2026-11-11 |
| TP-9-duna-reaim-skipped-landing-map-pictures | operator | duna-park-recorded | seam | Duna | warp, time-jump | - | LIVE-PROVEN 2026-11-12 |

### TSS (3)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| TSS-1-rover-delivery-ts-max-warp | operator | miner-rover-landed | seam | Kerbin | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-11-20 |
| TSS-2-mun-orbit-end-ts | operator | mun-park-kerbalx | seam | Kerbin, Mun | rewind-to-launch, reload, save, warp +1 | LeafVesselPlacement | LIVE-PROVEN 2026-11-20 |
| TSS-3-two-deliveries-same-spot-ts | operator | gloops-airshow +inj spawn-stack | seam | Kerbin | reload, save, warp | LeafVesselPlacement | LIVE-PROVEN 2026-11-20 |

### UIS (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| UIS-1-ui-scale | nightly | gloops-airshow | seam | - | - | UiScale | LIVE-PROVEN 2026-11-04 |

### V (67)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| V1-map-dwell-mun-orbit | nightly | b2-lko-craft +inj rewind-b9 | v1_map_dwell | Kerbin, Mun | - | - | 2026-10-07 2026-10-07_2302 PASS attempt 1... |
| V10-dres-loop-arrival | operator | dres-orbit-recorded | seam | Dres | warp, time-jump | - | ITERATION 5 (2026-08-14, branch line-blin... |
| V11-moho-player-loop | operator | moho-orbit-recorded | seam | Moho | - | - | LIVE-PROVEN 2026-08-13 |
| V11A-moho-loop-arrival | operator | moho-orbit-recorded | seam | Moho | time-jump | - | CENSUS PAIR ARMED 2026-08-14 (branch line... |
| V12-eeloo-player-loop | operator | eeloo-orbit-recorded | seam | Eeloo | - | - | FLOWN TWICE GREEN 2026-08-13 AND ARMED TH... |
| V12A-eeloo-loop-arrival | operator | eeloo-orbit-recorded | seam | Eeloo | time-jump | - | BOTH PASSES FLOWN, ARMED AND NEGATIVE-CON... |
| V13-jool-player-loop | operator | jool-orbit-recorded | seam | Jool | - | - | THE JOOL PROGRAM'S LOOP-UNIT READING, ARM... |
| V13A-jool-loop-arrival | operator | jool-orbit-recorded | seam | Jool | time-jump | - | CRITERION (b) RENDER-TOKEN CONTROL FLOWN... |
| V14M-ike-player-loop | operator | ike-orbit-recorded | seam | Duna, Ike | time-jump | - | LIVE-PROVEN 2026-08-18 |
| V14T-ike-ts-arrival | operator | ike-orbit-recorded | seam | Duna, Ike | reload, save, time-jump, scene-change | - | LIVE-PROVEN 2026-08-18 |
| V15M-gilly-player-loop | operator | gilly-orbit-recorded | seam | Eve, Gilly | time-jump | - | CRITERION (b) RENDER-TOKEN CONTROL FLOWN... |
| V15T-gilly-ts-arrival | operator | gilly-orbit-recorded | seam | Eve, Gilly | reload, save, time-jump, scene-change | - | 2026-09-25 (coverage wave 1b): D11 defaul... |
| V16M-laythe-player-loop | operator | laythe-orbit-recorded | seam | Jool, Laythe | warp, time-jump | - | LIVE-PROVEN 2026-08-19 |
| V16T-laythe-ts-arrival | operator | laythe-orbit-recorded | seam | Jool, Laythe | reload, save, time-jump, scene-change | - | LIVE-PROVEN 2026-08-19 |
| V17M-laythe-vall-player-loop | operator | vall-transfer-recorded | seam | Jool, Laythe, Vall | warp, time-jump | - | LIVE-PROVEN 2026-08-20 |
| V17T-laythe-vall-ts-arrival | operator | vall-transfer-recorded | seam | Jool, Laythe, Vall | reload, save, warp, time-jump, scene-change | - | LIVE-PROVEN 2026-08-20 |
| V18T-depot-route-ts-arrival | nightly | depot-route-recorded | seam | Kerbin | reload, save, time-jump, scene-change | - | SPEC CHANGED 2026-11-12, RE-FLOWN GREEN 2... |
| V19M-laythe-jool-player-loop | operator | jool-return-recorded | seam | Jool, Laythe | warp, time-jump | - | LIVE-PROVEN 2026-08-21 |
| V19T-laythe-jool-ts-arrival | operator | jool-return-recorded | seam | Jool, Laythe | reload, save, warp, time-jump, scene-change | - | LIVE-PROVEN 2026-08-21 |
| V2-loop-arrival-dwell | operator | duna-direct-recorded | m3_loop_arrival_dwell | Duna | - | - | LIVE-PROVEN 2026-08-06 |
| V20K-jool-kerbin-ksc-arrival | operator | kerbin-return-recorded | seam | Kerbin, Jool, Sun | reload, save, warp, time-jump, scene-change | - | SPEC CHANGED 2026-10-07, RE-FLOWN PASS 20... |
| V20M-jool-kerbin-player-loop | nightly | kerbin-return-recorded | seam | Kerbin, Jool, Sun | warp, time-jump | - | SPEC CHANGED 2026-10-07, RE-FLOWN PASS 20... |
| V20T-jool-kerbin-ts-arrival | nightly | kerbin-return-recorded | seam | Kerbin, Jool, Sun | reload, save, warp, time-jump, scene-change | - | SPEC CHANGED 2026-10-07, RE-FLOWN PASS 20... |
| V21M-mun-minmus-player-loop | operator | mun-minmus-recorded | seam | Kerbin, Mun, Minmus | warp, time-jump | - | RE-FLOWN GREEN 2026-11-22 ON THE FAITHFUL... |
| V21T-mun-minmus-ts-arrival | operator | mun-minmus-recorded | seam | Kerbin, Mun, Minmus | reload, save, warp, time-jump, scene-change | - | RE-FLOWN GREEN 2026-11-11 ON THE 1.0.2 MO... |
| V22K-kerbin-splashdown-ksc-arrival | operator | kerbin-splashdown-recorded | seam | Kerbin | reload, save, warp, time-jump, scene-change | - | SPEC CHANGED 2026-10-07, RE-FLOWN PASS 20... |
| V22M-kerbin-splashdown-player-loop | operator | kerbin-splashdown-recorded | seam | Kerbin | warp, time-jump | - | LIVE-PROVEN 2026-07-20 |
| V22T-kerbin-splashdown-ts-arrival | operator | kerbin-splashdown-recorded | seam | Kerbin | reload, save, warp, time-jump, scene-change | - | SPEC CHANGED 2026-11-16, RE-FLOWN PASS 20... |
| V23M-mun-landing-player-loop | operator | mun-landing-recorded | seam | Mun | warp, time-jump | - | FLOWN WITH THE STEP 2026-11-20: 2026-11-2... |
| V23T-mun-landing-ts-arrival | operator | mun-landing-recorded | seam | Mun | reload, save, warp, time-jump, scene-change | - | SPEC CHANGED 2026-10-07, RE-FLOWN PASS 20... |
| V24W-duna-one-warp-stair | operator | duna-one-recorded | m3_loop_arrival_dwell | Kerbin, Duna | - | - | DISCIPLINE-COMPLETE 2026-08-25 ACROSS SIX... |
| V25M-duna-park-player-loop | nightly | duna-park-recorded | seam | Kerbin, Duna, Sun | time-jump | - | RE-FLOWN 2026-11-12 on the REAIM-PARKING-... |
| V26T-interbody-route-ts-arrival | operator | interbody-route-recorded | seam | Kerbin, Duna | reload, save, scene-change | - | SPEC CHANGED 2026-11-12, RE-FLOWN GREEN 2... |
| V27M-rover-route-endpoint-substituted-map-lines | operator | rover-route-recorded | seam | - | reload, save, warp, time-jump, scene-change | RouteGhostPlacement | RE-FLOWN GREEN 2026-11-17 WITH THE ROUTE... |
| V28M-tylo-player-loop | operator | tylo-orbit-recorded | seam | Jool, Tylo | warp, time-jump | - | LIVE-PROVEN 2026-09-26 |
| V29M-bop-player-loop | operator | bop-orbit-recorded | seam | Jool, Bop | warp, time-jump | - | LIVE-PROVEN 2026-09-26 |
| V30M-pol-player-loop | operator | pol-orbit-recorded | seam | Jool, Pol | warp, time-jump | - | LIVE-PROVEN 2026-09-26 |
| V31M-duna-ike-continuation-player-loop | operator | duna-ike-recorded | seam | Kerbin, Duna, Ike | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-07 |
| V31T-duna-ike-continuation-ts-arrival | operator | duna-ike-recorded | seam | Kerbin, Duna, Ike | reload, save, time-jump, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-07 |
| V32M-eve-gilly-continuation-player-loop | operator | eve-gilly-recorded | seam | Kerbin, Eve, Gilly | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-07 |
| V32T-eve-gilly-continuation-ts-arrival | operator | eve-gilly-recorded | seam | Kerbin, Eve, Gilly | reload, save, time-jump, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-07 |
| V33M-jool-laythe-continuation-player-loop | operator | jool-laythe-recorded | seam | Kerbin, Jool, Laythe | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-07 |
| V33T-jool-laythe-continuation-ts-arrival | operator | jool-laythe-recorded | seam | Kerbin, Jool, Laythe | reload, save, time-jump, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-14 |
| V34M-jool-vall-continuation-player-loop | operator | jool-vall-recorded | seam | Kerbin, Jool, Vall | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-14 |
| V35M-jool-tylo-continuation-player-loop | operator | jool-tylo-recorded | seam | Kerbin, Jool, Tylo | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-14 |
| V36M-jool-bop-continuation-player-loop | operator | jool-bop-recorded | seam | Kerbin, Jool, Bop | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-14 |
| V37M-jool-pol-continuation-player-loop | operator | jool-pol-recorded | seam | Kerbin, Jool, Pol | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-14 |
| V38M-jool-moon-tour-player-loop | operator | jool-tour-recorded | seam | Kerbin, Jool, Laythe, Vall +1 | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-16 |
| V39M-jool-laythe-bop-player-loop | operator | jool-laythe-bop-recorded | seam | Kerbin, Jool, Laythe, Bop | reload, scene-change | MoonArrivalHold | LIVE-PROVEN 2026-11-16 |
| V3C-flight-arrival-companion | nightly | b17-duna-pad +inj looped-interp... | b17_duna_direct | Duna | time-jump | - | LIVE-PROVEN 2026-09-07 |
| V3F-flight-arrival-faithful | operator | duna-direct-recorded | m3_loop_arrival_dwell | Duna | - | - | LIVE-PROVEN 2026-08-07 |
| V3R-flight-arrival-reaim | operator | duna-direct-recorded | m3_loop_arrival_dwell | Duna | - | - | LIVE-PROVEN 2026-08-07 |
| V4-player-loop-workflow | operator | duna-direct-recorded | seam | Duna | time-jump | - | RE-FLOWN GREEN 2026-11-22 ON THE FAITHFUL... |
| V43M-vall-tylo-player-loop | operator | vall-tylo-recorded | seam | Jool, Vall, Tylo | - | - | LIVE-PROVEN 2026-11-16 |
| V44M-laythe-tylo-player-loop | operator | laythe-tylo-recorded | seam | Jool, Laythe, Tylo | - | - | LIVE-PROVEN 2026-11-16 |
| V45M-tylo-bop-player-loop | operator | tylo-bop-recorded | seam | Jool, Tylo, Bop | - | - | LIVE-PROVEN 2026-11-16 |
| V46M-minmus-mun-player-loop | operator | minmus-mun-recorded | seam | Kerbin, Mun, Minmus | - | - | LIVE-PROVEN 2026-11-16 |
| V5-ts-loop-arrival | operator | duna-direct-recorded | seam | Duna | reload, save, time-jump, scene-change | - | CRITERION (b) RENDER-TOKEN CONTROL FLOWN... |
| V6M-mun-player-loop | operator | mun-orbit-recorded | seam | Mun | time-jump | - | RE-FLOWN GREEN 2026-11-22 ON THE FAITHFUL... |
| V6T-mun-ts-arrival | operator | mun-orbit-recorded | seam | Mun | reload, save, time-jump, scene-change | - | CRITERION (b) RENDER-TOKEN CONTROL FLOWN... |
| V7M-minmus-player-loop | operator | minmus-orbit-recorded | seam | Minmus | time-jump | - | RE-FLOWN GREEN 2026-11-22 ON THE FAITHFUL... |
| V7T-minmus-ts-arrival | operator | minmus-orbit-recorded | seam | Minmus | reload, save, time-jump, scene-change | - | 2026-09-27: FLOWN GREEN (2026-09-27_1230,... |
| V7W-minmus-physics-warp | nightly | minmus-orbit-recorded | seam | Minmus | warp, physics-warp, time-jump | - | LIVE-PROVEN 2026-09-26 |
| V8-eve-player-loop | operator | eve-orbit-recorded | seam | Eve | time-jump | - | CRITERION (b) RENDER-TOKEN CONTROL FLOWN... |
| V8F-eve-loop-faithful | operator | eve-orbit-recorded | seam | Eve | time-jump | - | RE-FLOWN GREEN 2026-11-22 ON THE FAITHFUL... |
| V8T-eve-ts-arrival | operator | eve-orbit-recorded | seam | Eve | reload, save, time-jump, scene-change | - | CRITERION (b) RENDER-TOKEN CONTROL FLOWN... |
| V9-dres-player-loop | operator | dres-orbit-recorded | seam | Dres | - | - | calibration discipline |

### VB (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| VB-1-ghost-vessel-budget | nightly | mun-park-kerbalx +inj vessel-bu... | seam | Mun | save, warp | VesselBudget | LIVE-PROVEN 2026-09-30 |

### VN (14)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| VN-1-stock-rename-staging-f9 | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | reload, save, warp | VesselNames | LIVE-PROVEN 2026-10-12 |
| VN-10-dock-undock-dominant-rename | nightly | eva2-lko-crewed +inj chain-tip-... | ci9_tip_dock | Kerbin | warp | - | LIVE-PROVEN 2026-10-13 |
| VN-11-part-naming-accept | nightly | gs1-two-stage-pad | seam | - | warp | - | LIVE-PROVEN 2026-10-13 |
| VN-12-spawn-resume-rename | nightly | gloops-airshow +inj spawn-contr... | seam | - | reload, save, warp | - | LIVE-PROVEN 2026-10-13 |
| VN-13-ghost-map-commnet-rename | nightly | gloops-airshow +inj ghost-commn... | seam | - | warp | - | LIVE-PROVEN 2026-10-13 |
| VN-14-refly-rename-merge | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | re-fly, reload, save, scene-change | - | LIVE-PROVEN 2026-11-20 |
| VN-2-refly-rename-discard | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | re-fly, revert, reload, save, scene-change | - | LIVE-PROVEN 2026-10-11 |
| VN-3-spawn-rename-rewind | nightly | gloops-airshow +inj spawn-contr... | seam | Kerbin | rewind-to-launch, save, warp | - | LIVE-PROVEN 2026-10-11 |
| VN-4-dock-undock-rename | nightly | eva2-lko-crewed +inj chain-tip-... | ci9_tip_dock | Kerbin | warp | - | LIVE-PROVEN 2026-10-12 |
| VN-5-stock-revert-cancels-rename | nightly | gs1-two-stage-pad | seam | - | revert | - | LIVE-PROVEN 2026-10-12 |
| VN-6-ts-rename-then-decouple | nightly | depot-route-recorded | seam | - | reload, save | - | LIVE-PROVEN 2026-10-12 |
| VN-7-preupgrade-spawn-backfill-rewind | nightly | refly-a-recorded | seam | Kerbin | rewind-to-launch, save | - | LIVE-PROVEN 2026-10-12 |
| VN-8-control-part-leaves-staging | nightly | gs1-two-stage-pad | seam | - | warp | - | LIVE-PROVEN 2026-10-12 |
| VN-9-refly-rename-retry | nightly | gs1-two-stage-pad | gs1_auto_chute_booster | Kerbin | re-fly, revert, reload, save, scene-change | - | LIVE-PROVEN 2026-10-13 |

### W (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| W1-watch-distance-cutoff | operator | kerbin-splashdown-recorded | seam | Kerbin | time-jump | - | GREEN AGAIN 2026-10-15: 2026-10-15_2148 a... |

### WDK (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| WDK-1-watch-across-bdock-dock-undock | operator | bdock-recorded | seam | Kerbin | warp, time-jump | - | RED ON A LANE SHAPE 2026-11-23: 2026-11-2... |

### WF (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| WF-1-world-first-reearned-after-rewind | operator | gloops-airshow-career | seam | Kerbin | rewind-to-launch, save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-26 |

### WR (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| WR-1-world-record-reearned-after-rewind | operator | cs-career-new | record_climb_rewind | Kerbin | save, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-27 |

### WTT (2)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| WTT-1-timeline-warp-across-committed-rows | operator | cs-career-mun4 | seam | Kerbin, Mun | save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-11-20 |
| WTT-2-timeline-warp-to-career-start | operator | cs-career-new | seam | Kerbin | save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-11-20 |

### ZF (1)

| lane | tier | host save | driver | bodies | timeline ops | in-game categories | status |
|---|---|---|---|---|---|---|---|
| ZF-1-zero-funds-career | nightly | career-pad-craft-zero-funds | seam | - | save, warp, scene-change | LedgerGroundTruth | LIVE-PROVEN 2026-10-03 |
