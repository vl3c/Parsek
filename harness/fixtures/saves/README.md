# Career ledger fixtures (M-B3)

File-constructed, synthetic, pure-stock save templates for the L1 ledger-accuracy
scripts and B10. Built headlessly (no KSP launch) by trimming the cleanest existing
CAREER dev save down to a deterministic clean-slate KSC. Source template: the dev
install `test career` save (KSP 1.12.5), reset per the M-B3 operator checklist in
`docs/dev/todo-and-known-bugs.md`.

Each fixture is `persistent.sfs` + `persistent.loadmeta` + `AddOns/DistantObject/Settings.cfg`.
No craft in flight, no active/offered contracts, no completed milestones, no unlocked
tech beyond the mode default `start` node, all facilities at level 0, no Parsek footprint
(no `Parsek/` dir, no `ParsekScenario` SCENARIO node, no `ParsekSettings` custom-param),
default career difficulty multipliers (all 1.0). Third-party mod SCENARIO nodes
(`Trajectories`, `Tracking_Persistence`) stripped; `start` tech node parts reduced to a
pure base-stock set (ReStock / Making-History parts dropped).

## fresh-career (GAME Mode = CAREER)

Shared by B10 + the four career L1 scripts (hire / dismiss / research / upgrade).

| Facet | Pinned value | Read by |
| --- | --- | --- |
| Funding funds | `500000` | `CareerSaveParser` seed (hasFunds) |
| ResearchAndDevelopment sci | `100` | seed (hasScience) |
| Reputation rep | `0` | seed (hasRep) |
| Facilities (all 10) | `lvl = 0` (level 0) | facility facet |
| Roster (hired crew) | EXACTLY Jebediah / Bill / Bob / Valentina Kerman (4 Crew, 0 assigned, 0 Parsek reservations/stand-ins) | hire cost curve input |
| Applicant (hire target) | `Verhat Kerman` (Engineer) | `L1-hire-kerbal-career` step arg |
| Dismissable kerbal | `Bill Kerman` | `L1-dismiss-kerbal-career` step arg |

Author constants declared in the specs (assert `expected == save` on the touched pool):

| Spec | Constant | Status |
| --- | --- | --- |
| `L1-research-node-career` | `basicRocketry` science = `-5.0` | **VERIFIED**: the source save's `basicRocketry` Tech node carried `cost = 5` (stock 1.12.5 tech data). |
| `L1-hire-kerbal-career` | hire funds = `-24000.0` at hired-count 4 | VERIFY-PENDING-OPERATOR (GameVariables recruit-cost curve; read `observedAfter=` on the first live run). |
| `L1-upgrade-facility-career` | Tracking Station level 0->1 funds = `-150000.0` | VERIFY-PENDING-OPERATOR (`SpaceCenterBuilding.GetUpgradeCost`; read `observedAfter=` on the first live run). |
| `L1-dismiss-kerbal-career` | all pools = `0.0` | pool-neutral (stock does not refund a hire). |

Budget check: `500000 - 150000 (upgrade) - 24000 (hire) = 326000 >= 0`.

## fresh-science (GAME Mode = SCIENCE_SANDBOX)

Science pool only: `ResearchAndDevelopment sci = 100`, no Funding / Reputation /
facilities / contracts. `CareerSaveParser` sets hasFunds / hasRep false (oracle
facet-skips them). Stock starting kerbals (Jeb/Bill/Bob/Val), no applicant. Used by
`L1-research-node-science` (research `basicRocketry`, science-only assertion). Depends on
the M-C1 `research-node` SCIENCE_SANDBOX readiness widen (RnDPresent) which has landed.

## fresh-sandbox (GAME Mode = SANDBOX)

No economy pool at all: every `hasX` false. Stock starting kerbals. Used by
`L1-passive-sandbox` (pure B10 passive variant, no KscAction driven).

**KNOWN BLOCKER (not a fixture defect):** a SANDBOX template has no pools by definition,
and `run.py::_capture_seed_baseline` terminal-INVALIDs any `[expectations.ledger]`
scenario whose template parses with all pools absent (`invalid-fixture`). So
`L1-passive-sandbox` stays terminal-INVALID at run time until either (a) the seed-baseline
gate is taught to accept a no-pools template when the manifest is empty (expected == seed
== all-absent, the facet-skip path the spec assumes), or (b) the `[expectations.ledger]`
block is removed from `L1-passive-sandbox` (it then runs as a pure recording-invariants
passivity proof). Left at `pending-fixture` until that is resolved.

## Re-tier

All seven specs stay `pending-fixture`. Re-tier to `daily` is the LAST step of the
first green live run (the named headless-boot follow-up), coupling re-tier with
confirm-green per the M-B3 checklist and avoiding the never-run-daily self-quarantine
(todo item 4). `L1-passive-sandbox` additionally needs the seed-baseline blocker above.
