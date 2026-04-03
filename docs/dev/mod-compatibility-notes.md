# Mod Compatibility Notes

## CustomBarnKit
CustomBarnKit modifies facility upgrade costs and tier counts. Parsek's FacilitiesModule stores facility levels as integers from KSP's normalized values. The dynamic slot limit mapping in LedgerOrchestrator assumes stock 3-tier progression (levels 0/1/2). CustomBarnKit compatibility requires verifying that the normalized-to-integer level conversion still produces correct values with non-standard tier counts.

**Status:** Untested. The conversion formula `(int)Math.Round(valueAfter * 2)` may produce incorrect levels with CustomBarnKit's modified tier structure.

## Strategia
Strategia completely replaces the stock strategy system. StrategiesModule tracks strategy activate/deactivate actions and applies contract reward transforms. If Strategia uses different strategy IDs or transform mechanics, the module may produce incorrect results.

**Status:** Untested. StrategiesModule uses stock strategy semantics (source/target resource, commitment percentage). Strategia compatibility requires investigation.

## Contract Configurator
Contract Configurator adds custom contract types with complex parameters. Parsek captures contract ConfigNode snapshots at accept time. CC contracts may not round-trip correctly if CC version changes between capture and restore.

**Status:** Untested. Contract state patching (Task 33) is scaffolded but not implemented, partly due to CC compatibility concerns.
