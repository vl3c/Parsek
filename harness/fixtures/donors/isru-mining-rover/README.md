# isru-mining-rover donor

Source material for `harness/fixtures/saves/miner-rover-landed`. Nothing here is a
fixture the harness stages; `harness/tools/build_miner_rover_landed.py` is the only
consumer, and it reads `mining-rover-rc.vessel` alone.

## Provenance

- V's dev KSP install (ReStock installed), sandbox save `isru`.
- Collected 2026-10-17 by `scripts/collect-logs.py` into
  `logs/2026-10-17_1419_mining-rover` (umbrella root, outside git).
- Original branch `claude/mining-rover-donor`, commit
  `ff09b4748706ff2ce6951c72c124fbf4c7a5868c`, holds the byte-exact CRLF originals:
  the collected `persistent.sfs`, the `parsek_rw_9dbfac.sfs` rewind-point quicksave and
  `Mining Rover RC.craft`.

## Files

- `mining-rover-rc.vessel` - the `Mining Rover RC` VESSEL node, extracted verbatim
  from the collected `persistent.sfs` (vessel index 4, the active vessel) and converted
  to LF. A depth-2 block (`\t\tVESSEL` ... `\t\t}`), 27 parts: probeStackSmall,
  advSasModule, MiniISRU, MiniDrill x4, RadialOreTank x2, fuelTankSmall,
  radialRCSTank x2, roverWheel1 x4, rtg x4, radPanelSm x4, ksp.r.largeBatteryPack x2,
  adapterSmallMiniShort. Crewless, LANDED at lat 0.02961638465397691,
  lon -74.724749708449622 (just west of the KSC), vessel persistentId 110406740, pid
  e585079827a441ee8026bf1afad27961. V flew it in that save: it mined ore and ran the
  ISRU, so its module state is what stock KSP wrote. It carries ten ReStock modules
  (`ModuleRestock*`) the builder strips, since the harness's stock-minimal instance
  has no ReStock.
- `Mining Rover RC.craft` - LF copy of V's SPH craft file. Reference only; the builder
  does not read it.

Re-run `python harness/tools/build_miner_rover_landed.py` after any change here;
`harness/lib/test_miner_rover_landed.py` reds on drift.
