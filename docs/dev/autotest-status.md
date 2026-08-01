# Automated Testing System - Status

Last updated: 2026-08-01 (THE `pending-operator` TAG IS NOW HONEST, and what
enforces it is a REVIEWED INVENTORY rather than a cleverer string match. Branch
`s41-pending-operator-tag`. An adversarial review panel SPLIT on whether
S4.1-rewind-merge's tag was stale - the tell that nothing was pinning the
meaning. NINE specs carried it; SIX are dropped, TWO were added, and FIVE carry it now
(R1-rewind-loop-flown and V1-map-dwell-mun-orbit are `tier = "operator"`;
S1.5-rewind-loop names asserts no unattended run can discharge; B16-eve-orbit is
operator-tier AND carries a documented outstanding human call - see round 4). The six:
S4.1 on its own written rule ("the pending-operator TAG stays until that first
green run"; that run was 2026-07-28), L1-hire-kerbal-career as pending-FIXTURE
residue, and the four L1 career specs, each of which named a
`VERIFY-PENDING-OPERATOR` fixture constant its own green run had already
discharged - the oracle hard-gates scalar pools, so a green run IS the
confirmation those markers asked an operator for (L1-dismiss is the one whose
all-zero manifest makes the logContract pair plus the README row the discharging
evidence instead).

FIVE REVIEW ROUNDS, and the mistakes are the useful part - each was the SAME
error in a different costume, and each is why the final design is what it is.
ROUND 1: the audit read the specs for a string and stopped, never opening
`harness/fixtures/saves/README.md`, the project's own per-constant ledger, which
already marked the research-node cost **VERIFIED**. It kept four specs it should
have dropped; L1-hire (dropped) and L1-upgrade-facility (kept) were in identical
states, separated only by which file the string sat in. ROUND 2: the check
counted DISCHARGED markers as live ones - the corpus records "the former
PENDING-OPERATOR is CLOSED" in prose - and the round-1 commit had written exactly
that prose into all six dropped specs, so re-adding a tag to three of them
passed. ROUND 3: the fix for that read marker prose at face value and TAGGED
`B15-eve-flyby` on a PRE-FLIGHT risk note ("no headless test can verify it")
whose question flight 7 ANSWERED on 2026-07-26 - as B15's own row in this file
says. That addition is REVERTED; B15 carries no tag, and its spec now records the
trap.

SO THE CELL STOPPED GUESSING. `PendingOperatorTagHonestyTests` pins two
hand-maintained inventories - `CARRIERS` (who owns the tag and why) and
`REVIEWED_UNTAGGED` (every untagged CANDIDATE - a spec that mentions the token or is
`tier = "operator"` - and why it does not carry it) - which are together total over the corpus: gaining the tag, losing
it, or newly mentioning it all red until a human records which it is. The one
half that IS machine-checkable, a carrier claiming `tier = "operator"`, is
checked. That is a weaker guarantee than "the tag is always truthful" and a much
more honest one: string matching over English cannot tell a live claim from a
dead one, a true claim from a false one, or a spec's own debt from one it
mentions on another's behalf (`S0.5` describes B1/B2's fixtures). The truth lives
in the STATUS ROW and the FIXTURE LEDGER, and the docstring says so.

ROUND 4 then found the audit had only ever looked at specs that MENTION the
string, so a spec could owe operator work invisibly: `B16-eve-orbit` is
`tier = "operator"` with a documented outstanding human call (its PROMOTE note;
this file already said "TIER NOT CHANGED ... left as an explicit human call", and
described R1's identical state as "the same standard applied to B16 in this same
pass" - and R1 is tagged) and never writes the token. B16 is tagged, the three
FORGE specs are classified as operator-tier BY MECHANISM rather than debt
(fixture-forge runs are manual by design, fixtures committed), and the
completeness check now covers BOTH populations it can DETECT - token-mentioning
specs AND operator-tier specs. (Round 5 then sharpened what that totality IS; see
below.)
Mutation-proved: re-adding the tag to a dropped spec, adding it to an unrelated
spec, removing it from a carrier, a new untagged spec mentioning the token, a
spec BECOMING operator-tier unclassified, and a carrier falsely citing
operator-tier all red.

ROUND 5 confirmed every tag decision, every classification and every number, and
corrected two OVERCLAIMS - the defect class this branch exists to remove, so worth
naming. This file said S1.5 "satisfies the tag rule" enforced by the cell; round 4
had replaced rule-inference with a reviewed inventory, and a carrier with an
invented reason passes. And "total over the two populations that can OWE operator
work" was wrong: they are the two the check can DETECT. A spec can owe a human
something with neither signal - `EVA-1-pad-flag` says "the tier stays nightly until
the operator promotes it", a pending human call on a nightly spec with no token,
which nothing makes red. That limit is stated in the cell rather than papered over.

EVA-1 IS NOW TAGGED (2026-08-01), and settling it also settled what the tag
MEANS. The first instinct was to refuse, on the grounds that pending promotions
are everywhere and tagging them would pad the list straight back out. The corpus
refuted that: a dozen specs carry re-tier or promotion prose, but B10, BDOCK-1,
EVA-3 and the whole L1 family record promotions already DONE, so among untagged
specs EVA-1 was the only OPEN one and tagging it added exactly one. `S1.5` then
settles the principle - nightly, green, fully automated, and tagged purely
because something needs a person. So the tag means WORK ONLY A HUMAN CAN
DISCHARGE; `tier = "operator"` is one sufficient reason for it, never the
definition. Drop EVA-1's tag when the cadence call is made either way, not
merely because the spec is green - it already was.

STALE LEDGERS RECONCILED, since they are what let the tags rot in the first
place: `harness/fixtures/saves/README.md` and `todo-and-known-bugs.md` both still
carried a hire cost of `-24000` that the 2026-07-23 live run measured at
`-62113` (the specs were corrected that day; the ledgers were not, for over a
week), plus rows still reading VERIFY-PENDING-OPERATOR for constants long since
proven, a "all 7 stay pending-fixture" re-tier note, and a resolved
L1-passive-sandbox blocker. Four L1 specs also still carried "pending-fixture:
... not committed yet ... re-tier to daily once the fixture lands" headers two
lines above `tier = "daily"`.

NO BEHAVIOUR CHANGE: the tag gates nothing, no tier moved, nothing selects on it
but a generic `--tag` - the gain is that `--tag pending-operator` returns 5
scenarios that genuinely owe operator work instead of 9 padded with six finished
ones and missing two live ones. No CHANGELOG entry, matching the 2026-07-26 precedent. Suites: lib 1071
(6 new), provision 203, missions/lib 1107, dotnet test 19,526 passed / 1
skipped.)

Prior: 2026-07-31, second session (THE TWO LIVE-TESTABLE CLAIMS OF THE
SESSION BELOW ARE NOW LIVE-PROVEN AGAINST THE REAL GAME, AND THE LEDGER CAPTURE
CROSS-CHECK IS ARMED FOR THE FIRST TIME. (1) HARNESS-INJECT-FAILS-OPEN proven
closed end to end in a worktree where `Source/Parsek.Tests` had never been built -
the deterministic trigger: `S1.5-rewind-loop` terminated in UNDER A SECOND
(`wallSeconds=0`; wall-clock 19:26:47 -> 19:26:48), PRE-BOOT
(`kspExit.code=null`, `collectLogs.ran=false`, zero driver steps), INVALID
`stage-inject-noop`, attempt 1 only with NO `_a2` retry, and the error named both
cause and remedy - while the injector itself still exited 0, which is the fail-open
being closed. Running that named remedy verbatim (`dotnet build Source/Parsek.Tests`)
then staged, booted and went GREEN on attempt 1, 63 s (`2026-07-31_1627`). No
divergence from the PR #1397 claim, so no driver fix was needed. (2) The
FLOWN-SCENARIO ARMING PATH was walked end to end on `CL-2-pod-impact-ledger`, one
flight per known-gate-3 checklist step, all PASS attempt 1: `2026-07-31_1630`
baseline reproduced the finding (3 awards UNEXPECTED, report-only, ut
12.5 / 19.1 / 119.9), `2026-07-31_1638` with `utWindow`s declared drove the
unexpected rows to ZERO (`reportOnly` 3 -> 0) with expected totals byte-identical,
and `2026-07-31_1645` flew GREEN with `captureCrossCheck = "gate"` LIVE. CL-2 is now
the FIRST committed spec that gates on the ledger capture and the only one declaring
a `utWindow`; the two whole-set guards became ALLOWLISTS carrying that evidence. The
windows are PHASE BOUNDS, and the three flights proved why: the second `Progression`
award measured 19.1 / 19.0 / 19.1 across them. This is the ONE deliberate committed
verdict change - the documented operator action - and nothing else moved. (3) The
review's in-pattern follow-up closed, then went further: the pre-launch ledger gate
no longer MIRRORS a chosen subset of the oracle's entry rules, it DELEGATES to
`oracle.parse_manifest_entries` outright, carving out only the funds
fill-from-capture rule (the one rule that reads the captured pool, so raising it
pre-launch would refuse a spec the oracle could accept). The mirror boundary this
session first shipped did not survive review: running the oracle with
`captured=None` rejects every rule deterministically, and two of the supposedly
"semantic" ones (`seq`, `stockReason`) are hand-written on every CL-2 entry.
Twelve entry shapes that used to pass ADMIT and hard-fail after the flight now red
in seconds, plus an unbounded-integer `ut` that used to raise OverflowError out of
`validate_spec` and abort the WHOLE batch. Suites after merging origin/main (which
landed R9's save-parse verifier and its S4.1 arming in parallel): lib 1065, provision
203, missions/lib 1107, all green.)


NOTE ON THE TWO ARMINGS THAT LANDED THE SAME DAY, so the two "first and only
armed" claims below do not read as contradicting each other: they are DIFFERENT
KNOBS. `S4.1-rewind-merge` is the first and only spec arming the M-C2 save-parse
verifier (`[expectations.rewind] gating = true`); `CL-2-pod-impact-ledger` is the
first and only spec arming the M-B2 ledger capture cross-check
(`[expectations.ledger] captureCrossCheck = "gate"`). Each has its own whole-set
allowlist cell, and neither affects the other.

Prior: 2026-07-31, second session (R9's SAVE-PARSE VERIFIER IS ARMED AND
LIVE-PROVEN ON S4.1, closing the promotion the report-only landing left pending
that same day. Branch `r9-arm-s41`. `S4.1-rewind-merge` is the FIRST and ONLY
committed spec carrying `gating = true`, so the rewind save surface stopped being
a recorded reading and became a gate. THREE runs, in the order the house rule
demands: `2026-07-31_1628` REPORT-ONLY READING (PASS, 59 s) measured
`parsed=true scenarioFound=true blocks=["rewind"] armedBlocks=[] mismatches=[]`
with `observed.rewind = {supersedeRows 0, tombstones 0, rewindPoints 0,
rewindRetirements 0}` - every reading already inside the spec's declared
`max = 0` windows, so arming moved NO verdict and only made existing behaviour
load-bearing; `2026-07-31_1635` ARMED (PASS, 59 s, `status=PASS gating=true
armedBlocks=["rewind"]`); and `2026-07-31_1637` the NEGATIVE CONTROL - a
temporary `supersedeRows = { min = 1 }`, since reverted - which reddened
`PARSEK-FAIL(save-structure)` with `mismatches=["rewind.supersedeRows 0 < min
1"]`. That third run is the load-bearing one: a gate nobody has watched fail is
an assumption. PRECISE ABOUT WHAT IT PROVED: the control inverted the window
to `min = 1`, so it drove the `< min` comparator, NOT the armed window's own
`> max` branch - what the flight established end to end is the mismatch ->
verdict -> `PARSEK-FAIL(save-structure)` PLUMBING, which was genuinely
unproven live; the `> max` direction itself stays unit-covered
(`test_saveparse.py`). TWO GENUINE UNKNOWNS ANSWERED. The merge REAPS `rp_b9_root` -
`rewindPoints` measured 0 and the produced save carries no `REWIND_POINTS` node
at all; recorded, deliberately NOT pinned as a window (one observation is not a
window). And the B9 corpus writes NO `BRANCH_POINT` rows - `branchPoints={}` is
a TRUE reading, verified against the raw .sfs, not a parser miss; topology there
is carried by `parentRecordingId`. THE VERDICT-NEUTRALITY GUARD WAS NOT DELETED
on the way: `test_no_committed_spec_arms_gating` became an explicit allowlist
pinning exactly `{"S4.1-rewind-merge.toml"}`, so a second spec arming still reds
until someone edits it deliberately and cites its run ids, and
`test_s41_declares_the_rewind_block_unarmed` flipped to `..._armed` and now also
asserts the WINDOWS are untouched, so arming cannot smuggle in a re-pinned
window. ONE DEFECT FOUND AND FIXED IN PASSING: `--dry-run`'s `[VERIFY]` line is
a hand-maintained string literal that never listed `saveParse`, so on the day
S4.1 armed, the plan for the one gating scenario advertised a chain WITHOUT the
gate - it now renders armed / report-only / facets-only states, pinned by FIVE
cells. The declared-but-unarmed rendering has NO committed spec to pin it
against (S4.1 is the sole declarer and it is armed), so it is pinned with a
SYNTHETIC spec - which is also what makes `armed` and `declared`
DISTINGUISHABLE to the suite: over the committed corpus alone they are the
same predicate, and a regression advertising a merely-declared block as an
armed gate passed every cell until that comparison existed.
CL-2 STAGE-B CALIBRATION MEASURED OFF THE STAGE-A RUN, not
authored (CL-2 itself IS stage A; stage B is unwritten): `2026-07-31_1641_CL-2-pod-impact-ledger` (PASS, 168 s, declares no
M-C2 block so the row is pure measurement) read `rewind` all-zero and
`structure {trees 1, committedTrees 1, recordings 1, terminalStates
{Destroyed: 1}, branchPoints {}}`. That is the PRE-REWIND baseline, NOT stage
B's windows - stage B rewinds across CL-1's crew loss, so pinning off this block
would assert the absence of the thing stage B exists to exercise; numbers and
that caveat are in `todo-and-known-bugs.md`. Suites: lib 980 (3 new), provision
203, missions/lib 1103, dotnet test 19,490 passed / 1 skipped. Environment note:
three sessions shared `automation/stock-minimal` during this work; every flight
was gated on the harness run-lock being free and hash-verified the deployed DLL
before and after - it was `a7a21acb` and UNCHANGED across all four FLOWN runs. FIVE attempts were
made, not four: an earlier CL-2 attempt was terminal driver-INVALID
(`tooling-venv`, no KSP boot) because this fresh worktree had no
`missions/.venv`, and because run ids are minute-granular it wrote to the SAME
result path the PASS then overwrote - so
`2026-07-31_1641_CL-2-pod-impact-ledger.json` holds the PASS run, but that id
is not uniquely attributable.)

Prior: 2026-07-31 (R9's HARNESS HALF SHIPPED-REPORT-ONLY: the M-C2
save-parse verifier landed on branch `claude/r9-save-parse-verifier-tshhzv`. A
new pure-Python parser (`harness/lib/saveparse.py`, oracle-precedent sibling)
reads the produced save's ParsekScenario surfaces - RECORDING_TREE topology,
supersede rows, tombstones, rewind retirements, REWIND_POINTS/slots - and a new
`saveParse` verifier row evaluates `[expectations.rewind]` and the new
`[expectations.recordings.structure]` block, recording measured facets on every
driver-valid run. VERDICT-NEUTRAL BY CONSTRUCTION: S4.1 already declares a
rewind block, so evaluation is REPORT-ONLY unless a block arms `gating = true` -
declared by ZERO committed specs, guarded by `test_no_committed_spec_arms_gating`.
S4.1's supersede-row/tombstone asserts therefore now PRODUCE READINGS instead of
being recorded SKIPPED, but still move no verdict; `rewind` left
`RESERVED_EXPECTATION_BLOCKS` (sole-owner rule), `route`/`loop` stay reserved
with zero declarers. LIVE PROOF PENDING: one local S4.1 + one CL-2 run to read
the report-only `saveParse` facets out of `results/<runId>.json`, then arm
gating per scenario. An adversarial review round (fresh reviewer, scratch
clone, 4000-input byte-level fuzz + C# writer cross-check) then hardened three
fail-opens before merge: a zero-byte/whitespace persistent.sfs now parses as a
DEFINED FAULT (it was trivially brace-balanced, so an armed max=0 window would
have PASSED on a torn save); a produced save with NO ParsekScenario node now
raises a named mismatch when a block is declared (Parsek-never-loaded was
indistinguishable from "zero rows") with `scenarioFound` recorded on the row;
and gating became PER-BLOCK (arming a proven block no longer silently promotes
a second exploratory block to a gate - `armedBlocks`/`armed_mismatches` carry
the split). Headless-only validation this session (no KSP in the build
environment): all three suites green - lib 977, provision 203 (+5 skipped),
missions/lib 1103.)

Prior: 2026-07-31 (THREE FAIL-OPEN/MECHANISM ITEMS CLOSED on branch
`harness-hardening-2`, pure Python + docs, no committed verdict moved. (1) The
ledger capture cross-check is now ARMABLE ON FLOWN SCENARIOS: a manifest entry may
declare `utWindow = [lo, hi]` and the leg-A corroboration matches inside the window
instead of on the unpinnable exact UT that made CL-2's three correctly-declared
awards all read "unexpected" - opt-in per entry, zero committed specs declare one,
M-B2 independence untouched; see the rewritten arming paragraph in known-gate 3.
(2) HARNESS-INJECT-FAILS-OPEN is FIXED driver-side: staging now asserts the
injection POSTCONDITION (non-empty `Parsek/Recordings/`, plus `rp_b9_root.sfs` for
`rewind-b9`) and a miss is terminal INVALID(`stage-inject-noop`) pre-boot,
non-retryable, naming the likely cause and remedy - no more full flights burned on
`unknown-rp`. (3) HLIB-ALLOWBATCH-NONLITERAL-FAILS-OPEN is FIXED: a non-literal
`AllowBatchExecution` argument now resolves fail-CLOSED with an `<unresolved:...>`
marker the sync sweep reds, instead of silently reading batch-allowed; the tree
recount is unchanged (542 declarations, zero markers). All three suites green:
lib 927, provision 203 (5 skipped), missions/lib 1103. Forensics: the three struck
entries in todo-and-known-bugs.md.)

Prior: 2026-07-30, second session (S4.1's HONESTY CAVEAT IS RESOLVED and the
S4.1-IDLE-DISCARD refusal guard is LIVE-PROVEN. The unentered guard was
S4.1-PREFIX-RACE: `InvokeRewind` completes when the re-fly MARKER lands, but the
scene-exit prefix gates on `ParsekFlight.HasActiveTree`, which only goes true when
the `RestoreActiveTreeFromPending` coroutine later resumes the restored tree as
active (~300 ms after marker completion), so a driven exit issued in that window
slipped past the prefix un-intercepted to the deferred post-transition dialog. The
candidate cause the caveat named (`f97717744`) is REFUTED - run `2026-07-28_1939`
carried it and still entered the prefix. Fix in the seam: `AnswerMergeDialog` now
waits (bounded, 30 s) for the resume to settle before driving the conclusion, S4.1's
spec pins the pre-transition route with three new required tokens (the refusal line,
the ReFlyAttempt pre-transition dialog, the synchronous answer), and a fresh 5-run
sweep off branch `s41-prefix-live-coverage` flew 5/5 PASS on attempt 1
(`2026-07-30_1746`-`_1751`, 57-76 s) with all three firing in every log. Lifetime 12
PASS / 3 INVALID. B16's tier promotion remains the open human call.)

Prior: 2026-07-30 (S4.1's FLAKE QUARANTINE IS CLEARED, and the second of the
two human calls the 2026-07-29 session left open is now made. S4.1-IDLE-DISCARD - the
scene-exit idle-on-pad auto-discard tearing down a LIVE re-fly session's tree - was
ruled a real defect and FIXED on branch `fix-s41-idle-discard`
(`SceneExitInterceptor.TryAutoDiscardIdleActiveTree` now refuses while a re-fly marker
or a merge journal is live, falling through to the conclusion dialog). The deliberate
multi-run sweep the S4.1 row demanded then flew: FIVE consecutive runs, every one PASS
on attempt 1, 57-72 s, and the generated flake ledger now reads total=5 numerator=0
rate=0.0 quarantined=false. Lifetime 7 PASS / 3 INVALID. THE CAVEAT, carried in full
by the S4.1 row: not one of the five runs ENTERED the new refusal guard - zero
`TryAutoDiscardIdleActiveTree: idle detected` lines and zero `refusing - refly-active`
lines - so what the sweep proved was that S4.1 is deterministically green, NOT that the
guard fires live; resolved above.)

Prior: 2026-07-29 (THE NEVER-RUN BACKLOG IS EMPTY. All four scenarios that
had never produced a green unattended run flew that day and passed: S1.5-rewind-loop
(`2026-07-29_1528_..._a2`, 69 s - its first execution EVER), S4.1-rewind-merge
(`2026-07-29_1530`, 71 s), B1-pad-hop's chute re-prove (`2026-07-29_1532`, 408 s,
`craftCanopyObserved` MET at 11,963 m off the OBSERVED ParachuteState) and
B16-eve-orbit (`2026-07-29_1718`, 1,825 s, PASS on its FIRST FLIGHT). All 56
committed scenarios now have at least one fully-unattended PASS and "Committed, not
yet live-run" is empty for the first time. The session also reconciled this file
against the archived results, because the "not yet live-run (14)" section had gone
badly stale: 12 of its 14 rows had ALREADY flown green, some five days earlier - the
three FORGE specs (fixtures harvested AND committed), all five L1 specs, BDOCK-1 and
R1. Only S1.5 and B16 were genuinely unflown. One driver-side finding filed, no
product findings: HARNESS-INJECT-FAILS-OPEN, where a no-op fixture injection reports
success and the miss surfaces three minutes later as an unrelated seam rejection. Two
things deliberately NOT done, both left as human calls: B16's tier promotion (its
spec's rule has two conditions and only the green flight is met) and clearing S4.1's
flake quarantine (lifetime 2 PASS / 3 INVALID against the then-unfixed
S4.1-IDLE-DISCARD - that second call was made 2026-07-30, above).)

Prior: 2026-07-27 (R5 SHIPPED: the isolated-batch seam argument. `RunTests`
takes `isolated = "true"` and the autorun dispatcher reads `PARSEK_AUTORUN_ISOLATED=1`,
both routing to `InGameTestRunner`'s `*IncludingFlightRestore` entry points. Those were
public and fully implemented before this change and called from exactly two places, BOTH
INTERACTIVE, which left 68 already-written tests unreachable by any unattended path. That
is Cause B in the roadmap, CLOSED as a capability gap. Three things this turned up that
the roadmap had wrong: (1) the non-isolated form does NOT print `total=0` - filtered
tests are marked Skipped, not dropped, so `total` is identical on both paths and the
proof is the passed/skipped split; that makes the proof STRONGER, since
`passed=0 failed=0 total==skipped` is exactly the vacuity family the anti-vacuity gate
already rejects. (2) The hlib companion was load-bearing, not optional: `InGameTestDecl`
did not carry the restore flag at all, so the source-sync gate would have rejected a
correct isolated pin. (3) The fixture is the expensive trap - `gloops-airshow` has a
1-part engineless pod, and both `SceneExitMerge` cells stage and wait to clear 80 m, so
copying the H-series fixture would have produced an all-skipped red indistinguishable
from "the arg does not work". Shakedown spec `H21-scene-exit-merge-isolated` FLEW PASS on
attempt 1 in 101 s, matching `total=2 passed=2 failed=0 skipped=0
category=SceneExitMerge scene=FLIGHT` token for token plus both
isolated-path-only log tokens. Coverage 96 -> 97 covered (of 242 registry cells at the post-merge numbers; the
denominator moved 241 -> 242 in the same merge wave, from a cell this change did
not add). The one new cell is D1
`commit-scene-exit`, which the roadmap listed among the three no fixture, mission
profile or verb could produce.

Prior: 2026-07-27 (H7-H20 ALL FLOWN, all 14 PASS on attempt 1, 805 s / 13.4 min
for the group - 49-71 s each, against 2,825 s for B13 alone. What flying added over the
static derivation, precisely: the `total=` values were already gated by the source-sync
test and needed no flight; what no static analysis predicts is the passed / skipped
SPLIT, and 13 of the 14 pin that as a LITERAL, so a PASS means the runner printed the
pinned line token for token. All 13 pre-flight derivations were right. H20 was the
exception - its interim pin proved only passed>=1 and its split was in no artifact - and
it is now CLOSED: re-flown alone so its log survived, measured passed=2 skipped=0 (the
endpoint-overlap probe fired, walkback executed), pinned whole. All 14 now pin whole off
measured lines. Tiering decided on failure mode rather than cost:
H18 promoted to daily because it is the sole guard for the GameEvents subscription
contract and a dropped Add() is silent; the other 13 stay nightly pending flake data.
Prior: 2026-07-26 (THE IN-GAME CATEGORY GAP, measured and half closed.
Parsek ships 542 in-game runtime tests across 98 categories; committed scenarios
drove EIGHT of those categories, so 89 were written, passed under Ctrl+Shift+T, and
never executed in any unattended run. The inventory is now DERIVED from the C#
attributes rather than guessed at, every category triaged A/B/C with the reason
stated, and 14 categories wired as batch-only specs H7-H20 over the existing
gloops-airshow fixture: 22 of 97 categories driven at the time, 201 of 539
declarations inside a driven category and 179 of them that would actually execute.
None has flown - 13 pin their tally WHOLE from a source derivation that closes
(attribute-exact total, plus a transitive scan proving no reachable
InGameAssert.Skip), and H20 carries the honest interim form because its split is
genuinely fixture-measured. Detail + fly order:
autotest-ingame-category-inventory.md.
Three findings fell out. (1) The prior FUTURE recommendation to wire
EvaSpawnPosition AND CrewReservationLive over the injected corpus was half wrong:
the corpus injector STRIPS every spawnedPid line by construction, so
CrewReservationLive can only ever emit the vacuous total=2 passed=0 skipped=2 -
it needs a C# corpus change, not a spec. (2) A FOURTH vacuity trap, invisible to
the anti-vacuity gate: a test that RUNS, PASSES and asserts over nothing (a store
walk over an empty store), sometimes behind a silent yield break that reports
PASSED rather than Skipped. Only the fixture defends against it, so the four
corpus-backed members pin recordings.count=272. (3) A harness bug:
hlib._pin_literal_word excluded `-`, making all seven hyphenated Pipeline-*
categories structurally unpinnable while the runtime parser accepted them fine.
Prior: 2026-07-26 (PLAYBACK is now gated, twice. S1.6-render-parity
LIVE-PROVEN on its first flight and S1.7-maprender-parity built and LIVE-PROVEN
the same day: the production recorded-vs-rendered parity oracle had existed and
been wired for months with 47 in-game tests asserting through it, and no
scenario had ever driven either category. Both pin their batch tally WHOLE and
both carry a load-bearing negative control - a deliberately wrong reference that
must FLAG on the same draw the correct reference reads as zero - measured at
~545x tolerance on S1.6 and ~488x on S1.7, so neither zero-drift assertion can
be a circle compared with itself. S1.7's first flight also exposed a harness
FALSE POSITIVE: the Tier-C anomaly sweep was a bare substring search over
KSP.log, so a PhaseSpineSwap test line whose LABEL is `parity-drift` and whose
body reads `over=False` reddened a run containing ZERO `phase=Anomaly` raises.
The sweep is now anchored on the tracers' real raise shape, and the same
investigation found the reverse defect, left UNRESOLVED and now REPORTED per-run:
the harness token set has drifted from what the mod emits - `icon-jump` is dead
(the probe raises `reason=icon-teleport`) and NINE further reasons are ungated.
That enumeration is now derived from the C# source by a harness test rather than
hand-listed, after the first pass counted five and missed the four raises that
reach EmitAnomaly through MapRenderTrace's cutover-hardening wrappers.
Also FIXED: `allowedAnomalies` was misplaced under `[expectations.logContracts]`
in all 28 pre-existing specs, so S1.4's declared exception had never been in
force; validate_spec now REJECTS the misplaced form and every spec declares the
key where run.py reads it, S1.4 keeping the gate strength it actually flew with.
The first 2026-07-26 merge of main brought four more specs written against the
old shape (B11 / B12 / B15 / B16; B13 and B14 had pre-moved their own key in
anticipation of exactly this collision) and they were relocated in that merge
commit. The second merge the same day (batch-coverage + tally-gate) needed no
relocation: main still carries the misplaced form in 32 of its own 36 specs
because it has never received this branch, the auto-merge kept the relocated
side on every one of them, and M1 / M2 were authored with the key already in
`[expectations]`. All 38 committed specs declare it where it binds; the scan
that proves it is `test_no_committed_spec_still_carries_the_misplaced_key`.
Prior: 2026-07-26 (batch coverage) - VACUOUS-BATCH class found and closed; the
first two D11 scenarios; four of the seven RunTests tallies now MEASURED off
live flights and the other three honestly labelled; and one real Parsek defect
caught by the new M1 scenario's very first flight.

B10-career-passive-safety - shipped, daily tier - was proved live to read GREEN
while executing ZERO tests: its RecordingInvariants batch ran at SPACECENTER,
where both FLIGHT-scene tests are scene-skipped, and its only contract
`BATCH_COMPLETE v1 .* failed=0\b` cannot tell `passed=0 skipped=2` from two
passes. L1-passive-sandbox had the identical shape. Both fixtures are vessel-less
by design, so the CATEGORY moved to GameActionsHealth (4 scene-agnostic read-only
tests) rather than papering the pin over a category the fixture can never host.
The class is closed harness-side: hlib.validate_spec now synthesizes every
BATCH_COMPLETE line a vacuous batch could emit and REJECTS any spec whose
contract would accept one - checked by construction, not by a syntactic "must
mention total=" tautology, with a reason-required opt-out. Scope of that
guarantee, after review found three ways around the first cut and all three were
closed: a batch-owning spec must now declare exactly ONE RunTests step naming
exactly ONE category (a second step ran ungated; a multi-category aggregate
cannot express per-constituent non-vacuity), and vacuity detection requires ONE
single required pattern to reject the whole vacuous family plus the known
batch-independent decoy line, because evaluate_expectations searches each
pattern over the whole log independently. It still blocks only passed==0 - the
`passed=[1-9][0-9]*` placeholder form accepts 1-of-42 by design.

SECOND ORDER: pinning the tally whole makes each pin a hardcoded copy of a number
that lives in C#, so CommittedBatchTallySourceSyncTests now cross-checks every
pinned tally against the [InGameTest] attributes in Source/Parsek - total exactly,
skipped as a floor (run-time InGameAssert.Skip guards are not statically
derivable, which is why L1-passive-sandbox legitimately pins skipped=3 over a
category whose attributes force 0). Adding an in-game test to a pinned category
now reds locally instead of on the next nightly. The same sweep also asserts
RECOGNITION completeness: any `InGameTest` / `InGameTestAttribute` token sitting
in an attribute bracket that the strict parse did not claim (a stacked
`[Obsolete(...), InGameTest(...)]` list, the explicit `Attribute` suffix, a
namespace-qualified name, or a `[method: ...]` target) is reported as UNCLAIMED
and reds, so a form the parser does not model can never silently shrink a
category total.

NEW: M1-mission-loop-unit and M2-periodicity-solver take D11 from 0/18 to 8/18 by
running the Missions / Periodicity in-game categories, which needed no fixture
and had never run because no spec named them; both gate the mission-loop PLAN and
the periodicity SOLVER, explicitly not the playback.

2026-07-26 ran SIX flights, of which FIVE passed. The one red is M1 flight 1, and
it is the run that found the sidecar leak below - the pin doing its job, not a
blemish: B10 PASS, M1 PARSEK-FAIL, M2 PASS, M1 re-fly PASS, L1-passive-sandbox
PASS, S1.4 PASS (`harness/results/summary.txt`).

MEASURED - exact pin already committed before the flight, so the run's
expectations PASS matched the whole line: B10 `total=4 passed=4 skipped=0` (the
vacuity fix proven - the same step used to emit `total=2 passed=0 skipped=2`),
L1-passive-sandbox `total=4 passed=1 skipped=3`, M2 `total=11 passed=7
skipped=4`, M1 `total=12 passed=5 skipped=7`. H5's `total=2 passed=2 skipped=0`
is measured off its own 2026-07-19 archived line.

NOT fully measured, and now labelled as such rather than claimed: S1.4's
`total=42 passed=40 skipped=2` - the 2026-07-26 run executed the LOOSE
`passed=[1-9][0-9]*` pin, so it proves total=42 / failed=0 / category / scene but
not the 40/2 split, and that run archived no log (collectLogs ran=false on a
PASS). H6's `total=7 passed=7 skipped=0 ... scene=FLIGHT` is DERIVED: the
2026-07-24 PASS was against a pin carrying only `failed=0 skipped=0`, and the
scene token has no H6-specific evidence at all (it is inferred from the shared
gloops-airshow fixture's FLIGHT LoadGame route). Both pins STAY - each fails
LOUD, never green - and both are re-derivable from the spec comments.

M1's first flight also found a REAL Parsek-side defect and its `recordings.count
= {0,0}` pin is what caught it: in-game tests that register synthetic trees
through the real `RecordingStore.CommitTree` were leaving orphan `.prec` /
`.pann` / `.prec.txt` sidecars in the produced save. `CommitTree` flushes
`SaveRecordingFiles` per recording; `RemoveCommittedTreeById` is memory-only by
design; once those ids leave the store `CleanOrphanFiles` preserves the files
forever - the S0.5 discard-residue shape again. Fixed at the SHARED path: the
one-test `PersistenceSplitOptimizerTestCleanup` is generalized to
`InGameTestSidecarReaper` with a known-ids guard, wired into every enumerated
in-game site that reaches a real commit - the five DIRECT `CommitTree` callers
plus, after review found them, the four that reach one INDIRECTLY through the
production merge / live-recording paths (both `MergeDialog` and `SceneExitMerge`
deferred-merge canaries and the EVA ghost-snapshot canary), all now reaping at
the shared `RemoveCommittedTreeByIdForRuntimeTest` helper. Re-flown green with
the pin untouched.

Prior 2026-07-25 (ORBIT lane): REVIEWED by three Opus reviewers and the
findings applied: two blocking liveness/commanded-vs-observed defects in the new
capture tail are fixed, the lane's headline claim is now actually VERIFIED by a
commit-terminal log token instead of only asserted, and all four affected
missions were RE-FLOWN green on attempt 1 - B11 1,270 s, B12 581 s, B5 468 s,
B6 359 s. B7 was flown at HEAD too and does NOT pass, for the pre-existing
300 km-target reason its own row already gated on, not a lane regression.
Details in roadmap item 2. Prior: Mun/Minmus ORBIT lane CLOSED and LIVE-PROVEN: roadmap
item 2 - "capture burn + commit-in-target-orbit terminal" - is implemented AND
flown green as B11-mun-orbit + B12-minmus-orbit. The roadmap's informal "B8" label collided
with the catalog's existing B8/B9/B10 rows, so the lane took B11/B12 and both
docs now carry the mapping. The lane buys ONE Parsek surface no flyby reaches:
a recording that ENDS parked in a foreign SOI and is COMMITTED there. Built by
turning on a new `captureEnabled` param in the LIVE-PROVEN B5 flyby machine -
ascent, transfer, TLI, corrections and the whole warp policy are byte-identical
to the 26 B5 flights, and with the flag off none of the new code is reachable -
plus a four-phase tail: PLAN-CAPTURE (MechJeb circularize-at-periapsis) ->
CAPTURE-BURN (NodeExecutor with autowarp set EXPLICITLY; the done evidence is a
BOUND orbit, since a hyperbolic approach reads a NEGATIVE apoapsis) -> PARK (the
forge_lko held-dwell gate re-pointed at a foreign body: throttle cut, nodes
cleared, SAS+RCS held, rails dropped to 1x for 180 game-s of recorded coverage)
-> ORBIT-COMMIT (the B-DOCK route-1 mid-mission seam CommitTree) ->
ORBIT-COMMITTED. Leaving the target SOI anywhere in the tail is an ASSERT-FAIL,
so B5's free-return cannot green an orbit mission. Every actor-dependent phase
carries a GAME budget AND a distinctly named fast-fail
(capture-executor-no-start, capture under-burn, never-stabilized vs
never-HELD-stable, tree-commit-seam-returned-X). New D1 registry cell
`commit-in-foreign-soi`. 46 new headless tests; all four suites green. BOTH
FLOWN GREEN 2026-07-25: B11 FULL PASS three times (flight 2; flight 3 as the
confirmation the changed TARGET-FLYBY profile owed; flight 4, the count-pin run)
at wall 1,269-1,271 s with capture eccentricity 0.000127 and the flyby warp down
to 27 game-s / 2 commands from 8,213 game-s; B12 FULL PASS on flights 4 and 5 at
wall 580 s with capture eccentricity 0.00026 and a 194,543 game-second coast
flown in 26 wall-s at ratio 7,535 on 3 warp commands. B6-minmus-flyby also
re-flew green (wall 359 s), paying off the
confirmation it owed after the correction-budget regression. Four shared-machine
findings came out of the lane - our 600 s no-start watchdog colliding with
MechJeb's own 600 s pre-ignition hold, a GAME-time correction budget spent by the
aim-then-warp it waits on, the metastable coast warp-thrash behind KSP's NaN
`time_to_soi` under a warp ramp, and warp inherited across the SOI boundary at
10,000x - all four with forensics in todo-and-known-bugs.md. Both count windows
are now PINNED at {8, 8} from measured green runs (B11 flight 4, B12 flight 5),
with the caveat recorded in both specs: the count is COMMIT-BLIND (sidecars are
written for the ACTIVE tree too, and two never-committed runs produced the same
8), so it guards recording TOPOLOGY and the commit is guarded by log tokens
instead.

Prior 2026-07-25: B1-pad-hop DE-LISTED from live-proven: its
2026-07-19/20 PASSes proved the flight but its chute never opened - the
recordings carry ZERO Parachute* events - and its DOWN terminal gated on the
machine's own COMMANDED chute latch, so a ~300 m/s terminal-velocity impact was
awarded the "chute-deployed impact" success end for four months. Same
automateSafeDeploy=0 root cause EVA-4 flight 1 hit. FIXED with the same
live-proven technique: arm at the apoapsis crossing, and gate BOTH the DOWN
terminal and a new craftCanopyObserved assertion on the OBSERVED kRPC
ParachuteState; the spec also now requires the ParachuteSemiDeployed /
ParachuteDeployed Part-event tokens, so B1 claims D7 chute-two-phase for the
first time. Budgets DERIVED from EVA-4 flight-2's measurements rather than
guessed. New gate 7 names the general class: commanded-vs-observed assertions
fail OPEN, and B4's chuteDeployed is the known open instance. Its next nightly
IS its re-prove. Prior: EVA-4-atmo-chute LIVE-PROVEN on flight 2 - FULL PASS
attempt 1, all seven verifiers: the craft's canopy OBSERVED Deployed, handoff
at 1,606 m / -23.2 m/s, the kerbal out mid-air, its own chute verified, a
steady -4.5 m/s chuted descent and "down=true situation=LANDED alive=true",
with the mid-flight EVA branch + Atmospheric TrackSections on the kerbal's own
recording. All four operator pins closed: count PINNED 3, the `'kerbalEVA`
part-name token confirmed, the semi-deployed descent rate MEASURED at about
-236 m/s peak (which trimmed descentTimeoutSeconds 480 -> 240), and the kerbal
lands alive. Two post-live in-family hardenings on the same branch: the
EVA-window gate is now K=2 debounced (stock flips ParachuteState to DEPLOYED at
the START of the ~8 s canopy animation, so one glitched frame could have
certified a terminal-velocity EVA), and EvaChuteDeploy's CompleteOk now
requires the RAW per-poll aliveness read so a death inside the 3-poll loss
debounce cannot green out. Prior: EVA-4-atmo-chute FLEW ITS FIRST FLIGHT and
ASSERT-FAILed exactly as designed - fast, self-explaining, no budget burn:
"eva-window-missed: altitude 702m fell below the window floor 800m (vspeed
-295.2m/s, ... craftChute armed)". Root-caused from the measured per-frame
profile + the produced recording + decompiled ModuleParachute: arming the
craft's chute at 2500 m is INERT, not late - the fixture persists
automateSafeDeploy=0 and stock never opens a chute at ~300 m/s in dense air, so
the recording carries ZERO Parachute* events. Re-tuned to arm at the APOAPSIS
crossing, raise the stock full-deploy altitude, and gate the EVA window on the
chute's OBSERVED kRPC state instead of the machine's own "we commanded it"
latch. Prior: EVA-4-atmo-chute lands, NEVER FLOWN: the first
ATMOSPHERIC mid-flight EVA case - new seam verb `EvaChuteDeploy` [the kerbal
personal parachute, driving the same public `ModuleEvaChute.Deploy()` both
stock player paths call] + new mission `eva4_atmo_chute` whose terminal is an
AIRBORNE EVA window rather than a landing, reusing the committed b1-pad-craft
fixture. Claims the previously-unclaimed D7 chute-two-phase cell. Awaits its
first live flight. Prior: FORGE-eva2-lko lands: the FIRST ORBITAL fixture-forge
[mission forge_lko] that stamps the crewed-LKO fixture EVA-2 is waiting on, so
EVA-2-orbital-board's only remaining gate is the operator forge run + harvest.
Prior: H6-route-rewind-timeline LIVE-PROVEN on its first live

Prior: the whole EVA lane (EVA-1/2/3 + both forges) is LIVE-PROVEN; every
recordings-count window is PINNED to its measured topology (EVA-1 4, EVA-2 2,
EVA-3 7, EVA-4 3) because logContracts are presence-only.
run - FULL PASS attempt 1, all seven verifiers green - the route-rewind wave's
last automated acceptance item; EVA-1-pad-flag first flight = EvaExit/EvaBoard/
commit chain green + analyzer clean, three PlantFlag/EvaExit defects found +
fixed across flights 1-3 [live CanPlantFlag() gate read; verified ladder
release; and the seam now waits for the SiteRename popup before answering so
afterFlagPlanted fires + the FlagEvent is captured]). Prior: EVA lane
prep - PR #1345 review follow-ups 1-5 all addressed; crew-by-name + launch_site
plumbing threaded; FORGE-eva3-pad forge spec + harvest path land; EVA
batch-autorun evaluated = NOT wired; 2026-07-23 M-C2 EVA verbs + EVA-1/2/3 specs;
B-DOCK dock/transfer/undock lane + fixture-forge; all headless-green. This file
is the single at-a-glance answer
to "what is done, what is proven, what is gated" for the automated testing
initiative, so nobody has to re-derive status from code.

## Purpose - never forget it

This system exists for exactly one reason: MAKING PARSEK BETTER. Every
mission, verb, and scenario is an instrument for verifying Parsek's behavior
- that recordings are correct, complete, and schema-clean; that the ledger
reproduces career state exactly; that rewind/re-fly, playback, ghosts, and
routes survive real flight histories. Flying rockets is never the point: a
mission earns its place only by the Parsek recording/ledger/rewind surface it
exercises. The end goal is the L-track Ledger Accuracy Campaign (grand oracle
career runs with repeated rewinds, oracle-diffed at every session boundary).
When prioritizing work, ask "what Parsek defect class does this catch?" -
that question has already paid: the initiative's real catches include the
INV2 double-cover recorder seam defect, the S0.5 orphan-sidecar leak, and
(2026-07-26, on a brand-new scenario's FIRST flight) the same orphan-sidecar
class in every in-game test that drives a real `RecordingStore.CommitTree`.

## Doc map (no duplicate documentation)

Each fact about this system lives in exactly one place:

| Doc | Owns |
|---|---|
| THIS FILE | Status: what is shipped, proven, gated; the historical roadmap record |
| `autotest-roadmap.md` | FORWARD build order: what we still cannot reproduce, grouped by cause, and the ranked dependency-justified sequence to close it |
| `automated-testing-plan.md` | Strategy + rationale (why the system is shaped this way; L-track definition) |
| `automated-testing-scenario-catalog.md` | The INTENDED universe: dimension registry D1-D18 vocabulary, scenario blocks, tiers, regression rotation |
| `design-autotest-*.md` (12 docs) | Per-module design authority (how each module works; binding contracts) |
| `harness/README.md` | Harness module mechanics: ownership boundary, how to run, submodule readiness |
| `todo-and-known-bugs.md` | Finding forensics: the full evidence trail behind every live finding |
| `harness/coverage/registry.toml` | The machine-readable coverage denominator (authoritative cell list) |
| `autotest-ingame-category-inventory.md` | The in-game category axis in DETAIL: all 98 categories with per-scene batch eligibility and self-skip surface, the A/B/C wiring triage, and the H7-H20 fly-order runbook |
| `test-coverage-audit-2026-07-29.md` | Full-stack coverage SNAPSHOT (all three systems + design-doc contracts, measured 2026-07-29) and the consolidated ranked gap register. A dated audit, not a living status doc |
| `design-testing-unified.md` | The cross-system explainer (how the three testing systems work and compose, the validation-pyramid/atomic-decomposition model, binding constraints) and the beyond-R14 program (visual validation, mode-axis expansion, fuzz/perf lanes); its build-order extension is indexed as roadmap Tier 5 |

If a status statement appears anywhere else, it is a pointer to this file or
it is wrong. MAINTENANCE RULE: any PR that changes a module's status,
live-proves a scenario, adds a test case, or opens/closes a gate updates this
file in the same PR (same discipline as CHANGELOG).

## One-paragraph summary

The system flies KSP missions unattended (kRPC + MechJeb autopilot, or the
Parsek file-drop command seam), records them with Parsek, and verifies the
result through the verifier chain (driver validity, in-game test batch,
offline recording analyzer, log validation, results schema, anomaly sweep,
expectations, the save-parse row added report-only 2026-07-31 and armed on
S4.1 the same day, and the ledger
oracle). Forty-two test cases are live-proven green end-to-end (the 38
rows in the Live-proven table below plus the four EVA cases in their own
section), including Mun/Minmus/Duna flybys with a certified no-1x-coast warp
profile, the Mun/Minmus ORBIT pair, the Mun/Minmus LANDING pair, and both Eve
cases - the flyby and, as of 2026-07-29, the ORBIT with its capture tail.
Counting the 16 in-game batch cases and the 1 isolated case in their own
sections, ALL 60 committed scenarios now have at least one fully-unattended
PASS, and the "not yet live-run" section is empty for the first time.
(The four newest are R12's consumers, all flown green 2026-07-30:
`H23-tracking-station`, `S0.7-exit-auto-commit`, `S0.8-switch-click-segment`, and
`CL-2-pod-impact-ledger` - the CL-1 extension, PASS on both of its flights.)
(B1-pad-hop was de-listed from
live-proven on 2026-07-25 - its PASSes proved the flight but its chute never
opened, and its terminal could not tell the difference - and was RE-PROVEN on
2026-07-29 by run `2026-07-29_1532_B1-pad-hop`, which asserts the OBSERVED
canopy and reaches LANDED. See its row below and gate 7. The 2026-07-25 sweep's
PASS never counted as that re-prove: it predates the merge of the canopy-gated
terminal, so it ran the old contract. B10 and L1-passive-sandbox were re-proven
on 2026-07-26 in a corrected batch category, after their earlier greens were
shown to have executed zero tests; M1 and M2 are new and flew the same day, and
S1.6 and S1.7 are the two render-parity cases this branch flew.) All
infrastructure modules are shipped and merged. The FIRST two-vessel lane
(B-DOCK: dock/transfer/undock, the logistics-route recording entry point) is
IMPLEMENTED and headless-green, pending a headless fixture-forge run + its
first flight. The Mun/Minmus ORBIT lane (B11/B12: capture burn, park, and a
commit while parked in a FOREIGN SOI) is LIVE-PROVEN on both axes as of
2026-07-25, as is the Mun/Minmus LANDING lane (B13/B14: a recording that ENDS on
foreign soil and is COMMITTED there); B15-eve-flyby is green and B16-eve-orbit
is committed but not yet flown. PLAYBACK is no longer a blind spot either:
S1.6 + S1.7 drive 47 in-game parity tests between them. Coverage stands at 98 of
242 registry cells claimed by at least one scenario (84 of 241 at the 2026-07-27
recompute; the batch-wiring + isolated-batch wave and the UI-mode cell landed
since, and the registry grew one cell). The most recent +1 is D14 `scene-ts`,
claimed for the first time by `H23-tracking-station` on 2026-07-30 - R12 made the
tracking station reachable at all. R12's other two specs claim no NEW value: D14
`scene-ksc` already had 8 claimants (the KSC-side L1 cases) and D1
`switch-segment` already had H12.
That 98 is RECOMPUTED from
`hlib.compute_coverage` over the committed specs + the registry, not carried
forward from either side: the "52" this sentence used to
print had drifted across many spec additions (it predates the EVA, B-DOCK,
ORBIT, LANDING and EVE lanes), the "70 of 239" the ORBIT lane measured on
2026-07-25 was already stale by the time the landing and Eve lanes merged (that
tree alone recomputes to 74 of 240), the "77 of 238" the batch-coverage lane
carried predated the orbit/landing registry cells, and the two sides of THIS
merge printed 75 of 241 and 82 of 240 - each right only for its own tree. The
claimed-vs-GREEN split is a DIFFERENT number and is deliberately not restated
here: it needs the run archive, and `harness/results/*.json` plus
`harness/coverage/coverage.{json,txt}` are generated + gitignored, so re-derive
it from a full results set rather than trusting a number in prose. The last
measured split was 2026-07-25's 58 green and 12 claimed-but-never-green over
that day's 70, and the never-green 12 were every cell claimed only by the two
un-flown rewind scenarios plus EVA-4's `chute-two-phase`. Breadth (EVA, orbit,
landing, docking, career-ledger lanes) is the frontier.

## Infrastructure modules (all SHIPPED and merged)

| Module | What it gives Parsek testing | Status |
|---|---|---|
| M-A1 offline analyzer | Recording invariants (INV1-INV9) over any save, RED gate, per-save findings baseline | SHIPPED (#1300/#1302/#1306); AnalyzerVersion 3; core in Parsek.dll so in-game H5 runs the same rules |
| M-A2 command seam | Drives Parsek actions kRPC cannot (record/commit/discard, rewind, dialogs, KSC actions, EVA, scene routing, stock switch clicks) | SHIPPED (#1301); **21 implemented verbs, 10 reserved** as of R12 (2026-07-30), which added `ExitToSpaceCenter` (additive) and PROMOTED `SimulateStockSwitchClick` out of the reserved list - the first promotion since M-C1 - plus an additive `scene=` arg on `LoadGame` that needs no table entry. Earlier: 19 implemented / 11 reserved (M-C1 + M-C2 grew the table). R5 (2026-07-27) gave `RunTests` an optional `isolated` arg routing to `RunCategoryIncludingFlightRestore`, unlocking the 68 tests that are `AllowBatchExecution = false` + `RestoreBatchFlightBaselineAfterExecution = true`; a value other than the exact lowercase `true`/`false` is REJECTED `isolated-arg-invalid` rather than falling back, as is a written-but-empty `category=` (which previously ran the WHOLE assembly). R5 added no VERB - the count below moved 18 -> 19 because EVA-4's `EvaChuteDeploy` was never recorded in this cell, and R5 re-derived it from `TestCommandVerbs.cs` |
| M-A3 autorun hooks | Unattended in-game test batches (PARSEK_AUTORUN_*) | SHIPPED (#1305); R5 added a THIRD env var `PARSEK_AUTORUN_ISOLATED=1` mirroring the seam arg. Deliberately a separate var, not a selector prefix: the selector is consumed verbatim as the `category=` token both the runner stamps and hlib's anti-vacuity probe synthesizes, and a prefix would desynchronize them and silently void that gate |
| M-A5 harness core | The orchestrator: admission, staging, seam driving, budget kill, verifier chain, verdicts, coverage/flake ledgers | SHIPPED (#1307, #1316); UNMET-mission tail skip added 2026-07-25 (per-verb `SEAM_VERB_TAIL_ROLE`: after an unmet mission only `cleanup` verbs are driven, so an EVA-4-class world-mutating tail can no longer fire over a flight that never reached its envelope). Settings-sidecar baseline added 2026-07-26: `SetSetting` on a sidecar-tracked setting persists INSTANCE-WIDE and Parsek applies it over every loaded save, so S1.4's `mapRenderTracing=true` had pinned the per-frame render tracer on for every later run; run.py now writes a deterministic tracers-OFF baseline at stage AND at teardown, making tracer state a declared per-scenario property (a scenario that wants it adds its own SetSetting step). Anomaly sweep ANCHORED 2026-07-26: `grep_anomaly_tokens` was a bare substring search for each Tier-C token over the whole KSP.log, so any line that merely NAMED a token was a hit - S1.7's first flight reddened PARSEK-FAIL(anomaly) on a test diagnostic reporting `over=False` in a log with ZERO `phase=Anomaly` lines. The matcher moved into hlib and now requires the tracers' actual raise shape (`phase=Anomaly ... reason=<token>`, the one shape both `MapRenderTrace.EmitAnomaly` and `LedgerTrace.FormatAnomaly` produce). Same change adds a REPORT-ONLY `unlistedReasons` channel for the ANOMALY_TOKENS drift (see gates). `allowedAnomalies` misplacement promoted from WARN to ERROR the same day, checked over every `[expectations.<sub>]` table, with all 28 pre-existing specs relocated. Post-mission OUTCOME gate added 2026-07-26 (EVA-4 flight 3): the autopilot carve-out that made EVERY post-mission seam step non-gating was over-general - it dropped the ONE channel that observed a dead kerbal (`driver.steps[6].verdict=ERROR`, `allExpectedMet: false`, and `driverValidity: PASS` on the same run). New per-verb `SEAM_VERB_POST_MISSION_ROLE` (TOTAL over the implemented verbs, unit-gated): `outcome` verbs (the four M-C2 EVA verbs) gate via a new `missionOutcome` verifier row -> `PARSEK-FAIL(mission-outcome)`, `recording` verbs stay non-gating exactly as before so the carve-out's own rationale is preserved. Classified PARSEK-FAIL rather than driver-INVALID deliberately: a driver-stage failure preempts and SKIPS every verifier below it and is retryable, which would both discard the evidence and let an intermittent subject death retry into a PASS-with-a-flake-note. R5 companion added 2026-07-27: `InGameTestDecl` now carries `restore_baseline` and `derive_batch_tally` takes an `isolated=` mode, because without them `CommittedBatchTallySourceSyncTests` would have REJECTED a correct isolated pin (deriving `executable = 0` from the ordinary filter). Two adversarial reviews then closed the new axis: the anti-vacuity gate, the single-selector rule and the skipped/executable bounds are each now pinned to still apply when a spec is isolated, `spec_batch_isolated` resolves over ALL RunTests steps rather than the first, and the misplaced-key guard became a recursive sweep |
| M-A6 provisioner | Reproducible pinned KSP instance (kRPC 0.5.4 + MechJeb 2.15.1 + KRPC.MechJeb 0.8.1 + built TestingTools) | SHIPPED (#1303/#1308/#1318) |
| M-B1 mission library | Pure mission state machines + kRPC runner (flights become deterministic, diagnosable instruments) | SHIPPED (#1313); hardened by the flyby campaign |
| M-B2 ledger oracle | Seam-declared action manifests -> expected career totals -> save diff (PARSEK-FAIL(ledger)) | SHIPPED (#1314); stock-award-pattern gate below |
| M-B3 ledger scripts | The L1 scenario six-pack | SHIPPED (#1324); LIVE-PROVEN 2026-07-23 (career fixtures file-constructed headlessly; 7/7 ledger scenarios green, now daily tier). Caveat recorded 2026-07-26: the ORACLE half of those 7 was genuine, but L1-passive-sandbox's and B10's in-game BATCH half executed zero tests under the old category - both re-flown green in `GameActionsHealth` on 2026-07-26 |
| M-C1 seam verbs batch 1 | InvokeRewind, AnswerMergeDialog, TimeJump, KscAction, SaveGame | SHIPPED (#1320/#1325) |
| M-C2 EVA verbs + missions | EvaExit/EvaBoard/PlantFlag -> crew/EVA/flag recording coverage | LIVE-PROVEN 2026-07-24; 18 implemented verbs, 11 reserved; verbs + pure deciders + hlib companions + EVA-1/2/3 specs land, both fixtures forged headlessly, all three scenarios flown green, live-prove list P1-P6 closed |
| M-C2 save-parse verifier (R9 harness half) | Structural save-content expectations: `saveParse` chain row parses the produced save's ParsekScenario surfaces (tree topology, terminal/merge states, branch points by type, supersede rows, tombstones, rewind retirements, RPs/slots) and evaluates `[expectations.rewind]` + `[expectations.recordings.structure]` | SHIPPED-REPORT-ONLY 2026-07-31 (branch `claude/r9-save-parse-verifier-tshhzv`): pure core `harness/lib/saveparse.py` (oracle-precedent sibling; ConfigNode text parser fail-loud on torn files, node shapes pinned against the C# writers, committed-fixture sweep pins all 12 fixtures + 3 quicksaves exactly), run.py `saveParse` row (SKIPPED on killed/driver-invalid, measured facets recorded on every driver-valid run), spec-surface validation in `validate_spec`, `save_structure_mismatch` -> PARSEK-FAIL(save-structure) reachable ONLY via the opt-in `gating = true` key, declared by ZERO committed specs AT THE TIME OF LANDING (`test_no_committed_spec_arms_gating`; S4.1 armed later the same day - see below - and the guard became an allowlist). `rewind` left `RESERVED_EXPECTATION_BLOCKS` (sole-owner rule, the M-B2 `world` precedent); `route`/`loop` stay reserved with zero declarers. **LIVE-PROVEN AND ARMED 2026-07-31** (branch `r9-arm-s41`): `S4.1-rewind-merge` is the first and only committed spec declaring `gating = true`, promoted through the three-run workflow the verifier shipped with - `2026-07-31_1628` report-only reading (PASS; `observed.rewind = {supersedeRows 0, tombstones 0, rewindPoints 0, rewindRetirements 0}`, every reading already inside the declared `max = 0` windows, so arming moved no verdict), `2026-07-31_1635` armed (PASS; `status=PASS gating=true armedBlocks=["rewind"]`), `2026-07-31_1637` NEGATIVE CONTROL (temporary `supersedeRows = { min = 1 }`, reverted) -> `PARSEK-FAIL(save-structure)`, `mismatches=["rewind.supersedeRows 0 < min 1"]`. `test_no_committed_spec_arms_gating` became an ALLOWLIST pinning exactly `{"S4.1-rewind-merge.toml"}` (a second spec arming still reds), and `test_s41_declares_the_rewind_block_armed` additionally asserts the windows are untouched. Fixed in the same pass: `--dry-run`'s `[VERIFY]` enumeration never listed this row, so an armed spec's plan hid its own gate; it now renders armed / report-only / facets-only, pinned by FIVE cells (the declared-but-unarmed state needs a SYNTHETIC spec - no committed spec can reach it while S4.1 is the sole declarer and armed - and that cell is also what makes `armed` and `declared` distinguishable to the suite). STILL REPORT-ONLY for every other spec; CL-2 stage B's windows are measured but unauthored |
| V3 contact sheets | Unconditional per-run artifacts + human-scannable HTML: every attempt (PASS included) copies a BOUNDED KSP.log (whole file to 64 MiB, else head 8 MiB + tail 56 MiB with an explicit truncation marker; `hlib.plan_artifact_log_copy`) plus any run-window `Screenshots/` files (`hlib.select_run_screenshots`; fed by V4's capture verbs when they land) into `results/<runId>_shots/`, then `harness/tools/contact_sheet.py` renders `results/<runId>_contact.html` (captured images beside the run's key log lines - `BATCH_COMPLETE`, every `faithful-parity summary`, every `phase=Anomaly` raise with its +/-3 nearest `probe frame summary` lines - plus the verifier verdict rows) and a newest-first verdict-colored `results/index.html`. A green run finally leaves something a human can scan (design-testing-unified section 6 V3: numbers next to pictures) | SHIPPED 2026-07-31 (branch v3-contact-sheet). VERDICT-NEUTRAL BY CONTRACT: failure-isolated at both call sites (artifact or sheet trouble is a Warn, never a verdict change; BOTH halves pinned by injected-crash smoke cells), the only result-JSON change is the additive `artifacts` key, the verdict is written durably BEFORE the artifact copy (a kill mid-copy cannot cost a result), and the heavy non-PASS collect-logs snapshot is UNCHANGED. Cross-run growth bounded by a retention pass (`hlib.select_shots_dirs_to_prune`: newest 40 `*_shots` dirs / 2 GiB kept, current run always protected; contact pages embed the extracted lines as text so a pruned run stays scannable). Hardened by a 12-finding adversarial review (bounded tail copy under a mid-copy growing log, tmp+rename artifact writes, Infinity/NaN-poisoned result JSON tolerated, call-time-resolvable caps, no sys.path mutation, PID-suffixed index tmp). Pure extraction/formatting unit-tested over synthetic logs + a fake results dir (`lib/test_contact_sheet.py`) and driven end-to-end through the fake-KSP smoke (`AlwaysCollectAndContactSheetSmokeTests`). LIVE-PROVEN 2026-07-31 on the real stock-minimal instance, all three legs: (a) PASS `2026-07-31_1625_H23-tracking-station` - `results/<runId>_shots/KSP.log` byte-identical (sha256) to the instance log, untruncated at 1.59 MB, sheet carrying the REAL `BATCH_COMPLETE v1 total=10 passed=9 failed=0 skipped=1 category=TrackingStation scene=TRACKSTATION` line verbatim plus all 10 verifier rows, green badge, `artifacts` block in the result JSON, `Artifacts`/`Sheet` Info lines and zero `FAILED` warns; (b) KILLED `2026-07-31_1633_ZZ-v3-kill-probe` (throwaway spec, unreachable 25 s wall budget) - the heavy non-PASS `collect-logs` snapshot ran exactly as before AND the shots dir + dark-red KILLED sheet (`subkind budget`, every skip reason spelled out) landed alongside it; (c) retention - 45 backdated dummy `*_shots` dirs + non-`_shots` decoys seeded, one rerun pruned exactly 8 (48 - 40) OLDEST-first with the naming log line, left exactly 40, and touched ZERO non-`*_shots` entries (a 90-day-old decoy `.json`, `_contact.html`, `.txt` and a decoy `_logs` DIRECTORY all survived byte-unchanged, as did both real runs' shots dirs). Windows-specific concerns the cloud session could not prove are closed: all three suites green on Windows (lib 964 / provision 203 / missions 1103), and the tmp+rename `os.replace` artifact writes work on NTFS. ONE defect found and fixed by the live proof: an attempt that ends BEFORE the verifier chain (a refused run-lock - a sibling worktree held the shared instance - writes a readable result JSON whose `verifiers` is `{}`) rendered "no verifier rows (result JSON missing or unreadable)" over a file that was present and readable, sending a reader after a nonexistent file problem; the empty state now distinguishes the two cases and names the real reason (`test_empty_verifiers_is_not_reported_as_a_missing_result_json`). Operational note for future live proofs: the automation instance is SHARED across worktrees and its run-lock is the arbiter - `run.py` refuses cleanly and classifies `INVALID subkind=instance-locked`, so a concurrent sibling session costs a retry, never a false product verdict |
| EVA-4 atmospheric chute | EvaChuteDeploy (the kerbal personal parachute) + mission `eva4_atmo_chute` -> mid-flight atmospheric EVA branch, kerbal-owned atmospheric TrackSections, two-phase chute part events ON the kerbal, kerbal DOWN-alive terminal | LIVE-PROVEN 2026-07-24 (flight 2 full PASS); 19 implemented verbs, 11 reserved; all four first-flight pins closed (count 3, kerbalEVA token, semi-deployed rate measured -> descent budget trimmed 480 -> 240, kerbal lands alive), plus the K=2 window debounce + raw-alive CompleteOk conjunct hardenings. DE-LISTED from live-proven 2026-07-25 (the first full sweep red'd it: the kerbal's canopy cut itself mid-descent and the kerbal died) and FIXED HEADLESSLY 2026-07-26 from the archived log + decompiled KerbalEVA, no new flight: (b) a >3.5 m/s collision fires `On_stumble` from `st_semi_deployed_parachute` into `st_ragdoll`, and leaving that state calls `evaChute.CutParachute()` - closed by a bounded OBSERVED pre-chute standoff on `EvaExit` (`minStandoffMeters`, EVA-4 sets 30, debounced 2 polls, TWO non-fatal bounds - 8 s wall clock AND `standoffFloorAltMeters` 500, the latter load-bearing because the kerbal is unchuted and free-falling for the stage); (a) the MISSION cannot see the kerbal at all (its terminal is the handoff and its process exits before the EVA), so the closure is the harness-side `missionOutcome` gate plus an mlib handoff declaration. RE-PROVEN 2026-07-26 (flight 4, PASS on attempt 1, wall 409 s, all seven verifiers) with the closure verified STRUCTURALLY rather than by the green outcome - a live kerbal proves nothing about a dead one; see the runbook + residual in `todo-and-known-bugs.md` |

## Test cases (all 61 committed scenarios)

LIVE-PROVEN = at least one fully-unattended PASS with every verifier green.
The "Parsek surface verified" column is the reason the case exists.

### Live-proven (39)

| Test case | Tier | Parsek surface verified | Coverage cells |
|---|---|---|---|
| H6-route-rewind-timeline | daily | Route-rewind lifecycle rows, dormant classify + Tick materialize, kept-route reconciliation (Restore(cutoff) reconciliation-bundle path) | D9 reconciliation-bundle; D10 route-x-rewind; D14 sandbox/scene-flight. LIVE-PROVEN 2026-07-24: first live run = FULL PASS attempt 1, all seven verifiers green, in-game batch perCategory=1 - the route-rewind wave's last automated acceptance item. Batch tally pinned WHOLE 2026-07-26: `failed=0 skipped=0` still accepted an EMPTY batch (`total=0 passed=0 ...`), so the pin is now `total=7 passed=7 failed=0 skipped=0 category=RouteRewindTimeline scene=FLIGHT`. That pin is DERIVED, not measured: the 2026-07-24 PASS was against a contract carrying only `failed=0 skipped=0`, so it proves those two; `total=passed=7` follows from the category's 7 scene-agnostic batch-allowed tests plus skipped=0; and `scene=FLIGHT` is inferred from the gloops-airshow fixture's Focusable/FLIGHT LoadGame route (the rule H5 and S1.4 measured on the same fixture), with no H6-specific evidence. Re-pin from the measured line the first time an H6 run archives a log |
| B2-lko-ascent | nightly | Ascent-to-orbit recording, orbital checkpoints, 6-booster parent-anchored debris children model | D1; D3 orbital-checkpoint; D4 atmospheric/exo-propulsive; D14 kerbin |
| B4-reentry-splashdown | nightly | Full-cycle recording (ascent/deorbit/reentry/splashdown intact), exo-ballistic sections, rails-warp recording | D1; D3; D4 +exo-ballistic; D14 kerbin/warp-rails |
| B5-mun-flyby | nightly | Cross-SOI cohesive coast recording (Kerbin->Mun->Kerbin), on-rails checkpoints across warp, warp-reseed seams | D1; D3; D4 +cohesive-cross-body-coast; D14 kerbin/mun/warp-rails. NO-1X CERTIFIED at HEAD config (flight 26: wall 465 s, warp audit exit 0) |
| B6-minmus-flyby | nightly | Same cells on the minmus axis | As B5 with D14 minmus. GATE: 20 km course-correct target predates finding 16d; guarded (arrival gate + impact terminal fail clean); re-target ~150 km only if it reds. **CONFIRMATION RE-FLY PAID 2026-07-25 (wall 359 s, all seven verifiers green):** B6's prior live proof predated the no-1x-coast aim-then-warp (4219832b6) and B6 shares the machine, the correction params AND the 4,000 s `transferBurnTimeoutSeconds` that B12 flight 1 proved cannot cover a Minmus-class correction node's 73,733 s wait - B6 was exposed and had simply not re-flown. It is ALSO the mission most exposed to B12 flight 2's coast warp-thrash (same long Minmus coast). Both shared-machine fixes landed with the B12 forensics and the confirmation flight flew them green on the flyby side, so B6's LIVE-PROVEN mark is honest at HEAD again |
| B7-duna-flyby | nightly | Multi-SOI interplanetary recording (Kerbin->Sun->Duna->Sun), 100,000x warp recording, SOI-count | As B5 with D14 duna/soi-count/warp-high. **GATE CLOSED 2026-07-25 - B7 AT HEAD IS INTERMITTENT.** The gate ("HEAD's 300 km target has not itself flown; the pass flew 50 km") was paid by flying it. Run 1: both attempts `MISSION-ASSERT-FAIL body='Ike' (expected 'Duna' or exit 'Sun')`, wall 747 s / 735 s. Run 2 (the first full sweep): attempt 1 INVALID on the same Ike capture, attempt 2 PASS, terminal PASS at `attempts=2 wallTotal=1564s`. So it is a FLAKY scenario, not a hard red - the approach always transits Ike's shell, but whether Ike is close enough to capture depends on its phase at arrival. The 300 km target was hit correctly (`pe=310089`); the inbound approach transits Ike's orbital shell and Ike captured the craft (`window[19] body=Duna alt=3,687,346` -> `window[20] body=Ike alt=897,085`). NOT an ORBIT-lane regression: `corrBudgetAnchorUt=none` (the correction re-anchor never engaged, so it ran main's bound) and `phaseWarpIssues=1` (the coast latch worked, which is why it reached Duna at all - every archived pre-fix B7 run stopped at `Kerbin to Sun` and never reached the target SOI). Needs a B7 SPEC decision - accept an Ike encounter as a legitimate Duna-system arrival, or aim to clear Ike's shell - deliberately not taken in the ORBIT lane. Forensics in `todo-and-known-bugs.md` |
| S0.5-live-record-discard | daily | Live record start/stop marker pairing + DiscardTree returns the store to zero (caught the orphan-sidecar leak) | D1 discard-rollback; D5 single-node; D14 |
| S0.6-live-record-commit | daily | Commit on top of the injected corpus without corpus loss (the save-hollowing guard class) | D5; D14; D16 sidecar-prec |
| S0.7-exit-auto-commit | daily | R12/A2 `ExitToSpaceCenter`: the live FLIGHT -> SPACECENTER transition no seam verb could produce. Gates the wedge guard on live state (the whole `autoMerge=true hasActiveTree=true hasPendingTree=false switchSegmentSession=false activeTreeVariant=None pendingTreeVariant=None` input set), the stock exit path with its mandatory pre-exit persist (`driving scene-exit dest=SPACECENTER persisted=true`), the FLIGHT-side finalize/stash pipeline the transition drives (`CommitTreeSceneExit: stashed pending tree` + `Stashed pending tree '...' (1 recordings, state=Finalized)`), and the deferred terminal that only fires once the KSC is settled with a game loaded | D14 kerbin/sandbox/scene-flight/scene-ksc. D1 deliberately EMPTY. LIVE-PROVEN 2026-07-30, run `2026-07-30_1538_S0.7-exit-auto-commit`, PASS attempt 1, 47 s, every verifier PASS/SKIPPED. SCOPE, stated so it is not over-read: this proves the TRANSITION, NOT the pending-tree AUTO-COMMIT on arrival - see the CL-1-pod-impact row and the roadmap's R12 block for the extension that can. Two earlier attempts on the same day RED'd and are why the scope is what it is: a `StartRecording -> ExitToSpaceCenter` pair records exactly ONE point (a seam round trip is ~130 ms against a 200 ms minimum sample interval) and a one-point recording has `maxDist = 0` by construction, so `ParsekScenario.OnLoad` idle-discards the Finalized pending tree before the autoMerge commit branch - correct product behaviour for a 0.13-second recording. The second attempt tried EvaExit's `settleSeconds` (the harness's only spec-authorable dwell) and recorded ZERO points across 10 s of orbital EVA, which is a REAL and separate defect - see gate 12, PRODUCT DEFECT FIXED 2026-08-01 (branch `fix-orbital-eva-kepler`). RE-FLOWN 2026-08-01 on the fixed build, run `2026-08-01_1630_S0.7-exit-auto-commit`, PASS attempt 1, 48 s, every verifier PASS/SKIPPED and zero exceptions - so this spec carries no regression from the fix. Read that narrowly: the COMMITTED step list here is `StartRecording -> ExitToSpaceCenter` and never goes on EVA, so this run does not exercise the dwell at all. The dwell primitive itself is proven by `EVA-2-orbital-board` run `2026-08-01_1626` (10.068 s of orbital EVA -> `points=5 maxDist=1888m`, against `points=1 maxDist=0m` on 2026-07-30), which is the same `settleSeconds` mechanism this spec wanted |
| S0.8-switch-click-segment | daily | R12/B `SimulateStockSwitchClick`: the ONLY automated cell that drives the PATCHED stock switch handler rather than around it (kRPC's `active_vessel` setter calls `FlightGlobals.SetActiveVessel` directly, arms no `StockActionIntentMarker`, and would go green while `MapFocusObjectOnSelectPatch` rots). Gates the whole classification input set (`dialogDecision=NoPriorSession` with `targetIsUnloaded=false targetIsSeparateCommittedVessel=false`, i.e. Cases A/B/C evaluated and none applied), the marker built with the patch's own field set (`action=MapSwitchTo ttl=2s route=plain-arm-and-switch`), and the CONSUME site opening a real segment (`[SwitchSegment] created intentId=... focusedName='Kerbal X Probe' reason=MapSwitchTo`, `route=standalone`, `[SwitchIntent] cleared ... reason=consumed-into-segment`) plus the OBSERVED `activeVesselPid=2614652043 switched=true` | D1 switch-segment (with H12, which drives the arming DECISION headlessly while this drives the live click); D14 kerbin/sandbox/scene-flight. LIVE-PROVEN 2026-07-30, run `2026-07-30_1543_S0.8-switch-click-segment`, PASS attempt 1, 45 s. The fixture choice is the whole cell: `eva2-lko-crewed` is the only committed save with TWO LOADED vessels ~16.4 m apart and BOTH ORBITING, and orbiting is load-bearing - the consume REFUSES surface targets by design (`Refused_OnSurfaceTarget`) while the verb still reports `OK switched=true`, so every pad-launched fixture would read green and certify nothing. First attempt RED'd on REC-003 (quitting from FLIGHT with the recorder live and no `StopRecording`), which is spec authoring, not product. NOT claimed: D5 `chain-continuation-switch` - the measured route is `parentRecId=<standalone> branchPointId=<none>`, so no chain link exists |
| S1.4-injected-playback | daily | 272-tree corpus injection, load, ghost map presence + polyline render with no anomalies | D6 basic-playback/ghost-map-presence/non-orbital-polyline; D16 sidecar-prec/sidecar-pcrf. Batch contract hardened 2026-07-26 (was `failed=0` only) and its PENDING-OPERATOR CLOSED the same day by a live flight: the loose `passed=[1-9][0-9]* skipped=[0-9]+` placeholder is replaced by `total=42 passed=40 failed=0 skipped=2 category=GhostPlayback scene=FLIGHT`. Evidence standing, PARTLY MEASURED: the run executed the LOOSE pin (the exact one was committed after it), so it proves total=42 / failed=0 / category / scene and passed>=1; the 40/2 split was read off that run's live log, which was not archived (`collectLogs: {ran: false}` on a PASS) and is not re-derivable from any committed artifact. The 2 skips are 1 structural (`AllowBatchExecution=false`, attribute-exact) + 1 fixture-determined self-skip; the other 10 conditional guards did not fire on this corpus. The pin STAYS - a wrong split reds loud with the numbers to re-derive from |
| H5-invariants-corpus | daily | The full synthetic corpus (306 recordings / 276 trees) loads intact and holds every recording invariant in-game | D14 sandbox/scene-flight; D16 sidecar-prec/schema-gate. Batch tally pinned WHOLE 2026-07-26 from the MEASURED 2026-07-19 line: `total=2 passed=2 failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT` |
| B10-career-passive-safety | daily | Fresh career + stock actions only = ZERO economy drift (the BUG-A science/funds corruption class), now with a batch that genuinely executes | D8 funds/science/reputation/recalc-from-ut0; D14 career/cold-load-ut0/scene-ksc. RE-PROVEN 2026-07-26 in the corrected `GameActionsHealth` category: `total=4 passed=4 failed=0 skipped=0 ... scene=SPACECENTER`, MEASURED. Its earlier "green" runs executed ZERO tests (see the de-listing note in todo-and-known-bugs.md); this is the vacuity fix proven live. D16 schema-gate stays dropped - it was claimed over a save with zero recordings |
| L1-passive-sandbox | daily | Sandbox cold load moves nothing (recalc/orchestrator/patcher inert); the one scene- and mode-independent suppression-flag assertion actually runs | D8 recalc-engine/orchestrator/ksp-state-patcher; D14 sandbox/scene-ksc. RE-PROVEN 2026-07-26 in the corrected `GameActionsHealth` category (its first flight there): `total=4 passed=1 failed=0 skipped=3 ... scene=SPACECENTER`, MEASURED, and the 3 skips ARE the subject (SANDBOX has no pools). Ledger oracle green, hardDivergences=0 |
| B11-mun-orbit | nightly | COMMIT-IN-FOREIGN-SOI (the new D1 cell): a recording that ENDS parked in another body's SOI and is COMMITTED there - the commit path, the terminal classification, and the background-recording handoff for a tree whose terminal state is "in orbit around the Mun". Every other lunar/interplanetary case (B5/B6/B7) flies THROUGH an SOI and comes back or continues, so none of them reaches this surface. Machine: the LIVE-PROVEN `mlib.b5_decide` with the new `captureEnabled` param - ascent, transfer, TLI, corrections, warp policy all byte-identical to the 26 B5 flights; NEW is the four-phase tail PLAN-CAPTURE (MechJeb circularize-at-periapsis) -> CAPTURE-BURN (NodeExecutor, autowarp EXPLICIT; done evidence is a BOUND orbit, since a hyperbolic approach reads a NEGATIVE apoapsis) -> PARK (throttle cut, nodes cleared, SAS+RCS held, rails dropped to 1x, 180 game-s held dwell) -> ORBIT-COMMIT (the B-DOCK route-1 mid-mission seam CommitTree) -> ORBIT-COMMITTED. Leaving the target SOI anywhere in that tail is an ASSERT-FAIL, so B5's free-return cannot green this mission | D1 auto-record-launch + commit-in-foreign-soi (the NEW cell); D3 orbital-checkpoint; D4 atmospheric / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/mun/warp-rails (D5 bg-recording was CLAIMED and then REMOVED 2026-07-25: `CommitTreeFlight` nulls both `backgroundRecorder` and `PhysicsFramePatch.BackgroundRecorderInstance` before returning, no assertion or token covers a handoff, and `settle_frames = 0` ends the mission on the commit frame - BDOCK-1 is the honest claimant of that cell). LIVE-PROVEN 2026-07-25 (flight 2 FULL PASS attempt 1, all seven verifiers green, wall 1,268 s, analyzer red=0): capture apoapsis flipped -1,560,099 -> +138,789 m at eccentricity 0.000127, all six assertions met, PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED. Its coast issued the native warp ONCE with ZERO cancels, which is why the Mun lane never showed B12 flight 2's coast warp-thrash; the shared fix for that landed after this pass and does not alter any frame this flight took (a coast that never cancels a valid warp is unaffected). CONFIRMATION RE-FLY PAID (flight 3, 2026-07-25): B12 flight 3's periapsis-bound fix CHANGED this mission's flown profile - TARGET-FLYBY now warps to periapsis_ut - 900 instead of riding the rails flyby stair, and the COAST handoff stops the inherited warp - so a LIVE-PROVEN mission owed one flight on the changed profile. It flew FULL PASS again: wall 1,269 s, all six assertions met, capture eccentricity 0.000127 (flight 2 read the same number, so the profile change did not move the capture quality), and TARGET-FLYBY collapsed from 8,213 game seconds to 27 game seconds on 2 warp commands. The lane's re-fly debt is now clear on both axes. FLIGHT 4 (2026-07-25) FULL PASS, wall 1,271 s (`results/2026-07-25_0400_B11-mun-orbit.json`): the COUNT-PIN run - the first B11 pass carrying `verifiers.expectations.observed.recordings.count`, which read 8 and pinned the window to {8, 8}. **FLIGHT 5 (2026-07-25) FULL PASS attempt 1, wall 1,270.195 s - the POST-REVIEW re-fly, and the first flight on which this mission's headline claim is actually VERIFIED.** Owed because the review pass added a `time_to_periapsis > 0` conjunct to the capture arming gate (a frame-level change to a path the green flights took) and because the new commit-terminal token needed its first live proof. Both landed: the token reads `terminalState=Orbiting terminalOrbitBody=Mun` for the parked craft, and the 8-recording topology is now legible instead of merely counted - 1 `Orbiting Mun` (the committed craft), 6 `Destroyed` (the radial boosters), 1 `Orbiting Kerbin` (the flameout-staged ascent core), matching the count-pin derivation exactly. GATED since 2026-07-26 (review round 2): a regression that drops one recording while adding a spurious one still reads 8, so the count alone cannot catch it - and until this round only the Mun row was actually required. Both specs now also require `terminalState=Orbiting terminalOrbitBody=Kerbin` and `terminalState=Destroyed terminalOrbitBody=(null)`, so each of the three topology CLASSES is asserted (the count still owns the total). Measured on the same fixture and launcher in `logs/2026-07-25_1216_B7-duna-flyby/KSP.log`: 8 terminal lines, 1 `Orbiting Kerbin` and 6 `Destroyed (null)`. Flight 1 (2026-07-24) flaked at CAPTURE-BURN on our own no-start watchdog colliding with MechJeb's 600 s pre-ignition WARPALIGN hold; fixed with an OBSERVED NodeExecutor.Enabled channel + a node-clock no-start classifier (forensics in todo-and-known-bugs.md) |
| B12-minmus-orbit | nightly | Same cells on the minmus axis (a thin alias over the same capture-enabled machine, exactly as B6 is to B5) | D1 auto-record-launch + commit-in-foreign-soi; D3 orbital-checkpoint; D4 atmospheric / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/minmus/warp-rails (D5 bg-recording removed 2026-07-25 for the same reason as B11 - nothing gates it). LIVE-PROVEN 2026-07-25 (flight 4 FULL PASS, all six mission assertions met, wall 580 s; flight 5 repeated it at wall 580 s and was the COUNT-PIN run - the first B12 pass carrying `verifiers.expectations.observed.recordings.count`, which read 8 and pinned the window to {8, 8}, `results/2026-07-25_0349_B12-minmus-orbit.json`; **FLIGHT 6 FULL PASS attempt 1, wall 580.826 s - the POST-REVIEW re-fly that first VERIFIES the lane's headline claim**: the new commit-terminal token reads `terminalState=Orbiting terminalOrbitBody=Minmus` for the parked craft, and the 8-recording topology is now legible instead of merely counted - 1 `Orbiting Minmus`, 6 `Destroyed` boosters, 1 `Orbiting Kerbin` core, matching the count-pin derivation exactly. GATED since 2026-07-26 (review round 2): the spec required only the target-body row, so a regression dropping the ascent-core recording and adding a spurious booster would still read 8 and still pass - both specs now also require `terminalState=Orbiting terminalOrbitBody=Kerbin` and `terminalState=Destroyed terminalOrbitBody=(null)`, measured on the same fixture and launcher in `logs/2026-07-25_1216_B7-duna-flyby/KSP.log` (1 and 6 lines). Owed because the review added a `time_to_periapsis > 0` arming conjunct that changes a frame the green flights took. NOTE the flight-6 lesson: its FIRST attempt red'd PARSEK-FAIL on the new token and looked exactly like "the commit records the wrong terminal body", but the flight had run the PREVIOUS DLL - harness flights use the provisioned `automation/stock-minimal` instance, and `dotnet build` only deploys to the dev instance. Run `provision.py --profile stock-minimal` after any C# change; see the note in `.claude/CLAUDE.md`): capture eccentricity 0.00026, PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED, and the two shared-machine warp fixes this mission's own forensics produced both held - COAST-TO-TARGET flew 194,543 game seconds in 26 wall seconds (ratio 7,535) on 3 warp commands, and the periapsis-bounded TARGET-FLYBY armed the capture on the orbit's clock instead of blowing through it. At 580 s wall it is the CHEAPEST of the two orbit cases (B11 costs 1,269 s), which makes the Minmus axis the better default for a fast regression check of the shared capture machine. PRIOR FLIGHTS (the forensics that produced three of the lane's four findings): FLIGHT 3 FLOWN 2026-07-25: the coast fix WORKED (COAST-TO-TARGET 26 wall / 194,704 game = ratio 7,543 on 3 warp commands, down from never-finishing) and the run reached a capture burn. THIRD SHARED-machine defect found, named at a glance by the new warpUtilisation block: TARGET-FLYBY read 2 wall / 8,213 game (ratio 5,341, 2 commands) and blew straight through periapsis - entered at ut 268,934.5 / alt 1,902 km descending at -236 m/s, PLAN-CAPTURE at ut 277,147.5 / alt 41,609 m CLIMBING at +92 m/s. Two compounding causes: the COAST -> TARGET-FLYBY handoff emitted NO warp cleanup so the craft crossed the SOI still running the coast's RAILSx10000 warp (the first flyby poll alone advanced 3,907 game seconds), and capture mode fell through to a rails flyby stair floored at flybyWarpFactor whose distance term knows nothing about the periapsis CLOCK. The late burn produced a bound but wildly eccentric 325 x 5.3 km orbit grazing Minmus, CORRECTLY rejected by the capture window as an under-burn. FIXED: `mlib.capture_flyby_warp_target` - the only legitimate warp target inside the target SOI is `periapsis_ut - CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS` (900, covering our arming + plan, MechJeb's halfBurnTime ignition lead and its 600 s pre-ignition hold), read from `Orbit.TimeToPeriapsis` via the opt-in `read_periapsis`; past the bound or with the clock unreadable the machine does NOT warp (fail closed, 1x); and the handoff now stops the inherited coast warp. FLIGHT 2: the correction fix WORKED (both rounds cleared, `rounds=2`, round 1's 73,720 game-second aim-warp ran as ONE continuous warp from ut 475.3 to 74,195.7 with no cancel), then INVALID `mission-budget-expired` inside COAST-TO-TARGET at ut 225,990 with 41,655 game seconds still to go. SECOND SHARED-machine defect found: the coast derives its native warp target from `time_to_soi` EVERY poll, and KSP cannot read the patched-conic SOI time while re-patching under a warp ramp - measured over flight 2's COAST frames, tts was finite on 2,451 of 2,451 unwarped frames and NaN on 1,154 of 1,161 warping ones - so the machine cancelled its own warp on the blind read and re-armed on the next unwarped poll: 3,603 `warp_to_ut` issues, 3,602 `cancel_warp`, a rails rate that never escaped ~2.7x, ~40 game-s per wall-s. METASTABLE, which is why it had never been seen: B11 flight 2's Mun coast issued the warp ONCE (0 cancels, 30/30 warping frames finite) and locked in at RAILSx1000. FIXED: `mlib.coast_native_warp_hold` - the target is an ABSOLUTE UT, so a blind read UNDER WARP HOLDS the armed command; only a blind read with the game NOT warping is evidence the encounter is gone. Plus a NAMED `coast-warp-thrash` fast-fail past `MAX_PHASE_WARP_ISSUES` (500; a healthy coast issues 1) and a per-phase `warpUtilisation` block in the mission result whose `gameSecondsPerWallSecond` names this class in one line. Budget RE-DERIVED bottom-up from flight 2's measured spans (~2,280 nominal / ~3,100 worst) and deliberately NOT raised - 4200/4700 stand. FLIGHT 1 (2026-07-25): MISSION-FLAKE at CORRECTION-BURN (wall 286 s), UPSTREAM of the capture tail and in the SHARED B5/B6 machine, not in anything B12-specific. ROOT CAUSE: the no-1x-coast PR (4219832b6) turned the DIY correction burner into AIM-THEN-WARP (aim, then natively warp to `node_ut - nodeArrivalMarginSeconds`, then throttle) but left the phase bounded by `transferBurnTimeoutSeconds` - a GAME-time budget that the warp itself spends. Measured: B11/Mun needs 2,994 s of its 4,000 s budget for that wait (75%, passes), B12/Minmus needs 73,733 s (entry ut 475.3, MechJeb node at ut 74,208.3) and can NEVER pass. FIXED in the shared machine: `mlib.correction_budget_expired` suppresses the budget while an aim-warp is in flight and re-anchors it at the warp ARRIVAL (the same seam that already re-anchors the no-start clock - and a game-time bound cannot bound a STALLED warp anyway, which advances no game time; the runner's warp-stall watchdog + the WALL budget own that), and `mlib.classify_correction_timeout` NAMES the expiry (`correction-burner-no-start` / `correction-burn-incomplete`) so it can never again ride the generic timeout. Every correction round give-up now also carries a `corrGiveup` reason on the machine-diff line. B11 flight 2 passed on this same machine, so no budget number needed changing. Also inherits B11 flight 1's CAPTURE-BURN fix unchanged and `read_node_executor` is on; wall budgets 4200/4700 (raised from 3600/4100 for MechJeb's MEASURED ~600 s pre-ignition hold). Same PROVISIONAL pins; the Minmus-specific one is `captureBurnTimeoutSeconds` 200000 (its SOI edge is ~2,187 km up but arrival speeds are ~5x lower than the Mun's, so the executor's SOI-entry -> periapsis autowarp coast is ~10-20 game hours, not 1-3) |
| B13-mun-landing | nightly | LANDED-ON-ANOTHER-BODY (the new D1 `commit-landed-foreign-body` cell): a recording that ENDS on Mun soil and is COMMITTED there - the `Landed` terminal classification for a foreign-body tree, SURFACE-class TrackSections OFF Kerbin (the environment classifier's AIRLESS `Approach -> Surface*` path, which cannot occur where an atmosphere classifies first - hence the previously unclaimed D4 `surface-stationary`, the class THIS flight measured), the landing-leg part events, and the landed-vessel ghost / playback surface. B11/B12 end in ORBIT around a foreign body and B1/B4 land on KERBIN, so nothing else in the suite reaches this end state. Machine: the LIVE-PROVEN `mlib.b5_decide` with the new `landingEnabled` param on top of `captureEnabled` - PRELAUNCH through PARK byte-identical to the five B11 flights; NEW is the tail DESCENT (MechJeb `LandUntargeted`; warp-PASSIVE because MechJeb's landing states own the warp through the shared `Core.Node.Autowarp` flag the runner sets explicitly; `DeployGears` true, `DeployChutes` FALSE because the Mun is airless, `RcsAdjustment` false because the stage has no thruster blocks) -> LANDED-SETTLE (throttle cut, autopilot released, SAS held, rails 1x, a held settled dwell gated on target body + landed situation + BOTH speed components) -> SURFACE-COMMIT (the same route-1 mid-mission seam CommitTree) -> SURFACE-COMMITTED. FOUR named DESCENT give-ups on top of the budget: `landing-autopilot-not-enabled` (COMMANDED-vs-OBSERVED off `LandingAutopilot.Enabled`; a landed frame exits DESCENT BEFORE the supervisor runs, because MechJeb disables its own module on the landed frame and a perfect landing must not read as a dead autopilot), `landing-no-progress` (+ a separate `altitude-unreadable` name), `landing-touchdown-timeout`, `landing-vessel-lost` (a crash reads as neither a timeout nor a success) | LIVE-PROVEN 2026-07-25 FOR THE HAPPY PATH: FULL PASS attempt 1, wall 2,747.9 s, all verifiers green. `terminalState=Landed terminalOrbitBody=Mun`, airless `Approach -> SurfaceStationary`, measured count 8 (window PINNED). Most expensive scenario in the suite at 2,825 s harness wall. NOT live-proven: none of the four DESCENT give-ups nor `landed-never-stable` fired on either flight - they carry unit + fly-loop coverage only (see the COVERAGE HONESTY bullet under roadmap item 3) |
| B14-minmus-landing | nightly | Same cells on the minmus axis (a thin alias over the same landing-enabled machine, exactly as B12 is to B11), except the D4 surface class: B14 claims the previously unclaimed `surface-mobile`, which is what its own flight measured, and B13 keeps `surface-stationary`. NOT redundant for a LANDING: Minmus's ~0.05 g against the Mun's ~0.17 g makes MechJeb's descent-speed policy fly a long slow low-thrust settle instead of a short suicide burn, and the flats make an untargeted landing far more likely to end on level ground | LIVE-PROVEN 2026-07-25 FOR THE HAPPY PATH: FULL PASS attempt 1, wall 2,083.9 s, all verifiers green. `terminalState=Landed terminalOrbitBody=Minmus`, airless `Approach -> SurfaceMobile`, touchdown -0.25 m/s vertical / 0.06 m/s horizontal, measured count 8 (window PINNED). The CHEAPER landing axis, so the better default regression check of the shared landing tail. Same NOT-live-proven list as B13: the give-up ladder never fired here either |
| B15-eve-flyby | nightly | SECOND interplanetary destination, and the first INWARD transfer: multi-SOI recording Kerbin->Sun->Eve->Sun on the SAME machine and the SAME five params B7 flies. STATED SKEPTICALLY, as the spec does - this largely RE-EXERCISES B7's cross-SOI surface at a different body (a D14 MULTIPLIER, not a new mechanism); what it adds is a stable interplanetary regression subject, since B7 itself is FLAKY (Ike grabs its 300 km approach on roughly half of sweeps), plus the cheap prerequisite for B16. THE THING IT DELIBERATELY DOES NOT CLAIM: Eve's 90 km atmosphere - the one genuinely new Parsek surface Eve could reach - is not touched (the pass is aimed at 1,000 km), would not record if it were (the recorders early-return on `isOnRails` / `packed`), and is NOT ASSERTABLE TODAY at all because no emitted log line pairs an environment class with a body name; a follow-up aerobraking variant needs `body=` added to the `TrackSection started:` format string first, mirroring the B13 `terminalOrbitBody` fix | D1 auto-record-launch; D3 orbital-checkpoint; D4 atmospheric (the KERBIN ascent's cell, NOT an Eve one) / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/eve/soi-count/warp-rails/warp-high. NO NEW REGISTRY VALUE - D14 already carries `eve`, so this lane triggers no growth-rule obligation. FLOWN. The "no new mlib code" claim was REFUTED by flights 1-3 and the lane now carries ONE argued param key plus three param-gated machine changes (`EveLaneIsAParameterChangeTests` rewritten, not deleted, to pin the single-key delta). (1) THE INWARD TRANSFER - ANSWERED, and the answer was not the one the spec anticipated. (A 2026-08-01 tag audit briefly ADDED `pending-operator` here on the strength of the spec's PRE-FLIGHT risk note - "no headless test can" verify the inner target - and REVERTED it the same day once this row was read: flight 7 answered that question. The spec now records the trap, and B15 is classified in `REVIEWED_UNTAGGED` as discharged.) MechJeb plans an inner window fine; what it gets wrong is the EJECTION SIZE, because `DeltaVAndTimeForInterplanetaryTransferEjection` computes the post-burn speed at the park's SEMI-MAJOR AXIS and applies it at whatever radius its ejection geometry picks. On a circular park those coincide; on the 0.085-eccentric park MechJeb's own sloppy high-altitude circularization leaves, they do not, and the SAME planner priced the SAME ejection at 652.843 m/s from that park against 775.873 m/s from flight 5's round one, a MEASURED 123.0 m/s shortfall - the heliocentric leg's perihelion sat 2.46e9 m above Eve's aphelion, so no encounter was ever geometrically possible and `nextBody` never once read Eve. mlib still has no direction anywhere; the defect was a THRESHOLD and a MISSING ACTION, not a sign. Fixed by `parkTrimEccMax` (circularize-at-apoapsis before planning) plus a plan-time `reachesTargetOrbit=` verdict. (2) GILLY, and the answer is that it is NOT Ike: 126 km SOI vs 1,050 km (~69x smaller cross-section), 17-57% of the SOI radius vs 6.7%, 12 deg inclination vs 0.2 deg. Residual risk non-zero; a Gilly capture is a NAMED ASSERT-FAIL, deliberately not whitelisted - and it did NOT occur on the green run. GREEN on flight 7 (2026-07-26): full scenario PASS on attempt 1, every verifier PASS/SKIPPED, analyzer red=0, 86 `nextBody=Eve` reads, flyby periapsis 22,032,532 m against the 100,000 m floor, exit to Sun. Both correction rounds flew (378.32 m/s re-aim then a 3.35 m/s trim), which needed `maxCorrectionDvMps` 200 -> 450 - the 200 was a MOON-transfer calibration, and an interplanetary arrival-PHASE fix is legitimately dearer (measured affordable: ~1,944 m/s remained at Sun-SOI entry). PINS CLOSED: recordings count {8, 8}, mission wall 1,236 s, scenario wall 1,286 s, ejection-window wait 11,827,993 game s (0.80 synodic) |
| M1-mission-loop-unit | daily | The mission-loop PLAN against LIVE stock ephemerides: cross-tree partner-journey link discovery + include/normalize mutation + the REAL MissionLoopUnitBuilder shared span clock landing member windows on the recorded dock/undock UTs; joint landing+station arrival hold (with the landing-only byte-identical-off control); the resonant Jool inner-three configuration hold; incommensurate Bop failing CLOSED to faithful | D11 partner-journey/land-dock-dual-constraint/arrival-hold/multi-moon-config-hold/fail-closed-to-faithful; D14 sandbox/scene-ksc. NEW 2026-07-26 - the first spec to claim ANY D11 cell (the dimension was 0/18). LIVE-PROVEN 2026-07-26, and it EARNED ITS KEEP ON FLIGHT 1: batch tally exact (`total=12 passed=5 failed=0 skipped=7`), but its `recordings.count = {0,0}` pin red'd on a real Parsek-side defect - in-game tests driving the real `RecordingStore.CommitTree` left orphan sidecars the memory-only tree teardown could not reach. Fixed via the shared `InGameTestSidecarReaper`; re-flown FULL PASS with the pin untouched. Gates the PLAN, not the playback: no ghost, icon, cycle boundary or elapsed period is observed |
| M2-periodicity-solver | daily | The periodicity SOLVER against LIVE stock ephemerides: the re-aim feasibility scan over a pinned synodic period, UvLambert transfer synthesis that must actually encounter the target, the window schedule, the eccentric/inclined stage-A un-projection + stage-B tof band (Moho / Eeloo), heliocentric-parking r1==park-end, and deterministic clean declines at the band edge | D11 reaim-lambert/eccentric-inclined-targets/heliocentric-parking-departure/fail-closed-to-faithful; D14 sandbox/scene-ksc. NEW 2026-07-26, M1's sibling (separate spec because run.py's `_driven_category` reads only the FIRST RunTests category and a per-category line with no aggregate is a defined fault - one batch per spec). LIVE-PROVEN 2026-07-26, full PASS attempt 1; tally MEASURED and deliberately NOT total=passed: `total=11 passed=7 failed=0 skipped=4` (1 FLIGHT-scene member + 3 `AllowBatchExecution=false` diagnostics). Gates the SOLVER, not the playback |
| S1.6-render-parity | daily | The FIRST cell that gates PLAYBACK rather than recording: drives the in-game `GhostMap` batch so the production recorded-vs-rendered parity oracle (`RenderParityOracle` + `MapRenderProbe.ComputeFaithfulOrbitParity` / `ComputeSynthesizedConicParity`) actually runs unattended, tracer pinned on, `allowedAnomalies = []`. Anti-vacuity is MANDATORY here: a pinned whole tally plus two `[TestRunner]` measurement lines emitted only after a real diff ran on live ghost geometry | D6 recorded-vs-rendered-parity (new registry value); D14 sandbox/scene-flight. LIVE-PROVEN 2026-07-26: first flight = PASS, every verifier green. `total=25 passed=14 failed=0 skipped=11 category=GhostMap scene=FLIGHT`; the 11 skips are 9 TRACKSTATION scene-eligibility + 2 documented loop-icon self-skips. Negative control measured 1049421 m against a 1927 m tolerance (~545x), so the zero-drift assertion provably can still fail. FLOWN TWICE: flight 1 (run `2026-07-26_0950`) MEASURED the tally while the spec still carried the loose `passed=[1-9][0-9]*` conjunct, so the exact line was a transcription; flight 2 (run `2026-07-26_1207`, PASS, expectations mismatches=0, anomalySweep hits=[] unlistedReasons=[], log archived at `logs/2026-07-26_1207_S1.6-render-parity/KSP.log`) ran the spec AS COMMITTED and is what actually EVALUATED the exact pin, both measurement lines and both forbidden patterns. Caveat on flight 2: the instance's deployed DLL was a sibling worktree's build whose GhostMap surface is identical to main's (25 attributes, 16 FLIGHT + 9 TRACKSTATION in both), so the pin is not yet evaluated against a main-built DLL. Does NOT cover Recording.Points / TrackSection frames, the flight-scene ghost mesh, anything across time (one frame per assertion), or re-aim solve correctness |
| S1.7-maprender-parity | daily | S1.6's follow-up over the STRONGER category: drives the in-game `MapRender` batch (22 tests, all Scene = FLIGHT) - the parity baselines with the typed PhaseChain spine driving, multi-body concurrent ghosts, the re-aimed-loop lens distinction, and the descent / re-stitch / dock-undock / overlap / parent-anchored / BG-on-rails spine cells. Anti-vacuity accounts for the SINK TRAP: four MapRender test files install `ParsekLog.TestSinkForTesting`, which diverts rather than tees, so the obvious candidate (the three-oracle flag-on baselines) can never reach KSP.log. Pins the two arms that do: both `MultiBodyConcurrent` lines (`sampled=True skip=(none) hasMeas=True over=False`, the Mun arm doubling as the cross-body-leak proof) and the re-aimed-loop line | D6 recorded-vs-rendered-parity; D14 sandbox/scene-flight - deliberately NO new registry value (depth on an axis S1.6 opened, not breadth). LIVE-PROVEN 2026-07-26: `total=22 passed=21 failed=0 skipped=1 category=MapRender scene=FLIGHT`, the single skip being the `AllowBatchExecution = false` high-warp canary; zero scene-eligibility attrition. Negative control 1319093 m against 2701 m (~488x). Its first flight also EXPOSED the anomaly-sweep false positive (below) |
| CL-1-pod-impact | nightly (PROMOTED from operator 2026-07-28) | THE CREW-LOSS ATOM, and the first scenario in the suite whose subject DIES. A crewed pod launches, deploys no chute and hits the ground; the success terminal is the kerbal's death, read from the kerbal's OWN roster status (`SpaceCenter.GetKerbal(name).RosterStatus`, verified at the PINNED kRPC v0.5.4), never a commanded latch. Gates the RECORDER side end to end: `Recording started`, `Destroy-reason override ... reason=destroy_event`, `Active vessel destroyed during recording`, `Active vessel destroyed in tree mode`, `CrewStatusChanged '<name>' Assigned ... Dead`, `ShowPostDestructionTreeMergeDialog: finalized tree`, `pending tree stashed`, `Recording stopped`, plus `recordings.count` pinned at 1 and OBSERVED 1 | D1 auto-record-launch; D12 crew-death-in-flight (NEW value); D14 kerbin/career/scene-flight. LIVE-PROVEN 2026-07-28, flight 2 = FULL PASS attempt 1, 166 s, all seven verifiers PASS/SKIPPED. FLIGHT 1 RED and earned its keep on the first run: the flight was perfect (262 points, full destruction path, Jeb `state = Dead` on disk) but `expectations` failed 2 mismatches from ONE FIXTURE root cause - `career-pad-craft` inherited `fresh-career`'s deliberate absence of a `SCENARIO{name=ParsekScenario}` node, and the seam's FLIGHT focus route does not run `UpdateScenarioModules` the way the SPACECENTER route does (known-gate 6), so the ScenarioModule was never added, ZERO `[Scenario]` lines appear in the collected log, `OnSave` never ran, and the whole flight was recorded in memory and thrown away. The builder now splices the donor's inert node and `verify()` refuses a fixture without one. SECOND FINDING, why there is no ledger half: a destroyed active recorded vessel stashes its tree as PENDING and nulls `activeTree`, so the seam's `CommitTree` fails its `HasActiveTree` guard (`logs/2026-07-20_1829_B1-pad-hop/KSP.log:11310` `committree no-active-tree`, `:11325` `saving 0 committed tree(s)`); the pending tree auto-commits only OUTSIDE Flight and no seam verb produced that transition. **LEDGER HALF NOW BUILT AND LIVE-PROVEN 2026-07-30 by `CL-2-pod-impact-ledger` (its own row below), NOT by editing this spec.** R12/A2 shipped `ExitToSpaceCenter`, which drives exactly that transition; CL-2 is CL-1's step list plus `SetSetting autoMerge=true`, that exit, and an `[expectations.ledger]` block, and it flew green on both its runs. CL-1 stays the ATOM and stays UNCHANGED - `test_cl1_crew_loss.py::test_the_spec_drives_no_commit_and_declares_no_ledger_block` is what forbids the naive edit, and it still passes. Note what S0.7 could NOT do and CL-1 can: S0.7's tree is idle-on-pad (a seam-only recording is one point, so `maxDist = 0`) and is correctly discarded before the autoMerge commit branch, whereas CL-1 FLIES 262 points across 12 km - so CL-1 is not merely a nicer consumer of the verb, it is the first consumer that can reach the auto-commit at all. Both runs MEASURED the terms that extension needs, identically: `Added -9.999828 (-10) reputation: 'VesselLoss'` on the death, milestones RecordsSpeed 4800 / FirstLaunch 800 / RecordsAltitude 4800 all rep=0.0, produced pools funds 529600 sci 100 rep -7.99982834. The key is `VesselLoss`, NOT the `CrewKilled` that `PostWalkActionReconciler` used to map - settled by measurement, and the product map is now corrected to `VesselLoss` and pinned by `Source/Parsek.Tests/PostWalkActionReconcilerTests.cs` |
| CL-2-pod-impact-ledger | nightly (PROMOTED from operator 2026-07-30 after two green flights; 172 s / 167 s wall, as cheap as the atom it extends) | **CAPTURE CROSS-CHECK ARMED 2026-07-31 - the FIRST committed spec in the suite to gate on the ledger capture, and the only one that declares a `utWindow`.** Its three awards are now HARD: an unexpected stock award here is a `PARSEK-FAIL(ledger)`, not a report row. Walked as known-gate 3's checklist, one flight per step, all PASS attempt 1: `2026-07-31_1630` baseline (spec unchanged, 3 awards UNEXPECTED report-only at ut 12.5 / 19.1 / 119.9), `2026-07-31_1638` windows declared + still `report` (unexpected rows 3 -> 0, `hardDivergences=0 reportOnly=0`), `2026-07-31_1645` armed (`captureCrossCheck = "gate"`, PASS, `hardDivergences=0 reportOnly=0`). Windows are the mission's PHASE BOUNDS, never pins - `[0, 100]` ascent for the two `Progression` awards, `[100, 400]` for the `VesselLoss` impact - and the three flights are their own justification: the second `Progression` measured 19.1 / 19.0 / 19.1 across them and the impact 119.7 / 119.8 / 119.9 over six archived runs. THE BOUNDS ARE ABSOLUTE PLANETARIUM UT, NOT T+ (review finding, corrected before merge): the matched value is `fixtureUT(9.06) + padDwell + T+` and pad dwell is wall-clock, so an initial `[100, 140]` ceiling left less headroom than the harness's own 30 s kRPC connect budget - a latent false `PARSEK-FAIL(ledger)` with no retry absorption. Widening the ceiling was verified to leave the gate's bite intact (an extra `VesselLoss` and an out-of-phase `Progression` both still red). Expected totals did NOT move (`funds=529600.0 science=100.0 rep=-7.999829000000001` before and after): every entry is `ut`-less so ordering is untouched, and all three rep entries are `repMode="applied"` so the nonlinear curve is not re-entered. The funds milestone entry stays window-free on purpose (KSP logs no funds award, so it is never capture-matched). RE-PIN CONTRACT binds: a flight whose awards land outside these windows gets the windows re-pinned to measured bounds with the run id, never widened blindly and never retreated to `report`. Guards: the two whole-set cells are now ALLOWLISTS naming this spec, and `test_cl2_crew_loss_ledger.py` pins GATE + the phase bounds + a corroboration replay of the measured award set (including a stray-award probe proving the gate still bites). ORIGINALLY: THE CREW-LOSS ATOM'S LEDGER HALF, and the first run in the suite that reaches the pending-tree AUTO-COMMIT out of a destroyed crewed flight. CL-1's mission, fixture and missionParams verbatim, plus exactly three things: `SetSetting autoMerge=true` before the flight (MANDATORY - without it `DecideExitGate` refuses `variant=RegularMerge`), `ExitToSpaceCenter` after the crash, and an `[expectations.ledger]` block. CL-1 itself is UNTOUCHED: a new spec id keeps `test_cl1_crew_loss.py::test_the_spec_drives_no_commit_and_declares_no_ledger_block` valid. Gates CL-1's eight recorder tokens verbatim (a superset, pinned by a unit cell) + the exit triple with the MIRROR IMAGE of S0.7's guard input set (`hasActiveTree=false hasPendingTree=true` - S0.7 exits with a LIVE tree, this with a STASHED one) + the four commit tokens no run on this profile had ever produced: `Silent full-fidelity auto-commit (scene-exit): tree='...' recordings=1 spawnable=0`, `Committed tree '...' (1 recordings). Total committed: 1 recordings, 1 trees`, `CreateKerbalAssignmentActions: 1 crew members from '...'`, and `OnSave: saving 1 committed tree(s)` - the B1 negative control INVERTED (the archived pre-commit run of this exact craft logs `saving 0`; flight 1 logged `saving 0` three times before the exit and `saving 1` after). S0.7 could not reach any of this: its seam-only tree is one point, so `maxDist = 0` and it is correctly idle-discarded; CL-2's flies 11,885 m | D1 auto-record-launch + **commit-scene-exit + auto-merge (the two values S0.7 had to DROP)**; D8 kerbals/orchestrator/reputation/funds (CL-1 claims NO D8 at all); D12 crew-death-in-flight; D14 kerbin/career/scene-flight/scene-ksc. LIVE-PROVEN 2026-07-30, **PASS on BOTH flights, attempt 1 each**: flight 1 (unpinned, measurement) run `2026-07-30_1711_CL-2-pod-impact-ledger` 172 s; flight 2 (pinned to the measured tokens) run `2026-07-30_1721_CL-2-pod-impact-ledger` 167 s, `expectations mismatches=0`. LEDGER ORACLE PASS with **0 hard divergences on both**: seed funds 500000 / sci 100 / rep 0 + a 4-entry manifest -> expected funds 529600, sci 100, rep -7.999829 against the produced save's 529600 / 100 / -7.99982834; the arithmetic closes to ~7e-7 against a 0.1 rep tolerance. The rep manifest models the death as the STOCK `stock-reputation-award` `VesselLoss` at the APPLIED (post-curve) -9.999828 plus the two `Progression` +1s, because nothing in `Source/Parsek/` ever CONSTRUCTS a `ReputationPenaltySource.KerbalDeath` action. FLIGHT 1 EARNED ITS KEEP, twice over. (a) PRODUCT FINDING: `PopulateCrewEndStates` NEVER RUNS on this path - zero occurrences in the whole log - because `NeedsCrewEndStatePopulation` requires `rec.VesselSnapshot != null` and a DESTROYED vessel has none, so the KerbalAssignment row for a kerbal who DIED commits as `KerbalEndState.Unknown` and the reservation comes out `permanent=0 temporary=1` with a stand-in generated for the corpse, where the product's own comment says `Dead -> permanent`. NOT FIXED (own change, own review); the token is deliberately NOT pinned and no crew-end-state coverage is claimed. It also BLOCKS stage B, whose in-game test skips on exactly the `KerbalEndState.Dead` this prevents. (b) HARNESS FINDING: this is the FIRST run with a live capture (`stockLines=3 deduped=3 seamRejected=0`) and all three awards still report UNEXPECTED, because `unmatched_captured_awards` joins on a UT-valued `seq_key` a flown spec cannot declare without pinning a golden trajectory value (119.7 / 119.9 / 119.8 across three runs of this craft) - so `captureCrossCheck = "gate"` was not armable on a flown scenario as the mechanism then stood. Both filed in todo-and-known-bugs.md; (b) is what the `utWindow` key and the 2026-07-31 arming above CLOSED, (a) is still open. SCOPE FENCE: D9 `tombstones`, D12 `dead-crew-strip` and `tombstone-rep-penalty` are NOT claimed and are not reachable here - `SupersedeCommit` is the only tombstone producer and runs strictly in the re-fly merge tail. That is stage B |
| B1-pad-hop | nightly | Auto-record-on-launch, atmospheric TrackSections, and a genuinely CHUTE-BORNE ground-arrival recording: the two-phase ParachuteSemiDeployed -> ParachuteDeployed part events on the craft's own parachuteSingle (D7 chute-two-phase, claimed 2026-07-25) | D1 auto-record-launch; D4 atmospheric; D7 chute-two-phase; D14 kerbin. LIVE-PROVEN (RE-PROVE) 2026-07-29: run `2026-07-29_1532_B1-pad-hop`, attempt 1, wall 408 s (mission 351 s). This is the re-prove the 2026-07-25 de-listing demanded, and it lands the leg that de-listing said had never flown: `craftCanopyObserved` MET at 11,963 m off the OBSERVED kRPC ParachuteState, `apoapsisWindow` 19,879 m, and the terminal phase is LANDED - not the commanded-latch DOWN that had awarded a ~300 m/s impact the "chute-deployed impact" end. The difference is legible in the mission JSONs: the de-listed 2026-07-25 PASS (`2026-07-25_0749_B1-pad-hop`) carries two assertions and no canopy one at all; this run carries three. One report-only Unity NRE (`MuMech.MechJebCore.OnDestroy`, autopilot teardown, not Parsek). BUDGETS, now validated by a real chute-borne descent rather than derived from EVA-4: the re-prove's mission leg ran 351 s against the DERIVED budgets descent 240 -> 360 s, mission 600 -> 900 s, wall 900 -> 1320 s, and chuteFullDeployAltMeters was raised 1000 -> 2500 because the full canopy needs 894 m just to brake. The first draft's 600 s descent assumed a ~30 m/s semi-deployed crawl and was wrong by ~8x - the semi-deployed craft sinks at up to -236 m/s. (These five numbers are pinned against the spec by `harness/lib/test_doc_spec_sync.py`; edit them here only together with the .toml.) |
| BDOCK-1-station-interceptor | nightly | FIRST two-vessel flight (18-phase machine): cross-tree Dock branch, authoritative onVesselsUndocking split, RouteConnectionWindow recorded-delta contract (the new `Route window delta:` line), same-craft-twice launch identity. Flight-1/2 wall budgets re-timed; flight-3 lesson (STATION-SEPARATE / INT-SEPARATE) + flight-4 lesson (two-step SEPARATE: drop the spent lifter AND ignite the orbital engine, thrust-verified, cap 2) both live-confirmed through RENDEZVOUS on flight 5; flight-5 lesson (MATCH-VELOCITY kill-rel-vel retargeted XFromNow ~15 s lead + bounded 600 s give-up + per-frame diagnostics + one-shot dropped-target re-acquire); flight-8 lesson (prox-ops rule: abort the pending kill-rel-vel node executor at DOCK entry before the docking AP owns the ship, else it rails-warps + packs the port target null + NREs); flight-9 lesson (core.target one-Update sync trap: stagger the docking-AP enable one poll after the port target); flight-10/11 lesson (prox-ops observability [angular_velocity/sas/rcs/docking_ap_status + per-frame DOCK diag line] + attitude hold [SAS+RCS after each separation and at DOCK entry] + LIVENESS watchdogs [budgets bound SLOW, watchdogs bound BROKEN: DOCK enable-never-took / died-mid-approach / no-progress fast flakes, TRANSFER stall fast flake, bounded dropped-target re-arm x3]). flight-13 ROOT CAUSE (behind every dock failure since flight 7): pre-`launch_vessel`-reload PART handles are stale - the reload destroys every Part, so the captured docking-port handle resolves to a destroyed part and assigning it silently CLEARS the target; VESSEL handles survive (P9 answered). Fix: resolve port + docking-state + transfer tanks LIVE at call time. Flight 13's liveness layer fast-flaked in 10 s with the named E1a reason (wall 2133 s) and pinned this. Flight 16 (2026-07-24): MISSION-OK END TO END (launch, separate, mid-mission commit seam, launch_vessel, rendezvous, hard dock, LF 40 + mono 15 transfers, undock, TERMINAL) - and the verifier chain caught the FIRST mission-machinery-found Parsek recording defect: analyzer RED, INV4-PARTEVENT-PID x13 on the Station recording d5355cc6. Root cause: the launch_vessel FLIGHT->FLIGHT reload is classified as a quickload (stale vesselSwitchPending), and RestoreActiveTreeFromPending's NAME fallback adopted the fresh-rollout Interceptor (same .craft, same "Kerbal X" name, different Vessel.id) and PID-remapped the Station recording onto it, so the whole Interceptor flight recorded into the Station recording with foreign craft-baked part pids. FIXED Parsek-side: QuickloadResumeMatchGuard (fresh-rollout pid + launch-guid gates in the restore match loop); forensics in todo-and-known-bugs.md flight-16 entry | D1 auto-record-launch; D3 orbital-checkpoint; D4 atmospheric, exo-propulsive; D5 cross-tree-foreign-dock, undock-split, bg-recording; D7 dock-undock, rcs; D8 route; D10 candidate-detection, ksc-origin, dock-producer, delivery, pickup, mixed-direction, resource-cargo; D14 kerbin, warp-rails, scene-flight, sandbox. LIVE-PROVEN 2026-07-24: run `2026-07-24_1204_BDOCK-1-station-interceptor`, wall 2,156 s, reached after 14 driver-INVALID autopilot-flake attempts and 3 PARSEK-FAILs that were fixed on the branch. Re-confirmed 2026-07-25 (`2026-07-25_0929`, wall 2,164 s). Row moved here 2026-07-29: it had sat under "not yet live-run" for five days after its first green flight. |
| FORGE-bdock-station | operator | (Not a Parsek-surface test) FIXTURE-FORGE: launch_vessel the docking Kerbal X onto the pad + SaveGame -> stamps the bdock-station-pad fixture headlessly (replaces the operator fixture flight) | D14 kerbin, scene-flight, sandbox (tooling - claims no Parsek-surface cells). LIVE-PROVEN 2026-07-23: run `2026-07-23_2031_FORGE-bdock-station`, wall 60 s. Its output fixture `bdock-station-pad` was harvested and committed the same day (`cbd465929`), and is what BDOCK-1 then flew against. |
| FORGE-eva3-pad | operator | (Not a Parsek-surface test) FIXTURE-FORGE (EVA-3 sibling): launch_vessel the Kerbal X onto the pad with THREE named crew + SaveGame -> stamps the eva3-pad-3crew fixture headlessly. Uses the review-follow-up-2 crew (by NAME) + launch_site plumbing | D14 kerbin, scene-flight, sandbox (tooling). LIVE-PROVEN 2026-07-24: run `2026-07-24_1720_FORGE-eva3-pad`, wall 54 s; output fixture `eva3-pad-3crew` committed the same day (`8d07890e4`), which unblocked EVA-3. |
| FORGE-eva2-lko | operator | (Not a Parsek-surface test) FIXTURE-FORGE, the FIRST ORBITAL one (mission `forge_lko`): boots the SAME bdock-forge-base, launch_vessel the Kerbal X with TWO named crew (Valentina + Bob), then flies the LIVE-PROVEN B-DOCK Interceptor-leg shape - MechJeb ascent, circularization with node-executor autowarp EXPLICIT (flight-12 lesson), the two-step separation contract (drop the spent core AND ignite the orbital stage, thrust-verified, cap 2), then a PARK phase that cuts throttle, clears nodes, holds SAS+RCS and requires a HELD stable ~100 km circular orbit (pe >= 75 km, tumble <= 0.05 rad/s) before SaveGame. Crew is gated ON THE PAD (crew_count >= minCrew, fail-closed on the -1 unread sentinel) so an uncrewed stamp flakes in 300 s instead of after a 10-minute flight. autoRecordOnLaunch pinned false so the fixture carries no recordings / trees / ledger state (the stamped .sfs does keep an inert populated `SCENARIO{name=ParsekScenario}` node - `gameStateEventCount=18` + one MILESTONE_STATE row - which is what suppresses PreParsekBackup at load) | D14 kerbin, scene-flight, sandbox (tooling). LIVE-PROVEN 2026-07-24: run `2026-07-24_1807_FORGE-eva2-lko`, wall 323 s - the first ORBITAL forge; output fixture `eva2-lko-crewed` committed the same day (`d4380ef52`), which unblocked EVA-2. |
| S1.5-rewind-loop | nightly (RE-TIERED from operator 2026-07-26) | TimeJump-past-EndUT spawn, then rewind-strip-respawn cycle observables | D6 time-jump; D18 time-jump-observables, rewind-strip-respawn-cycle; D8 epoch-isolation, recalc-engine; D9 rewind-to-separation, refly-gate. LIVE-PROVEN 2026-07-29, on its first execution ever: run `2026-07-29_1528_S1.5-rewind-loop_a2`, wall 69 s, every verifier PASS/SKIPPED. The 2026-07-26 re-tier note's central prediction held exactly - `LoadGame` took the FOCUS route into FLIGHT from the `gloops-airshow` host and all eight verbs EXECUTED rather than deferring, so the happy path came in at 69 s against a 2,400 s ceiling that only ever bounded the defer path. Attempt 1 (`2026-07-29_1525`) was driver-INVALID(`driver-arg`) on a STAGING fail-open, not on anything this spec asserts - see HARNESS-INJECT-FAILS-OPEN in todo-and-known-bugs.md. TAG REVISITED 2026-07-31 and KEPT, reversing the "can now drop" line that stood here: the three PENDING-OPERATOR live asserts named in its GAP note (crew re-reservation, resource reset, self-authored RewindPoint) stay out of scope for the DRIVABLE subset, and out-of-scope-for-the-driver is precisely a debt an OPERATOR still owes - not a discharged one. S1.5's entry in `PendingOperatorTagHonestyTests.CARRIERS` records those three asserts as its reason. NOTE what that cell does and does not do (this sentence used to claim a rule it enforces): it pins WHO carries the tag and requires a recorded reason, and it machine-checks a reason that cites `tier = "operator"` - but it does NOT verify a prose reason. A carrier with an invented reason passes. The inventory guarantees the set was REVIEWED; the reviewer supplies the truth. |
| S4.1-rewind-merge | nightly (RE-TIERED from operator 2026-07-26) - **seam fix PROVEN 2026-07-28; S4.1-IDLE-DISCARD FIXED 2026-07-30 (branch `fix-s41-idle-discard`); FLAKE QUARANTINE CLEARED 2026-07-30 by a deliberate 5-run sweep; SAVE-PARSE GATING ARMED + LIVE-PROVEN 2026-07-31 (branch `r9-arm-s41`) - the ONLY committed spec with `gating = true`; lifetime rate in the cells column** | Full re-fly cycle: InvokeRewind a crashed slot, merge-dialog fold, corpus survival, read-back guard | D9 rewind-to-separation, refly-gate, reconciliation-bundle, read-back-guard, terminal-kind-classify, merge-journal; D8 recalc-engine. LIVE-PROVEN 2026-07-28: run `2026-07-28_1639_S4.1-rewind-merge_a2`, wall 61 s (flakedThenPassed). CONFIRMED clean on attempt 1 2026-07-29: run `2026-07-29_1530_S4.1-rewind-merge`, wall 71 s, all verifiers green, and it did NOT reproduce S4.1-IDLE-DISCARD. FLAKE QUARANTINE CLEARED 2026-07-30 by the deliberate multi-run sweep the previous note demanded, flown off the `fix-s41-idle-discard` build (provision result=OK, deployed automation DLL sha256 4bd6f246 identical to the worktree's `bin/Debug`, fix string verified present): FIVE consecutive runs, every one PASS on attempt 1, no retries - `2026-07-30_0940` 72 s, `_0942` 58 s, `_0944` 57 s, `_0945` 57 s, `_0947` 58 s. Every run: all 6 driver steps verdict=OK, kspExit.code=0, recordings.count=4, expectations mismatches=[], analyzer red=0, logValidate PASS, anomalySweep hits=[], zero `[Parsek][ERROR]`, and the required `AppendRelations outcome=refused-unflown-provisional` token present once. The generated flake ledger now reads total=5 numerator=0 rate=0.0 quarantined=false. Lifetime 7 PASS / 3 INVALID at that sweep; 12 PASS / 3 INVALID after the second 2026-07-30 sweep below. HONESTY CAVEAT RESOLVED 2026-07-30 (branch `s41-prefix-live-coverage`): the 0940-0947 sweep's guard branch was never entered because of S4.1-PREFIX-RACE, and the caveat's candidate cause (`f97717744`) is REFUTED - run `2026-07-28_1939` carried that commit (`persisted=True` in its log) and still entered the prefix. The real cause: `InvokeRewind` completes when the re-fly MARKER lands, but `ParsekFlight.HasActiveTree` - what the scene-exit prefix gates on - only goes true when the `RestoreActiveTreeFromPending` coroutine later resumes the pending-LIMBO tree as active (~300 ms after marker completion; in `2026-07-28_1939` the resume won the race by 121 ms and the prefix fired; in the 0940-0947 sweep the seam won every time and the exit slipped through un-intercepted, `DialogVariant.None`, concluding via the deferred post-transition dialog). FIX in the seam: `AnswerMergeDialog` now defers its driven exit through the pure `TestCommandMergeAnswer.DecideConclusionDrive` until the restored tree is active in FLIGHT (bounded 30 s; on expiry it drives anyway and the deferred-dialog fallback concludes as before). The spec now REQUIRES the pre-transition route: `refusing - refly-active` + `Pre-transition tree merge dialog: .* labels=ReFlyAttempt` + the synchronous `answermergedialog choice=merge result=committed`. GUARD LIVE-PROVEN 2026-07-30 by a 5-run sweep off the `s41-prefix-live-coverage` build (deployed automation DLL hash-verified identical to the worktree's `bin/Debug`, both new UTF-16 strings present): runs `2026-07-30_1746` (76 s), `_1748`, `_1749`, `_1750`, `_1751` (57 s each), every one PASS on attempt 1 under the EIGHT-token contract - each log shows the settle-wait line (`waiting for re-fly resume to settle`, proving the race was live that run), then `TryAutoDiscardIdleActiveTree: refusing - refly-active`, the ReFlyAttempt pre-transition dialog, and the synchronous merge answer, with zero `idle detected` and zero `Deferred merge dialog fired` lines. The same race also explains R1's route nondeterminism (run `2026-07-26_2237` answered via the deferred dialog, `2026-07-26_2303` via the pre-transition dialog); R1's contract does not pin the route, so it stays green either way and now settles deterministically on the pre-transition path. SAVE-PARSE GATING ARMED 2026-07-31 (branch `r9-arm-s41`): S4.1's `[expectations.rewind]` block, RESERVED and recorded SKIPPED since the spec was written, now carries `gating = true` - the first and only committed spec to arm the M-C2 save-parse verifier, so what this scenario gates grew from log contracts + a recording-count floor to include the produced save's structural truth. Promoted through the three-run workflow: `2026-07-31_1628` report-only reading (PASS, 59 s) measured `parsed=true scenarioFound=true blocks=["rewind"] armedBlocks=[] mismatches=[]` and `observed.rewind = {supersedeRows 0, tombstones 0, rewindPoints 0, rewindRetirements 0}` - already inside the declared `max = 0` windows, so arming moved no verdict; `2026-07-31_1635` armed (PASS, 59 s, `status=PASS gating=true armedBlocks=["rewind"]`); `2026-07-31_1637` NEGATIVE CONTROL (`supersedeRows = { min = 1 }`, reverted) reddened `PARSEK-FAIL(save-structure)` with `mismatches=["rewind.supersedeRows 0 < min 1"]`, proving the gate fails when it should. TWO READINGS RECORDED, NOT PINNED: the merge REAPS `rp_b9_root` (`rewindPoints` 0; the save carries no `REWIND_POINTS` node at all), and the B9 corpus writes zero `BRANCH_POINT` rows (`branchPoints={}` verified against the raw .sfs - a true reading, not a parser miss; topology is carried by `parentRecordingId`). Structure facets measured on all three runs: `trees 1, committedTrees 1, recordings 4, terminalStates {Destroyed 1, Landed 1, Orbiting 1, SubOrbital 1}`. `pending-operator` TAG DROPPED 2026-07-31 on this spec's own stated rule ("the pending-operator TAG stays until that first green run") - that run was `2026-07-28_1639_..._a2`, three days earlier, with seven green runs since; the tag had stopped describing the record and started contradicting it. Non-gating, tier unchanged at nightly. Lifetime 14 PASS / 3 INVALID (12 before this branch + the two PASS runs above; the negative control's deliberate PARSEK-FAIL is counted in neither, and `coverage/duration.json` - PASS results only - moves n=12 to n=14 in step). |
| R1-rewind-loop-flown | operator (promotion EARNED 2026-07-28, DELIBERATELY NOT APPLIED - see the FLIGHT 4 note) | FIRST rewind cycle driven from a REAL FLOWN flight: the delegated live-proven B2 ascent machine, a mid-flight CommitTree + StopRecording + RecordingState issued through the NEW verb-agnostic seam bridge (`ACTION_PARSEK_SEAM_COMMAND`), the dispatcher's `recording-active` gate carried as an OBSERVED precondition (`recorderIdleBeforeRewind` reads `recording=false` off a RecordingState reply before the rewind is commanded), then a real Rewind-to-Separation from FLIGHT judged by an OBSERVATION - the game clock RUNNING BACKWARD (`clockRewound`, corroborated by `vesselStateChanged`), never by InvokeRewind's own OK (which rides as one strictly-additional `rewindSeamAccepted` row). Also the first mission to prove a mission can drive ANY seam verb mid-flight, not only CommitTree | D1 auto-record-launch; D4 atmospheric, exo-propulsive; D14 kerbin; D9 rewind-to-separation, refly-gate, reconciliation-bundle, read-back-guard, supersede-relation, merge-journal; D8 recalc-engine. LIVE-PROVEN 2026-07-28: run `2026-07-28_1509_R1-rewind-loop-flown`, wall 304 s, attempt 1 - the flight the row's own "PROMOTION NOW EARNED, not yet applied" note was waiting on, and the discriminating experiment that resolved R1-EMPTY-PROVISIONAL as a fixture artifact. PROMOTION EARNED BUT NOT APPLIED: `R1-rewind-loop-flown.toml` still reads `tier = "operator"`, and operator is in NO `hlib.CADENCE_TIERS` set, so R1 runs ONLY under an explicit `--id` / `--tier operator` - it does NOT fly nightly. An earlier draft of this cell claimed the promotion was applied on 2026-07-29; that was wrong and no spec was ever edited. Flipping the tier is a real scheduling change (it adds ~304 s to every nightly and R1's spec carries a long "what its next run is for" list, item 6 of which only just closed), so it is left as a deliberate human call - the same standard applied to B16 in this same pass. |
| L1-hire-kerbal-career | daily | Hire debits funds by exactly the pinned cost, nothing else | D8 kerbals, funds, recalc-engine, orchestrator, ksp-state-patcher, action-blocking; D12 hire-dismiss-patches; D14 career, scene-ksc. LIVE-PROVEN 2026-07-23: run `2026-07-23_1952_L1-hire-kerbal-career`, wall 52 s - reached after the first live run that day red'd on the seam double-debit this row's blocker described, and that fix landed. Re-confirmed 2026-07-25 (`2026-07-25_0742`). `pending-operator` TAG DROPPED 2026-07-31: it was pending-FIXTURE residue (the header still told a reader to re-tier once `fresh-career` landed - it landed, and the tier had already been `daily` for some time), not a record of outstanding operator work. This spec names no `VERIFY-PENDING-OPERATOR` assert; its four L1 siblings each named one and lost their tags in the SAME pass, once a review corrected the audit to check whether those markers were still outstanding (they were not). Precedented by `L1-passive-sandbox`, dropped 2026-07-26 for the same reason. |
| L1-dismiss-kerbal-career | daily | Dismiss is pool-neutral | D8 kerbals, recalc-engine, orchestrator, ksp-state-patcher, action-blocking; D12 hire-dismiss-patches; D14 career, scene-ksc. LIVE-PROVEN 2026-07-23: run `2026-07-23_1914_L1-dismiss-kerbal-career`, wall 52 s. Re-confirmed 2026-07-23 (`_1951`) and 2026-07-25 (`2026-07-25_0741`). `pending-operator` TAG DROPPED 2026-07-31: its `VERIFY-PENDING-OPERATOR` marker asked an operator to confirm a fixture constant and record it, and `fixtures/saves/README.md` records the chosen kerbal (`Bill Kerman`), this spec's step arg IS `Bill Kerman`, and the constants row reads pool-neutral rather than pending. The oracle hard-gates scalar pools, so the green run IS that confirmation; the marker is now recorded as OPERATOR-VERIFIED in the spec. |
| L1-research-node-career | daily | Research debits science exactly | D8 science, recalc-engine, orchestrator, ksp-state-patcher, action-blocking; D14 career. LIVE-PROVEN 2026-07-23: run `2026-07-23_1917_L1-research-node-career`, wall 52 s. Re-confirmed 2026-07-23 (`_1953`) and 2026-07-25 (`2026-07-25_0744`). `pending-operator` TAG DROPPED 2026-07-31: its `VERIFY-PENDING-OPERATOR` marker asked an operator to confirm a fixture constant and record it, and `fixtures/saves/README.md` already marked the `basicRocketry` cost **VERIFIED**. The oracle hard-gates scalar pools, so the green run IS that confirmation; the marker is now recorded as OPERATOR-VERIFIED in the spec. |
| L1-research-node-science | daily | Same in science mode (no funds/rep pools) | D8 science, recalc-engine, orchestrator, ksp-state-patcher, action-blocking; D14 science-mode. LIVE-PROVEN 2026-07-23: run `2026-07-23_1917_L1-research-node-science`, wall 52 s. Re-confirmed 2026-07-23 (`_1954`) and 2026-07-25 (`2026-07-25_0744`). `pending-operator` TAG DROPPED 2026-07-31: its `VERIFY-PENDING-OPERATOR` marker asked an operator to confirm a fixture constant and record it, and it rides the same **VERIFIED** `basicRocketry` node cost. The oracle hard-gates scalar pools, so the green run IS that confirmation; the marker is now recorded as OPERATOR-VERIFIED in the spec. |
| L1-upgrade-facility-career | daily | Facility upgrade debits funds per-level exactly | D8 facilities, funds, recalc-engine, orchestrator, ksp-state-patcher, action-blocking; D14 career, scene-ksc. LIVE-PROVEN 2026-07-23: run `2026-07-23_1955_L1-upgrade-facility-career`, wall 52 s - reached after the first live run's ledger math passed (-150,000, hardDivergences=0) but its logContract red'd on `FacilityUpgraded` never being recorded, the blocker this row described, and that fix landed. Re-confirmed 2026-07-25 (`2026-07-25_0745`). `pending-operator` TAG DROPPED 2026-07-31: its `VERIFY-PENDING-OPERATOR` marker asked an operator to confirm a fixture constant and record it, and the constant was proven live at exactly -150,000 with hardDivergences=0. The oracle hard-gates scalar pools, so the green run IS that confirmation; the marker is now recorded as OPERATOR-VERIFIED in the spec. |
| B16-eve-orbit | operator (PROMOTE to nightly after its first green flight) | The FIRST mission to fly B7's five interplanetary params AND B11/B12's capture tail together: capture burn, held park and mid-mission seam CommitTree after a HELIOCENTRIC traverse. HONEST SCOPE - B11/B12 already own the D1 `commit-in-foreign-soi` cell on two bodies, so doing it a third time at Eve adds no commit path and no terminal classification. The ONE thing it adds is that the capture tail has only ever run after a LUNAR transfer: here the committed tree's terminal orbit body is reached through TWO SOI transitions with an arrival v_inf ~4x the Mun's. Eve's atmosphere is NOT claimed (the park is at ~5,000 km) - see B15 | D1 auto-record-launch, commit-in-foreign-soi; D3 orbital-checkpoint; D4 atmospheric, exo-propulsive, exo-ballistic, cohesive-cross-body-coast; D14 kerbin, eve, soi-count, warp-rails, warp-high. LIVE-PROVEN 2026-07-29 on its FIRST FLIGHT, attempt 1: run `2026-07-29_1718_B16-eve-orbit`, wall 1,825 s (mission 1,766 s), every verifier PASS/SKIPPED. All 19 phases reached through ORBIT-COMMITTED, and all six assertions met: `reachedTargetSoi` Eve, `flybyPeriapsisFloor` 4,850,416 m, `capturedInTargetOrbit` ecc 4.67e-05, `parkedStable`, `treeCommitted` OK. The committed save carries exactly 8 recordings - the number the PROVISIONAL window predicted from B11/B12/B13/B14 and the three green B7 runs, now MEASURED on the Eve lane too. Four report-only Unity NREs, all stock/third-party teardown at scene exit (KnowledgeBase map-focus, CrewHatchController, MechJebCore), none Parsek. COST, and it matters for the promotion decision: the spec's budget prose sized this lane at ~4,700 s and warned a nightly rotation with retry-once could spend ~2.6 HOURS a night. MEASURED 1,825 s makes the retry-once worst case ~61 min, and puts B16 CHEAPER than B13 (2,825 s), BDOCK-1 (2,164 s) and B14 (2,141 s), all of which are nightly. TIER NOT CHANGED. (`pending-operator` TAG ADDED 2026-08-01: this row already called the promotion "an explicit human call" and described R1's identical state as "the same standard applied to B16 in this same pass" - and R1 carried the tag while B16 did not. A tag audit that inspected only specs MENTIONING `PENDING-OPERATOR` could not see B16, which never writes it; the completeness check now covers operator-tier specs too.) The spec's promotion rule has TWO conditions - fly green AND replace the PROVISIONAL pins with MEASURED ones - and it states the pins need a human reading the result. Only the first condition is met here, so `tier = "operator"` stands and the promotion is left as an explicit human call with the measurements above in hand. |

### Committed, not yet live-run (0)

EMPTY again as of 2026-07-30 (V1-map-dwell-mun-orbit landed here and flew the
same day; its row now lives in the visual-validation section below). Before
that it had been empty since 2026-07-29, when the last two holdouts
(S1.5-rewind-loop and B16-eve-orbit) flew. Keep this heading rather than
deleting it: a new spec lands here until its first flight, and an empty section
is the honest way to say the backlog is clear.

### Visual-validation program (V1): flown, RED BY FINDING (1)

| Test case | Tier | What it proves | Cells / notes |
|-----------|------|----------------|---------------|
| V1-map-dwell-mun-orbit | operator | THE FIRST VISUAL-PROGRAM lane (design-testing-unified section 6, V1): aims the shipped render parity oracle (MapRenderProbe + RenderParityOracle, previously exercised only by S1.6/S1.7's one-frame synthetic fixtures) at REAL flown geometry ACROSS TIME. Machine: the LIVE-PROVEN B11 profile (delegated `mlib.b5_decide`, captureEnabled) flies and commits in the Mun SOI, then the R1-proven rewind tail (STOP -> RECORDER-IDLE -> InvokeRewind rp_b9_root -> OBSERVED backward clock) puts the flown tree back into replay scope - LOAD-BEARING, not decoration: PlaybackScopeTracker (BUG-B historical-not-replayed) keeps a forward-play committed tree DORMANT, measured in BDOCK-1's archived log (674 post-commit probe frames, all ghosts=0), so a dwell without the rewind is structurally vacuous (the S1.4 552-frames-ghosts=0 class). Then the dwell: kRPC map camera staged (OBSERVED camera_mode readback, new `read_camera` opt-in channel), native warp to just past the flown launch, 45 s held 1x, a rails stair 10x->1000x->10x, and a held 10x re-cross of the RECORDED Kerbin->Mun SOI boundary UT. Anti-vacuity pins: `probe frame summary ... ghosts=[1-9]` + `faithful-parity summary sampled=[1-9]` required (verboseLogging pinned - the summaries are VerboseRateLimited). NO anomaly budget armed, NO overTolerance gate: this scenario IS the calibration instrument known-gate 0 defers to. Post-mission tail is AnswerMergeDialog choice=DISCARD (never merge: the never-flown re-fly provisional is empty, and merging one is R1-EMPTY-PROVISIONAL, a [Parsek][ERROR]) | D6 recorded-vs-rendered-parity (first claim over non-synthetic recordings across time; gated by the two dwell tokens); D14 scene-map (gated by the OBSERVED mapCameraObserved mission row + the dwell tokens). NOT claimed: B11's flight cells, R1's D9 cells, any anomaly cell. **FLOWN TWICE 2026-07-30, both flights end-to-end green at the mission layer and RED `PARSEK-FAIL(anomaly)` on the same real finding - the intended shape of a first calibration flight, not a regression.** Flight 1 (run `2026-07-30_1955`, wall 1,598 s, OPERATOR-ASSISTED: a manual helping warp during the in-SOI coast, so it does not count as unattended): mission PASS, all 20 assertion rows met, driverValidity/analyzer/logValidate/testResults PASS, sweep `hitCounts={line-blink: 2}` `unlistedReasons=[]`. Flight 2 (run `2026-07-30_2023`, wall 2,044 s, FULLY UNATTENDED): MISSION-OK, zero unmet rows, identical verifier picture and the IDENTICAL baseline `hitCounts={line-blink: 2}` `unlistedReasons=[]`. The sweep FAIL preempts the expectations verifier, so all 12 required pins + the forbidden-ERROR contract were evaluated MANUALLY against both collected logs (`logs/2026-07-30_2322`, `logs/2026-07-30_2357`): all 12 matched on both (130/130 nonzero-ghost probe frames, 12/12 `faithful-parity summary sampled=1 overTolerance=0` passes, zero `[Parsek][ERROR]`), so the faithful-parity `sampled>0` pin's stated measurement protocol is CLOSED - the lens measures real replayed geometry, and its 55-per-run `skip.reaimed-or-foreign-seed` passes are the injected-b9-corpus ghost, not the flown one. WHAT THE BASELINE SAYS FOR ARMING (known-gate 0): across three dwells (incl. the discarded first attempt) `line-blink` fired EXACTLY 2x per dwell on the flown MAIN recording's proto orbit line under the 100x ramp step, at ownership-handoff frames, and NOTHING else fired - zero unlisted reasons, zero icon-teleport/icon-off-orbit. So: (a) `line-blink` is deterministic on this geometry and is a REAL defect signal or a benign handoff transient - diagnose before arming (forensics: todo `V1-REPLAY-LINE-BLINK`); (b) a future budget of `{ token = "line-blink", maxCount = 2 }` would exactly fit the measured behavior IF the blink is ruled benign - do not arm it before the diagnosis; (c) the nine ungated reasons stayed SILENT on real geometry, which is the first actual evidence for the gate-vs-instrument call on them. PROMOTE TO a cadence only after the line-blink call is made (either fixed -> green runs, or ruled benign -> budgeted). An earlier same-day invocation burned two full flights on HARNESS-INJECT-FAILS-OPEN (fresh worktree; evidence appended to that todo entry) |

FLAKE-LEDGER ARTIFACT worth knowing before reading `coverage/flake.json`: the
2026-07-29 session left B1-pad-hop and S1.5-rewind-loop QUARANTINED at rate 0.50
(1 of 2). Neither is scenario flakiness. Both attempt-1 failures were
fresh-worktree ENVIRONMENT faults on a checkout that had never run the harness -
B1 on a missing mission venv (`tooling-venv`, terminal before any KSP boot) and
S1.5 on HARNESS-INJECT-FAILS-OPEN - and both went green in the same session. The two
recoveries were NOT the same shape, which matters if you are reading the run list:
S1.5 recovered through a real in-run retry (`_a2`), whereas B1's `tooling-venv`
INVALID was terminal before any KSP boot (wall 0 s, no `_a2`) and its PASS came from
a fresh invocation a minute later, after the venv was bootstrapped. Expect the rate
to decay to 0 over the 7-day window without anyone touching either spec; do not read
the quarantine as a product signal.

### EVA (M-C2 + EVA-4), committed (4): all 4 LIVE-PROVEN

EVA-1 and EVA-3 are LIVE-PROVEN (2026-07-24). EVA-4 was live-proven the same day,
DE-LISTED on 2026-07-25 when the first full sweep red'd it (the kerbal's own canopy
cut itself mid-descent and the kerbal died), fixed headlessly on 2026-07-26 (that
defect plus the fail-open it exposed), and RE-PROVEN the same day by flight 4 -
PASS on attempt 1, all seven verifiers, wall 409 s. EVA-2 is LIVE-PROVEN too, as of
2026-07-24: its blocker was the `eva2-lko-crewed` fixture, FORGE-eva2-lko produced it
that day and it was committed as `d4380ef52`, and EVA-2 then passed twice
(`2026-07-24_1813`, wall 64 s; `2026-07-25_0738`, wall 57 s). This paragraph and the
section heading said "3 LIVE-PROVEN, 1 blocked" until 2026-07-29 - doc lag behind the
fixture commit, not an open gate. Parsek surfaces: EVA/Board tree branch
points + EvaCrewName, FlagEvent fidelity, crew conservation, foreground vs
deferred EVA recording paths, and (EVA-4 only) the mid-flight ATMOSPHERIC EVA
branch with the kerbal's own falling-vessel recording.

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| EVA-1-pad-flag | nightly - **`pending-operator` TAGGED 2026-08-01** (the promotion call this row's spec header names is still open; P1/P3/P6 are done and it is live-proven, so the cadence decision is the only thing left, and only a human makes it) | Foreground EVA branch (structural snapshot + EvaCrewName), FlagEvent capture into the foreground recorder, board merge back to the pod | First flight 2026-07-24: EvaExit (ladder release applied) + EvaBoard merge-back + StopRecording + CommitTree chain all green, analyzer save CLEAN. TWO gate/release defects FOUND + FIXED (both edge-triggered-FSM family): (1) PlantFlag gate read `Events["PlantFlag"].active`, an edge-triggered cache that latches stale-false when a kerbal lands and stands still on the pad; gate now reads live `CanPlantFlag()` + plantable-fsm-state. (2) Flight-2 (with the fixed gate + `blocked=` diag) NAMED the real blocker: `blocked=fsm=Ladder_Idle,no-ground-contact` for the full 180 s - EvaExit's `released=true` was a FALSE POSITIVE. The release fired `On_ladderLetGo` during the transitional `st_ladder_acquire` state (~0.2 s post-exit) where the event is not registered = a silent `RunEvent` no-op, so the kerbal hung on the hatch ladder forever. `ApplyLadderRelease` now fires ONLY from a receptive ladder state, VERIFIES the fsm left the ladder (synchronous RunEvent), bounded re-fire cap 3, and `released=true` means verified-left. (3) Flight-3 (both FSM fixes landed): the plant went in PHYSICALLY (`Kerbin/FlagPlant` milestone credited) but the recording-layer FlagEvent was never captured - `afterFlagPlanted` never fired. Decompiled `FlagSite`: the SiteRename popup that fires `afterFlagPlanted` (inside its button `afterDialog` callback) only spawns after the FULL plant-animation timer (`On_flagPlantComplete` KFSMTimedEvent) in `OnPlacementComplete`, but the seam's "edge case 10" fallback declared the dialog "answered-externally" as soon as the FlagSite vessel existed (created at `flagPlant_OnEnter`, ~110 ms in, before the animation), false-OK'd the plant, and `flushandquit` tore the scene down before the popup ever spawned. Fixed (seam side): the answered-externally inference is DELETED; the seam now waits for the real popup (`DecideSiteRenameDialogAction`) then invokes its dismiss button's own callback so `afterFlagPlanted` fires and Parsek captures the FlagEvent; honest `flag-timeout` if the popup never spawns. See todo-and-known-bugs.md. Re-flight pending to prove all three fixes + P6 flag-capture (`Flag planted: ... date stamped` + `Flag event captured`) end to end ; LIVE-PROVEN 2026-07-24 (flight 4 full PASS; 3 seam/fsm defects found+fixed: stale plant-gate cache, silent no-op ladder release, SiteRename dialog false-OK that skipped afterFlagPlanted) |
| EVA-2-orbital-board | daily | Deferred auto-record-on-EVA path (D1 auto-record-eva) + re-board; the settleSeconds dwell beats the auto-record race (F7) | LIVE-PROVEN 2026-07-24: run `2026-07-24_1813_EVA-2-orbital-board`, wall 64 s; re-confirmed 2026-07-25 (`2026-07-25_0738`, wall 57 s) and 2026-08-01 on the `fix-orbital-eva-kepler` build (`2026-08-01_1626`, wall 61 s, EVA kerbal `points=5 maxDist=1888m`) - that last run is gate 12's live proof, and its caveats are recorded there, not here. Its blocker was the `eva2-lko-crewed` fixture, which FORGE-eva2-lko produced on 2026-07-24 (run `2026-07-24_1807`) and which was committed the same day as `d4380ef52`. The forged contract the spec relies on: orbital stage only, 2 named crew with Valentina as crew[0], ~100 km circular pe >= 75 km, throttle cut, nodes cleared, SAS+RCS held, zero Parsek state. Row corrected 2026-07-29 - it had read "STILL pending-fixture: `eva2-lko-crewed` does not exist yet" for five days after the fixture landed. |
| EVA-3-multi-kerbal | nightly | Two sequential EVA branch points + two board merges in one tree; the F2 quiescence conjunct protects the second exit | Fixture `eva3-pad-3crew` COMMITTED (P2 done, forged 2026-07-24). First flight 2026-07-24: driverValidity PASS (all 4 EVA verbs OK, each exit->board cycle under 0.8 s wall), analyzer red=0, logValidate PASS, anomalySweep PASS; the only red was 2 missing logContract tokens (`detected boarding from EVA`, `Tree board merge completed`) for BOTH cycles. A PARSEK defect FOUND + FIXED: an EVA branch parks the kerbal's recording in BackgroundMap and only the post-switch first-modification watcher promotes it, so a `release=false` exit-then-board inside ~0.18 s left `recorder=null` at the board and BOTH the boarding detection and `HandleTreeBoardMerge` failed closed - the saved tree carried 2 EVA branch points and ZERO Board branch points, kerbal recordings terminal Destroyed instead of Boarded. `OnCrewBoardVessel` now rebinds a background-only EVA recording to the live recorder at the board (`DecideEvaBoardPromotion`, 11 xUnit cells); the seam was deliberately NOT changed (a wait-for-merge there would reclassify a dropped merge as driver INVALID instead of PARSEK-FAIL). Re-flight pending to pin the P3 count window and confirm 2 EVA branches + 2 boarding detections + 2 board merges. Batch autorun evaluated = NOT wired (batchComplete SKIPPED, see EVA-1 spec) ; LIVE-PROVEN 2026-07-24 (flight 2 full PASS after the board-merge data-loss fix; 2 promotions/2 merges/7 recordings) |
| EVA-4-atmo-chute | nightly | Mid-flight ATMOSPHERIC EVA branch (every other EVA case exits on the ground or in orbit), atmospheric TrackSections on the KERBAL's own falling-vessel recording, the EVA chute captured as a two-phase part event on the kerbal (D7 chute-two-phase, previously unclaimed), and the DOWN terminal applied to a KERBAL recording with the kerbal ALIVE | FLIGHT 1 (2026-07-24) ASSERT-FAILED AS DESIGNED, re-tuned, re-fly pending. The machine, the named-failure design and the diagnostics all worked: `eva-window-missed: altitude 702m fell below the window floor 800m (vspeed -295.2m/s, situation FLYING, craftChute armed)`, phasesReached PRELAUNCH/ASCENT/COAST/DESCENT, apoapsisWindow met (19,879 m), no budget burn (107 s wall). MEASURED profile: peak altitude 11,965 m at ut 60.6; unchuted descent settles at TERMINAL -301 m/s by ~2,700 m; chute armed at 2,382 m / -301 m/s and 5.1 s later at 855 m the rate had moved 4.7 m/s. ROOT CAUSE (recording + decompile, not inference): the pod's `.prec` carries ZERO Parachute* part events, and decompiled `ModuleParachute.cs:1255-1290` gates ACTIVE->SEMIDEPLOYED on `automateSafeDeploy >= deploymentSafeState` while the fixture persists `automateSafeDeploy = 0` (only while SAFE) - which DeploySafe never reads at ~300 m/s in dense air. Arming low was INERT, not late; a craft at terminal velocity never slows on its own. THREE FIXES: (1) ARM WHILE SLOW - the machine now arms on the COAST->DESCENT transition frame itself (falling through into the descent body so there is no one-poll delay; measured entry rates -7.4/-16.9/-26.1/-35.5 m/s, bound 30), i.e. at the apoapsis crossing where DeploySafe is trivially SAFE and Kerbin is already ~0.2 atm; (2) RAISE the stock full-deploy altitude from the fixture's 1000 m to 2500 m via kRPC `Parachute.DeployAltitude` (a PAW tweakable) so the full canopy exists well above the EVA band - the Mk16 animation is ~8 s (`deploymentSpeed = 0.12`); (3) GATE ON OBSERVED STATE - new opt-in `craft_chute_state` telemetry channel (kRPC `ParachuteState`, "" unread = fail-closed) so the window requires the chute to READ Deployed, never the commanded latch that was true for the whole failed flight. Window re-tuned [800,2400]/60 -> [700,2100]/25; descent budget provisionally raised 240 -> 480 s and runtime 1560 -> 1920 s because the semi-deployed rate was not measured yet. A new `craftCanopyObserved` assertion row reports observed-vs-commanded in the result JSON. Same-evidence FINDING SPUN OFF: B1-pad-hop's chute never opens either (its 2026-07-20 recording has zero Parachute* events and ends at 65 m) - B1 passes because its DOWN terminal only checks the COMMANDED latch. NOTE on the failed attempt's artifacts: run.py USED TO drive the remaining seam steps regardless of the mission outcome, so flight 1 DID perform a terminal-velocity hatch EVA after the ASSERT-FAIL (EvaExit at ~356 m / -277 m/s, kerbal chute semi-deployed at 221 m, landed alive, tree committed) - no false PASS (the run classifies INVALID(mission) before the tail and the save is re-staged per attempt), but a window-missed run's collected save/log carried a spurious EVA branch + landing and could burn ~120 + 420 s of deferral budget. FIXED harness-side 2026-07-25 (see the M-A5 row): an UNMET mission step now drives the CLEANUP tail only (StopRecording + FlushAndQuit), so this scenario's EvaExit / EvaChuteDeploy / CommitTree are skipped on a window-missed attempt ; LIVE-PROVEN 2026-07-24 (flight 2 FULL PASS, all seven verifiers: canopy observed Deployed, handoff 1,606 m / -23.2 m/s, kerbal chuted descent steady -4.5 m/s, ParachuteCut at touchdown, down=true situation=LANDED alive=true. All four pins closed: P1 count PINNED 3, P2 `'kerbalEVA` token confirmed, P3 semi-deployed rate MEASURED at about -236 m/s peak with the whole DESCENT phase 61.6 s -> descentTimeoutSeconds trimmed 480 -> 240 (~3.9x margin; step/runtime budgets deliberately left at 900/1920 as wall-clock envelopes), P4 kerbal lands alive. Post-live hardenings: K=2 EVA-window debounce and the RAW-alive CompleteOk conjunct) ; DE-LISTED from live-proven 2026-07-25 by the first full sweep and FIXED HEADLESSLY 2026-07-26 (branch `eva4-failopen`), then RE-PROVEN 2026-07-26 (flight 4, PASS on attempt 1, wall 409 s, all seven verifiers: apoapsisWindow 19696.874, evaWindowReached 1592.752, evaWindowDescentRate -18.560, craftCanopyObserved 11964.692, missionOutcome PASS gating=2, expectations mismatches=0). FLIGHT 3 red'd `PARSEK-FAIL(expectations)` at 187 s wall: the kerbal's canopy went SemiDeployed at 1,650 m and Cut 200 ms later, and the kerbal accelerated -11 -> -109 m/s into the ground. TWO defects, both diagnosed from the archive with no new flight. (b) THE CUT: not a parachute decision at all - `On_stumble` is registered on `st_semi_deployed_parachute` (KerbalEVA.cs:8153) with `GoToStateOnEvent = st_ragdoll`, is fired only from the collision callback above `stumbleThreshold = 3.5` m/s (KerbalEVA.cs:12700), and `OnSemiDeployedParachuteModeLeft` calls `evaChute.CutParachute()` on every exit but a full-deploy transition (KerbalEVA.cs:11152-11169). The collected log's ONE `Event Stumble not assigned to state Ragdoll` line, 16 ms after the cut, is the second frame of that contact. The collider is MEASURED, not inferred (corrected in panel review): the kerbal's own `.prec` carries a pod-anchored `Relative` section whose anchor-local metres put it 0.82 m from the pod at `ParachuteSemiDeployed` and 1.50 m at the cut. ALSO CORRECTED: the first draft blamed the LENGTH of the semi-deployed window, but `OnFullyDeployedParachuteModeLeft` (KerbalEVA.cs:11219) cuts UNCONDITIONALLY and `On_stumble` is registered on the full-deploy state too, so a full canopy is equally exposed and the `deployAltitude` knob would NOT have fixed this - the operative variable is PROXIMITY AT CANOPY TIME. FIX: bounded OBSERVED standoff on `EvaExit` (`minStandoffMeters=30`, 2-poll debounce, and TWO non-fatal bounds - 8 s wall clock plus `standoffFloorAltMeters=500`; the wall-clock-only first draft was sized on a 6.2x-wrong altitude figure and would have flown a low handoff into the ground with the canopy never armed). (a) THE FAIL-OPEN: the mission returned MISSION-OK over the dead kerbal, and NO mission assertion could ever have caught it - the machine's terminal is the handoff and the subprocess exits before `EvaExit` creates the kerbal vessel. The observed channel that DID see it (`eva-chute-kerbal-lost`) was recorded as `driver.steps[6].verdict=ERROR` and consulted by nothing; `driverValidity` reported PASS beside `allExpectedMet: false`. FIX: `SEAM_VERB_POST_MISSION_ROLE` + the `missionOutcome` verifier row + `PARSEK-FAIL(mission-outcome)`, plus an mlib handoff declaration so MISSION-OK states what it did not verify) |

### In-game batch wiring H7-H20 + H22 + H23, all 16 LIVE-PROVEN (16)

The gap these close: Parsek ships 542 in-game runtime tests across 98 categories, and
before this group committed specs drove EIGHT of them. The other 90 were written,
passed when an operator pressed Ctrl+Shift+T, and never executed in any unattended
run. Full enumeration, the A/B/C triage behind which 14 were picked, and the
fly order live in
[`autotest-ingame-category-inventory.md`](autotest-ingame-category-inventory.md).

All 16 are batch-only specs on the S1.4 / H6 shape: LoadGame the committed
`gloops-airshow` host, pin `autoRecordOnLaunch` false, one `RunTests` step naming one
category, FlushAndQuit. Fourteen of them (`H7`-`H20`) shipped as one wave; `H22`
joined afterward, arriving with the Basic/Advanced UI-mode feature, and flew
separately. `H23` joined 2026-07-30 with R12 and is the ONE that breaks the shape in
a load-bearing way: its LoadGame carries `scene = "trackstation"`, so it is the first
committed spec whose batch runs anywhere other than FLIGHT or the KSC. Counting `H21`'s isolated `SceneExitMerge` alongside them, driven
categories go 8 -> 25 of 98. Declarations inside a driven category go 125 -> 216 of
542, and the subset that actually EXECUTES (surviving both runner filters at the scene
each spec drives) goes 103 -> 193. Of the 89 declarations this group adds, 88 execute -
H23's single `AllowBatchExecution=false` declaration is the one exception; the rest of
the 23-declaration gap sits in the eight pre-existing driven categories, over half of it at FLIGHT rather than
SPACECENTER (`GhostMap` alone is 9) and 4 more of it `AllowBatchExecution=false` rather
than scene-skipped. Per-category decomposition is in the inventory doc.

EVIDENCE STANDING FOR THE WHOLE GROUP: **ALL 14 OF THE WAVE FLOWN 2026-07-27**
(`python run.py --tag ingame-batch`, against a DLL built and provisioned from the
branch). All 14 PASS on attempt 1, every verifier PASS or SKIPPED,
`batchComplete found=True failed=0 perCategory=1` on each, **805 s (13.4 min) wall for
the whole wave**, 49-71 s per scenario. `H22` was not part of that sweep - it arrived
later with the Basic/Advanced UI-mode feature and flew on its own on 2026-07-28 (FULL
PASS attempt 1, 53 s, under its pre-rename id `H7-ui-complexity-mode`).

WHAT THE FLIGHTS ADDED OVER THE PRE-FLIGHT DERIVATION, precisely, because the two are
easy to conflate. The `total=` values were already statically derivable from the
`[InGameTest]` attributes and gated by `CommittedBatchTallySourceSyncTests`; flying
proves nothing new about them. What flying measured is the **passed / skipped
SPLITS**, which no static analysis predicts because a run-time `InGameAssert.Skip`
can move them at any time. Thirteen specs pin those splits as LITERAL patterns, and
`evaluate_expectations` requires a required pattern to match, so a PASS means the
runner printed the pinned line TOKEN FOR TOKEN. All thirteen pre-flight derivations
were correct.

`H20` was the one member the sweep could not settle, and it is now CLOSED. Its pin was
the loose interim form, so the sweep's PASS proved only `passed >= 1`, and the exact
split survived in no artifact (collect-logs fires on non-PASS only, and the instance
KSP.log was overwritten by the later scenarios in the same sweep - the identical
evidence gap S1.4 documented). It was re-flown ALONE on 2026-07-27 so its log would
survive, measured `total=2 passed=2 failed=0 skipped=0` (corroborated by that run's
parsek-test-results.txt export: `captured=2 Passed=2 Failed=0 Skipped=0`), and is now
pinned whole. **All 15 members of the group pin their tally whole, and every one of
those tallies has been measured off a live batch.**

One asymmetry worth keeping in view: for 13 of the 15, `skipped=0` is also DERIVABLE
from the attributes plus a reachable-Skip scan, so the pin is re-derivable after a
source change. For `H20` it is measured only - both its cells carry run-time Skip
guards, and the walkback cell's endpoint-overlap probe is a live `Physics.OverlapBox`
whose outcome depends on the host's collider geometry. A fixture change to a taller or
elevated host can legitimately make it skip and red the pin as `passed=1 skipped=1`;
that is a FIXTURE change, not a walkback regression. `H22` is in the same position for
the same kind of reason: all three of its cells carry run-time Skip guards (no live
ParsekUI, Gloops recording in progress), so its `skipped=0` is a FIXTURE claim about
`gloops-airshow` that the 2026-07-28 run measured, not an attribute derivation.

COST, and the estimate that was wrong: this runbook estimated ~4-10 min per scenario
and ~80 min for the group, extrapolating from mission-flying scenarios. A batch-only
scenario is dominated by KSP boot plus save load, not by the batch, so the real figure
is 49-71 s each (`H22` measured 53 s in the same band). For scale, `B13` alone is a
measured 2,825 s - the whole H7-H20 wave costs under a third of one landing mission.

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| H7-trajectory-math | nightly | Sampling predicate + quaternion helpers against LIVE Unity arithmetic; `ShouldRecordPoint` against the density preset the running game loaded (D2 density-presets/threshold-debounce) | LIVE-PROVEN 2026-07-27, 49 s: `total=8 passed=8 failed=0 skipped=0 category=TrajectoryMath scene=FLIGHT` matched token for token |
| H8-spawn-rotation | nightly | The srfRelRotation-vs-world-rotation spawn-node contract and terminal-pose frame preference, as PURE arithmetic over fabricated quaternions - the review round withdrew the live-Kerbin/Mun claim and the D14 kerbin/mun tokens with it (D13 surface-orbit-reseed only) | LIVE-PROVEN 2026-07-27, 49 s, matched token for token: `total=10 passed=10 failed=0 skipped=0`. The scene inference is load-bearing here (all 10 are FLIGHT-scoped) |
| H9-incomplete-ballistic | nightly | Ballistic tail extrapolation through atmosphere/terrain/SOI, patched-conic snapshot integration, extrapolated-segment map line (D1 ballistic-extrapolation/scene-exit-finalization) | LIVE-PROVEN 2026-07-27, 49 s, matched token for token: `total=8 passed=8 failed=0 skipped=0` |
| H10-finalize-backfill | nightly | Terminal-orbit backfill from OrbitSegment, no-overwrite guards, and the four stale-cached-tuple endpoint realignments (D1 finalization-cache) | LIVE-PROVEN 2026-07-27, 56 s, matched token for token: `total=7 passed=7 failed=0 skipped=0` |
| H11-pipeline-anchor | nightly | Anchor epsilon vs recorded geometric offset across all seven anchor situations. Only 1 of the 7 cells resolves through a live body; 4 install a constant-returning stub resolver, so the review round withdrew the D3 claims (D14 only) | LIVE-PROVEN 2026-07-27, 55 s, matched token for token: `total=7 passed=7 failed=0 skipped=0`. Seven frame-yielding coroutines, so this is the first budget to re-time |
| H12-switch-segment | nightly | The Map Switch-To arming PREFIX gate across focus modes + intent arm/clear with no marker leak (D1 switch-segment) | LIVE-PROVEN 2026-07-27, 70 s, matched token for token: `total=6 passed=6 failed=0 skipped=0` |
| H13-ksp-api-smoke | nightly | FIXTURE CANARY: loaded-scene / Kerbin / PartLoader / root-path sanity plus `ActiveVesselExists` and `FlightCameraExists`, the only POSITIVE proof that the fixture took the Focusable/FLIGHT route the other 13 infer (D14 only - no Parsek behavior is under test) | LIVE-PROVEN 2026-07-27, 50 s, matched token for token: `total=6 passed=6 failed=0 skipped=0`. FLY THIS FIRST: a wrong route reds here as `passed=4 skipped=2` naming the real scene, before 13 other runs are wasted |
| H14-corpus-data-health | nightly | Body names, OrbitSegment bodies, PartLoader part resolution and time ranges across all 272 injected recordings (D16 sidecar-prec/schema-gate) | LIVE-PROVEN 2026-07-27, 71 s, matched token for token: `total=4 passed=4 failed=0 skipped=0`, plus `recordings.count = 272` which is the real anti-vacuity guard (see the fourth-trap note below) |
| H15-corpus-ghost-visuals | nightly | A ghost mesh built from EVERY recording in the corpus; every stored part name resolved through the live PartLoader (D16 sidecar-craft/sidecar-pcrf) | LIVE-PROVEN 2026-07-27, 70 s, matched token for token: `total=4 passed=4 failed=0 skipped=0` + count 272. Heaviest batch in the group; the one most likely to need a budget bump |
| H16-corpus-spawn-health | nightly | Stuck `SpawnAbandoned` and out-of-bounds `SpawnDeathCount` across the corpus (D13 three-cycle-abandon) | LIVE-PROVEN 2026-07-27, 69 s, matched token for token: `total=3 passed=3 failed=0 skipped=0` + count 272. One of its three cells (`SpawnedPidConsistency`) is INERT over this corpus and the spec says so |
| H17-flight-integration | nightly | Recorded lat/lon/alt vs `GetWorldSurfacePosition` over the corpus, ParsekFlight liveness, active-vessel surface API, Harmony patch operational (D3 surface-body-fixed; D16 sidecar-prec) | LIVE-PROVEN 2026-07-27, 69 s, matched token for token: `total=4 passed=4 failed=0 skipped=0` + count 272. Its first cell bails SILENTLY over an empty store, so the count pin is what makes it mean anything |
| H18-pipeline-smoothing | daily | Coast-jitter suppression, structural-event flag alignment and child-seed parity, and the LIVE GameEvents subscription contract - a dropped `GameEvents.X.Add(...)` compiles, unit-tests green, and silently stops recording docks (D2 structural-event-snapshots) | LIVE-PROVEN 2026-07-27, 50 s, matched token for token: `total=4 passed=4 failed=0 skipped=0`, plus the `asserted=5 of 5 GameEvents bindings` line. One caveat stated in the spec: the wiring helper's only Skip branch is a KSP field rename, unreachable on 1.12.5 |
| H19-recording-finalization | nightly | BackgroundRecorder finalization-cache apply: destroyed-cache tail trim at the deletion UT, stable-cache Orbiting finalization, active-crash tail append (D1 finalization-cache) | LIVE-PROVEN 2026-07-27, 49 s, matched token for token: `total=3 passed=3 failed=0 skipped=0` |
| H20-eva-spawn-position | nightly | EVA spawn within 10 m of the recorded endpoint and at least 50 m off the parent; trajectory walkback when the endpoint overlaps (D13 terrain-correction/trajectory-walkback) | LIVE-PROVEN 2026-07-27, 49 s in the sweep + a 59 s solo re-fly to capture the split. Pinned WHOLE: `total=2 passed=2 failed=0 skipped=0`. The overlap probe DID fire, so the walkback path really executed. Its `skipped=0` is measured, not derivable - see the group note |
| H22-ui-complexity-mode | daily | The LIVE `InputLockManager`, which headless xUnit structurally cannot reach: entering Basic must force-close every gated window AND leave no Parsek control lock held (design 7.2 / section 8 edge case 2 - a leaked lock soft-locks the player's mouse for the rest of the scene session), a Basic round trip must preserve gated-window state, and Advanced must restore every `UiSurface`. Every mode flip goes through the `ParsekUI.SetUiComplexityMode` seam and waits on the DEFERRED latch, so the run also proves the real `ParsekFlight.Update` apply wiring exists - the two `Mode changed: uiComplexityMode=` contract lines are PRODUCTION emissions, not test echoes (D14 sandbox/scene-flight only - NO NEW REGISTRY VALUE; D15 UI surfaces deliberately NOT claimed, because it carries exactly one value and `test_real_registry_denominator` pins that count, so growing it is a separate reviewed decision) | LIVE-PROVEN 2026-07-28, 53 s, flown under its pre-rename id `H7-ui-complexity-mode` (runId `2026-07-28_1808_H7-ui-complexity-mode`; renamed to H22 in the PR #1370 merge because main's batch-wiring wave took H7-H20). FULL PASS on attempt 1, all six verifiers green (driverValidity, batchComplete perCategory=1, analyzer red=0, logValidate recRulesSuppressed=True, anomalySweep hits=[], expectations mismatches=0). Matched token for token: `BATCH_COMPLETE v1 total=3 passed=3 failed=0 skipped=0 category=UiComplexityMode scene=FLIGHT` - all three `InGameAssert.Skip` guards (no live ParsekUI, Gloops recording in progress) were indeed unreachable on the gloops-airshow fixture, and the FLIGHT LoadGame route inferred from it is confirmed. Measured alongside: `opened 6 gated surfaces, locksHeldBeforeSwitch=0`, `12 surfaces visible in Advanced after the round trip`, `round trip preserved Career tab=3`, zero `[Parsek][ERROR]` lines. Adding a test to the category moves BOTH numbers in the spec, same commit. EXTENDED 2026-08-01 to total=4: `MissionRevealScrollsToTheTargetMission` (`InGameTests/MissionRevealInGameTests.cs`) covers the Timeline GoTo cross-link's mission-reveal SCROLL, whose capture reads real `GUILayoutUtility` rects and so was unreachable headless - it shipped in PR #1401 on manual playtest alone. It guards the unsolved-layout-cache defect that class of code fails silently with: a Repaint painted against a layout cache that was never solved reads zeros, consumes the target, lands the list at offset 0 and logs it as a success. The test SEEDS ITS OWN two synthetic trees through the non-flushing `AddCommittedTreeInternal` (this fixture injects no recordings, so a skip-if-absent test would verify nothing on the very run meant to verify it) and removes them in a finally, writing nothing to disk. Its assertion is sort- and size-independent: two different missions must capture two DIFFERENT offsets, which the defect collapses to a shared 0. Spec tally, `test_hlib` row, and the category-inventory doc all moved in the same commit. LIVE-PROVEN 2026-08-01 (runId `2026-08-01_1435_H22-ui-complexity-mode`, FULL PASS, 66 s, every verifier green, unityExceptions total=0): measured `BATCH_COMPLETE v1 total=4 passed=4 failed=0 skipped=0` and `MissionReveal: captured distinct offsets A=0.0px B=52.0px fromClosed=52.0px` (re-flown 2026-08-01_1449 after the test was extended). The FIRST attempt (`2026-08-01_1432`) red'd `expectations mismatches=2` with the test SKIPPED - every window draw in `ParsekFlight.OnGUI` sits inside `if (showUI)` and nothing clicks the toolbar unattended, so the Missions tab never drew and never seeded the probe missions. Fixed by raising `ParsekFlight.ShowUIForTesting` (new seam) for the duration of the test. That is exactly the class of gap only a live flight finds: the other three tests in the category pass without any draw at all, because they only flip window flags and the mode. The measured `A=0.0px` also vindicates the sort-independent DIFFER assertion - mission A is legitimately the first row, so a `> 0` assertion on a single mission would have false-red'd. SCOPE, stated because the test's name implies more: it proves the capture measures real DISTINCT content-space rects end to end, and that the `missionsLayoutFrame` guard does not OVER-suppress (an over-strict guard starves both captures to NaN and reds). It does NOT regression-guard the mid-OnGUI unsolved-cache defect that motivated the guard - a coroutine resumes BETWEEN frames and so cannot open a window mid-OnGUI, and deleting the guard leaves the steady-state cases green. The third case reveals from a CLOSED window, the closest reachable approximation (the reveal's own force-open means the capture must survive a window that did not exist last frame), and pins that it lands on the same row the open-window reveal did - `fromClosed=52.0px` against `B=52.0px` |
| H23-tracking-station | daily | The 10-test `TrackingStation` category, stranded since it was written: 9 batch-eligible tests that no driven run could reach, because `DecideLoadRoute` had exactly two routes and no seam verb changed scenes. Covers the TS scene host (`ParsekTrackingStation` + stock `SpaceTracking` + `MapView.fetch` + `flightState`), the span-clock TS seam including the zero-drift scheduled variant, the synthetic-ghost `ProtoVessel` lifecycle and Fly-strip, and the map/TS render tracer's LIVE Vectrosity line truth - which is Unity-runtime by nature and is exactly what `MapRenderProbe.ReadLineActive` regressed on once already (D14 sandbox + scene-ts, the latter a NEW registry value; no other value claimed - the run boots and batches, it does not record) | LIVE-PROVEN 2026-07-30, run `2026-07-30_1522_H23-tracking-station`, PASS attempt 1, 44 s, every verifier PASS/SKIPPED. Tally MEASURED on a first flight flown deliberately with the interim `passed=[1-9][0-9]*` pin, then re-pinned WHOLE and re-flown green: `BATCH_COMPLETE v1 total=10 passed=9 failed=0 skipped=1 category=TrackingStation scene=TRACKSTATION`. total/skipped agree with the attributes (one member carries `AllowBatchExecution = false`); passed=9 could NOT be derived, because three members carry runtime `InGameAssert.Skip` guards keyed on whether KSP built a Vectrosity orbit line for a synthetic ghost that session - all three were satisfied. `scene=TRACKSTATION` inside the tally is the B10 fail-open guard and is the point of the cell: every member is TRACKSTATION-scoped, so a silently-wrong-scene boot reports `total=10 passed=0 skipped=10` and a bare `failed=0` would read GREEN over zero executed tests. The fixture is the FLIGHT-route `gloops-airshow` host on purpose, so a TRACKSTATION landing can only mean the requested scene overrode the save-derived route. Observed once and not gated: an INTERMITTENT stock `SpaceTracking.buildVesselsList` NRE during synthetic-ghost teardown (2 raw Unity exceptions on the measuring flight, ZERO on the pinned one) - see gate 13 |

### In-game ISOLATED batch wiring, R5 (1)

The R5 unlock. `InGameTestRunner` has always had a second batch entry point
(`RunCategoryIncludingFlightRestore`) admitting
`AllowBatchExecution || RestoreBatchFlightBaselineAfterExecution` and restoring a
quickloaded flight baseline after each test. It was public, fully implemented, and
called from exactly two places, both INTERACTIVE - so 68 already-written tests were
unreachable by any unattended path. R5 added the seam arg `isolated = "true"`, the
autorun mirror `PARSEK_AUTORUN_ISOLATED=1`, and the hlib companion
(`InGameTestDecl.restore_baseline`, `derive_batch_tally(isolated=)`,
`spec_batch_isolated`, and the fail-closed spec validation). This spec is its
shakedown; the other 12 unlocked categories are now spec-authoring work under
R6 / R7 / R10 rather than blocked.

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| H21-scene-exit-merge-isolated | nightly | A real recording, a real launch, a real stock save-and-exit out of FLIGHT, and both branches of the pre-transition merge dialog - D1 commit-scene-exit + discard-rollback, EXECUTED rather than decided. The two `SceneExitMerge` cells are `AllowBatchExecution = false` + `RestoreBatchFlightBaselineAfterExecution = true`, so the ordinary path runs ZERO of them | LIVE-PROVEN 2026-07-27, 101 s, PASS attempt 1: `total=2 passed=2 failed=0 skipped=0 category=SceneExitMerge scene=FLIGHT` matched token for token, alongside both isolated-path-only tokens. The batch itself was 29.6 s. Both open questions came back favourable: the launcher clears 80 m inside the 30 s deadline, and the post-test quickload returns the vessel to FLIGHT in PRELAUNCH so test A's guard does not fire. Not a degenerate pass - the log carries the full sequence including `User chose: Tree Discard` and `User chose: Tree Merge` |

TIER: `nightly`. An isolated batch is a real quickload per test, so these two cost
three full FLIGHT scene reloads on a 73-part craft plus two ascents; budget is 1200 s
against the H7-H20 group's 49-71 s. MEASURED at 101 s wall (29.6 s of it the batch),
so the cost is far lower than feared and the 1200 s budget is now generous rather
than tight. Promotion to `daily` is defensible on cost; left at `nightly` until a
second run establishes it is not a one-off, since a quickload-per-test batch has
more ways to be slow than a pure-arithmetic one.

FIXTURE, and it is the expensive part of this spec: `b2-lko-craft`, NOT the H7-H20
`gloops-airshow`. That save's active vessel is a 1-part `mk1-capsule` with ZERO
`ModuleEngines`; both cells stage and then wait to leave PRELAUNCH and clear 80 m, so
on it they would BOTH self-skip and print `total=2 passed=0 failed=0 skipped=2` -
numerically identical to the non-isolated failure the spec exists to rule out.
`IsolatedBatchWiringGroupTests::test_the_fixture_can_actually_fly_the_category` now
gates the PRELAUNCH + non-zero-engine property statically so this cannot regress
silently.

TIERING, decided on the measured cost: **13 stay `nightly`, `H18-pipeline-smoothing`
is promoted to `daily`.** Cost does not discriminate here - every member is 49-71 s and
the whole group is 805 s against 2,825 s for B13 alone - so the promotion is decided on
FAILURE MODE instead. H18 is the only guard anywhere in the suite for the live
GameEvents subscription contract, and a dropped `GameEvents.X.Add(...)` is SILENT: it
compiles, every xUnit cell stays green, and Parsek stops recording docks / undocks /
EVAs from that moment with nothing else to catch it. A day of latency on that is worth
50 s a day. The other 13 have flown exactly ONCE, and the house convention (see the EVA
rows above) is to promote on flake data rather than on a single green; revisit after
about three nightly runs. Note H18's promotion is narrower than it looks: the flight
also showed that cell is commanded-vs-observed for one of its five bindings, so what
runs daily guards FOUR production subscriptions, not five.

Two findings from building this group, both with full detail in the inventory doc:

- **The FUTURE recommendation was half-wrong, and the data says why.** The 2026-07-25
  EVA decision recommended a dedicated batch-only scenario over the injected corpus
  for `EvaSpawnPosition` AND `CrewReservationLive`. `EvaSpawnPosition` shipped as H20.
  `CrewReservationLive` CANNOT ship that way: both its cells short-circuit on
  `spawnedCount == 0`, and the corpus injector
  (`SyntheticRecordingTests.CleanSaveStart` -> `RemoveSpawnedPidLines`) strips every
  `spawnedPid = ` line out of the staged save by construction, so the corpus is
  guaranteed to contain zero spawned-endpoint recordings and the batch would emit
  exactly the vacuous `total=2 passed=0 failed=0 skipped=2`. Unblocking it is a C#
  corpus-writer change, and the same change makes H16's inert third cell meaningful.
- **A FOURTH trap, invisible to every existing defence.** `batch_contract_vacuity_gap`
  catches `passed == 0`. It cannot catch a test that RUNS, PASSES and asserts over
  nothing - a store walk counting violations passes on zero items, and several tests
  bail early with a silent `return` / `yield break` plus a Verbose log INSTEAD of an
  `InGameAssert.Skip`, so they are reported PASSED rather than Skipped. Only the
  FIXTURE defends against it, which is why the four corpus-backed members pin
  `recordings.count = 272`.

A three-reviewer panel ran against this branch before merge: 26 findings, no
blockers. Four changed what the specs CLAIM rather than what they do, and all four
are corrected here. H18's GameEvents cell is commanded-vs-observed for one of its
five bindings (the test seeds `onPartJointBreak` itself via `SubscribePartEvents`,
so that one fails open); H11 and H8 both claimed live CelestialBody work they do not
perform, and their D3 / D14 claims are withdrawn; H20's interim pin named the one
guard that IS derivable (`WalkbackFixtureCoversParent`, true with about 913 m of
margin) instead of the live `Physics.OverlapBox` that is not; and the `spawnedPid`
mechanism cited here and in the todo doc was wrong - `CleanSaveStart` runs before the
corpus writer injects and does not run on the harness path at all, the real
invariants being that `RecordingBuilder.WithSpawnedPid` has zero callers and
`AddRealCareerRecordings` injects `RECORDING_TREE` nodes only. That last one was an
active trap: "making the stated mechanism real" by adding `-CleanStart` would strip
the fixture's VESSEL nodes and route every corpus spec to SPACECENTER. Also fixed:
the new test class had no anti-vacuity floor (emptying its GROUP table left all eight
cells passing over zero specs), its membership guard compared an intersection so a
new spec was invisible to it, and H15's budget is raised to 1200 s because its
failure mode is a KILLED run that produces no tally to re-derive from.

A harness bug was found and fixed on the way: `hlib._pin_literal_word`'s character
class excluded `-`, so all seven hyphenated categories (`Pipeline-Anchor`,
`Pipeline-Smoothing`, `Pipeline-Frame`, `Pipeline-Outlier`, `Pipeline-Terrain`,
`Pipeline-AnchorPropagate`, `Pipeline-Anchor-BubbleEntry`) were structurally
unpinnable - `statically_checkable` went False and the sync sweep rejected the spec
with a message blaming the author. The runtime parser never had the gap (`_BATCH_RE`
reads a non-space category token), so this was a static-path-only disagreement with
the line the game actually prints.

## Mission-machine trust layer

The shared flyby machine (mlib) was hardened by 19+ live findings so that a
mission FAILURE is attributable to Parsek or the contract - never to
autopilot noise. Capabilities (all live-proven): native warp-to-UT with
zombie-safe cancel + asymmetric retargeting; certified no-1x coast
(`harness/warp_audit.py --fail-on-violation`, contiguous + cumulative);
flameout staging under both the DIY burner and the throttle-collapsing
MechJeb executor; bounded correction give-ups with warp-time-excluded
clocks; closed-loop arrival quality (patched-conic next_pe telemetry,
no-encounter creation, impact-certain early terminal); planner-bias margin
targets (finding 16d); 20+ telemetry channels + machine-state/gate-evidence
lines + live status CLI (`harness/status.py`). Full forensics per finding:
`todo-and-known-bugs.md`.

## Verification layers (all active)

- Headless: 1,013 mission-machine + 836 harness + 203 provisioner unittest
  cells; 18,669 xUnit on the C# side (18,668 passed + 1 skipped: analyzer,
  seam, log contracts, the new route-window delta formatter). The three Python
  figures were re-measured 2026-07-28 on `fix-park-warp-teardown` (branched
  from `origin/main` at 861e6fbfe, which carries the merge wave the previous
  note tracked); that branch adds 6 of the 1,013 mission-machine cells and the
  rest of the 918 -> 1,013 / 768 -> 836 movement is inherited, not new here.
  The xUnit figure is CARRIED FORWARD, not re-measured - this branch touches no
  C#. Previously re-measured
  2026-07-26 from `autotest-render-parity` AFTER its SECOND merge of
  `origin/main` (which carried `autotest-orbit-missions` ->
  `autotest-landing-missions` -> `autotest-eve-missions` ->
  `autotest-batch-coverage` -> `autotest-tally-gate`); the harness figure
  moved 664 -> 726 because that merge is the first tree holding both sides of
  those cells, then 726 -> 742 on `ingame-test-wiring` (the H7-H20 group's
  `IngameBatchWiringGroupTests`, the hyphenated-category pin cells, and the
  inventory-doc source-sync gate).
  This line inherits SILENTLY through a clean auto-merge and is wrong
  the moment either side adds a test, so re-measure rather than editing it by
  memory - `cd harness && python -m unittest discover -s missions/lib -q`
  (and `-s lib -q`, `-s provision -q`), plus
  `cd Source/Parsek.Tests && dotnet test`.
- Per-run: the verifier chain (the R9 `saveParse` row joined report-only
  2026-07-31 and is ARMED on S4.1 alone from the same date - report-only
  for every other spec; alongside driverValidity / batchComplete / analyzer /
  logValidate / testResults / anomalySweep / unityExceptions / expectations /
  ledgerOracle, plus missionOutcome on autopilot runs) + collect-logs on
  every non-PASS.
- In-game: 543 runtime tests / 98 categories (autorun-able), H5 invariants,
  log-contract tests. Counted mechanically by
  `hlib.parse_ingame_test_declarations` over `Source/Parsek`, not by hand
  (the hand-and-grep number was 534 / 96: five namespace-qualified
  declarations were invisible to both). AUTORUN-ABLE IS NOT THE SAME AS
  AUTORUN: **25 of the 98 categories are driven by a committed spec** (up from
  8 before the H7-H20 group, plus H21's isolated `SceneExitMerge`, H22's
  `UiComplexityMode` and H23's `TrackingStation`). That covers 217 of the 543
  declarations, 194 of which would actually execute; the 23-declaration gap is
  decomposed per category in the inventory doc (it is NOT simply the SPACECENTER
  categories - over half is at FLIGHT). The remaining 73 categories still run only
  when an operator presses Ctrl+Shift+T. These numbers are re-derived from, and
  must agree with, the triage totals in
  `docs/dev/autotest-ingame-category-inventory.md`, which ARE gate-checked
  (`IngameCategoryInventoryDocTests`); this bullet is prose and is not, so it had
  drifted by one whole scenario (H23, 10 declarations) before 2026-08-01.
  Per-category eligibility, the A/B/C triage of what is left, and the reason
  each un-wired category is un-wired live in
  [`autotest-ingame-category-inventory.md`](autotest-ingame-category-inventory.md);
  the split is re-derived there, never hand-counted.
- Spec-vs-source: every batch-owning spec's pinned `BATCH_COMPLETE` tally is
  cross-checked against the C# `[InGameTest]` attributes it describes
  (`CommittedBatchTallySourceSyncTests`), so adding an in-game test to a
  pinned category fails locally instead of on the next nightly. The same
  sweep asserts RECOGNITION completeness, so an attribute spelling the parse
  does not model reds instead of quietly shrinking a total.
- Findings baseline: 5 historical saves baselined; fresh harness saves run
  baseline-Forbid (structural fresh-save guard).
- Coverage ledger: 96 / 241 registry cells claimed (the growth metric),
  RE-DERIVED at each merge through `hlib.compute_coverage` over the committed
  specs + the merged registry (53 of them as of the #1357 merge) - neither side's
  number survives such a merge. `main` read
  84 / 241 over its 38 specs (83 at the 2026-07-26 recompute; D3
  `parent-anchored-debris` is the cell R1's debris gate added). This branch read
  95 / 241 over its 52. The merged figure is recomputed rather than taken from
  either, because the two changed disjoint things: main CLAIMED a new cell and
  this branch both claimed cells and WITHDREW two. The withdrawals were
  deliberate - H11's D3 tokens (four of its seven cells run with the production
  anchor resolver replaced by a constant-returning stub, so the frames it named
  are not actually exercised) and H8's D14 kerbin / mun (its "KerbinPadCase" /
  "MunCase" are fixture labels over fabricated quaternions - the file never looks
  up a CelestialBody). A withdrawn false claim is worth more than a claimed cell:
  the failure mode of over-claiming is a later audit reading a dimension as
  covered and skipping the test that would have caught the regression. The green-backed subset is the OTHER half of
  this metric and needs the run archive, which is gitignored
  (`harness/results/*.json`, `harness/coverage/coverage.{json,txt}`), so
  re-derive it on a checkout that has one; last measured 2026-07-25 at 58 of
  that day's 70. Never carry either number forward by hand.

## Run telemetry - what a live run actually shows you

A 2026-07-25 audit measured the live surface and found that every phase budget
in the system is GAME time and there was no WALL budget anywhere in it. That let
a real failure through: a B12 run died on `mission-budget-expired` after burning
57% of its wall budget in ONE phase while every displayed budget read ~7.5%
consumed. Six surfacing changes landed; none of them is a new subsystem, all
six publish or compare numbers the runner already measured.

| Change | What it fixes |
|---|---|
| WALL block in the status payload + `status.py` panel (`wallElapsedSeconds` / `wallRemainingSeconds` / `wallBudgetSeconds` / `phaseWallSeconds`) | The panel printed `wall ~N (telemetry-line est.)` with NO denominator. It now reads `mission wall: 39m39s / 1h10m (57%) \| phase wall 39m39s`, falling back to the line-count estimate for an older/stale status file. `status.py` also reads the real denominator out of `driver.steps[].budget` (it only ever looked at `[driver.missionParams]`, where the wall budget does not live) |
| `gameSecondsPerWallSecond` live + per phase | The ratio that named two shared-machine warp defects was end-of-run only with ZERO programmatic consumers. It is now a phase-history column, the OPEN phase's live `phaseWarp` block in the status payload, and a LOW marker. **Revised 2026-07-26:** ratio + wall ALONE marked only false positives (4 of 4 on healthy B11, the identical 2 on all 5 healthy B12 runs; healthy CORRECTION-BURN reads 43.6 against a defect at ~40, so the ratio cannot separate them). The marker now also requires `armedWarpCommands > 0` - we ASKED for warp and did not get it - where `ACTION_CANCEL_WARP` and `SET_RAILS_WARP(0)` do not count as arming. On healthy B11 and all 5 healthy B12 runs it fires on ZERO rows. Still INFORMATIONAL; the BROKEN case stays owned by `warp_liveness_starved` |
| `PHASE_BUDGET_KEYS` covers the ORBIT tail | The table covered 8 phases and was blind to PLAN-CAPTURE / CAPTURE-BURN / PARK / ORBIT-COMMIT, so B11's CAPTURE-BURN (642 wall s, the most expensive phase in the suite) printed `budget n/a`. All four keys plus the B1/B4/EVA-4/FORGE/B-DOCK phases are mapped, each mirroring mlib's own `_*_phase_budget`, and the four ORBIT phases have heuristic branches reading `captureExecDownStreak` / `parkStableStreak` / `nodeExec` |
| Gate-flip window dumps rate-limited | MEASURED: one B12 stdout log is 43 MB / 181,786 lines, 79.5% of it `window[NN/20]` payload from 7,218 gate-flip dumps (7,207 from the single field `gate warpToCmd`). Even a healthy B5 run spends 50% of its lines on window payload carrying 71% duplicate frames. **Revised 2026-07-26:** that whole run holds only **16 distinct `(phase, gate-field)` pairs**, so the FIRST occurrence of each pair is now admitted unconditionally (16 windows instead of 7,218 - a bigger reduction than the time rule, and no novel flip ever loses its context) and the 10 s limit (= the ring's own span) applies to REPEATS only. `phase-transition` / `terminal-*` / `vessel-lost` dumps stay unconditional, the `gate warpToCmd` line itself is untouched, and one batch-summary line per flight names how many were suppressed. `GateFlipSuppressionFlightTests` now drives the real fly loop through the suppression path (before it, removing the limit entirely and rate-limiting every reason both survived the whole suite) |
| Committed `harness/coverage/duration.json` | `flake.json` tracked outcomes and nothing tracked duration, and every artifact carrying one is gitignored. Consequence: the B12 spec claimed B11 was the SHORTER run, backwards across four measured runs each (B11 p50 1,317 s, B12 p50 627 s), unnoticed. **Revised 2026-07-26:** the first cut was DESTRUCTIVE - it recomputed the record from the gitignored per-checkout `results/` dir and truncate-wrote it, so a fresh worktree flying one scenario replaced the 24-entry file with 1 entry (observed live), and a measured scenario's `n=5` became `n=1`, disarming the warn. The ledger now stores a bounded per-scenario SAMPLE tail keyed by `endedUtc` and MERGES into the committed file (`hlib.duration_samples` + `hlib.merge_durations`), the write is tmp + `os.replace`, and an unreadable ledger SKIPS the write with an Error instead of being replaced. **Revised again 2026-07-26 (review round 2):** the committed file was still SUMMARY-ONLY - all 24 entries lacked `samples`, so every scenario would have taken `merge_durations`' BOOTSTRAP branch on the next run and neither the watermark rule nor the bounded tail was exercised by the artifact in the repo. It has been regenerated through the production `duration_samples` + `merge_durations` path over the archived `results/*.json`: 24/24 entries now carry samples, 23 keep their summary numbers to the digit and B10 picks up the second PASS it had never merged (n 1 -> 2). The B12 spec claim is corrected |
| Per-scenario retry cost + `missionWallSeconds` | B7-duna burned 794 + 776 = 1,570 s across two INVALID attempts and produced nothing, traceable only as two unrelated summary lines. Each scenario now logs `scenario cost attempts=N wallTotal=Xs terminal=Y` and carries `attemptsWallSeconds`; the result also carries the mission's own `missionWallSeconds`, so the harness-vs-mission residue is a subtraction. That residue MEASURED at a stable 40-67 s across 16 runs (KSP boot ~35 s + verifier chain ~10 s), which is why the seven individual call sites are deliberately NOT instrumented - past ~120 s is the signal to look closer |

## Known gates and latent items (forensics in todo-and-known-bugs.md)

0. ANOMALY_TOKENS has DRIFTED from what the mod raises, and the drift is a
   FAIL-OPEN. Found 2026-07-26 while anchoring the sweep. **PARTIALLY CLOSED
   2026-07-29 (branch `harness-fail-open-gates`); the ungated-reason half stays
   open and is still the operator call described below.** What changed: the DEAD
   `icon-jump` token is REMOVED from `hlib.ANOMALY_TOKENS` and retired to
   `hlib.ANOMALY_TOKENS_DEAD` (the two tuples are now disjoint, and the
   source-derived enumeration asserts a retired token is still raised by nothing,
   so one gaining a producer reds instead of quietly staying ungated). The removal
   moves NO verdict by construction - a token no producer raises can never be a
   hit - it stops the gated set advertising coverage it does not have. Also
   shipped: a per-token COUNT BUDGET on `allowedAnomalies`
   (`{ token = "...", maxCount = N }` beside the historical bare-token form, which
   all 55 committed specs keep using unchanged), so "this anomaly is benign" and
   "this anomaly fires at most N times on a healthy run" become different claims.
   NO committed spec arms a budget - `anomalySweep.hitCounts` now records the
   per-token raise counts on every run so an operator can size one from a green
   flight first. What is STILL OPEN, unchanged: `icon-teleport` (the real raise, and
   the one that most likely should be gated) remains REPORT-ONLY, because nobody
   has yet measured whether it fires on a green S1.4.
   NINE reasons are raised and ungated: `icon-teleport`,
   `icon-off-orbit`, `unaccounted-drawn-recording`, `gap-vs-retire`,
   `decision-vs-old-truth`, `clock-not-ready`, `retire-not-held`,
   `anchor-resolve-fail` and `factory-parity`. (The first version of this gate
   said FIVE. The four it missed are the cutover-hardening raises, which reach
   `EmitAnomaly` through thin `MapRenderTrace` wrappers rather than at their guard
   site in `ShadowRenderDriver` / `AnchorFrameResolver`, so they do not show up in
   a grep for `EmitAnomaly` call sites. They emit the same `phase=Anomaly ...
   reason=<token>` line as any direct raise. The list is now DERIVED FROM SOURCE
   by `AnomalyGroundTruthEnumerationTests` and pinned in
   `hlib.ANOMALY_REASONS_RAISED_UNGATED`, so the count cannot drift silently
   again.) Deliberately NOT resolved in the same change: the call is per token -
   some are coverage instruments rather than defect signals
   (`unaccounted-drawn-recording` is the S0 polyline-coverage probe;
   `factory-parity` is a shadow comparator that never drives a draw), and the one
   that most likely SHOULD be gated (`icon-teleport`) would widen S1.4 without
   anyone knowing whether it fires there - every S1.4 flight predates the
   `unlistedReasons` channel, so its next nightly is the measurement that should
   decide the rename. NOTE what the deferral is not:
   an earlier wording said widening "moves verdicts on every committed scenario",
   which stopped being true in this same change - only S1.4, S1.6 and S1.7 arm the
   map tracer now, so only those three could move. Interim:
   `hlib.unlisted_anomaly_reasons` REPORTS every raised reason absent from the set,
   run.py warn-logs it and records it in the result JSON as
   `anomalySweep.unlistedReasons` - non-gating, but the drift is now visible on
   every run instead of silent. Pinned by
   `AnomalyGrepAnchoringTests.test_icon_jump_is_retired_and_icon_teleport_is_still_only_reported`
   plus `AnomalyGroundTruthEnumerationTests`, and the budget surface by
   `AnomalyBudgetParseTests` / `AnomalyBudgetSweepTests` / `AnomalyTokenCountTests`.
   OPERATOR-BLOCKED REMAINDER: (a) the per-token gate-vs-instrument call for the
   nine ungated reasons, which wants S1.4's next nightly `unlistedReasons` reading;
   (b) arming any `maxCount`, which wants a green run's `hitCounts`.

   **FIRST REAL-GEOMETRY BASELINE (2026-07-30, V1-map-dwell-mun-orbit; the
   first runs ever whose probe sampled ghosts over a sustained window):** both
   flights (runs `2026-07-30_1955` / `2026-07-30_2023`, 130 nonzero-ghost probe
   frames each) read `hitCounts={line-blink: 2}` and `unlistedReasons=[]` -
   deterministic 2x per dwell, raised at ownership-handoff frames under the
   100x ramp step (forensics: todo `V1-REPLAY-LINE-BLINK`), and the NINE
   ungated reasons all stayed silent. That is the first actual evidence toward
   (a) - real geometry across time raises none of the nine - and toward (b) a
   `line-blink maxCount=2` budget on V1 IF the blink is ruled benign; do not
   arm before that diagnosis. The zeros here are MEANINGFUL, unlike the
   historical-corpus sweep's: these runs demonstrably exercised the path
   (`sampled>0` on 130 summaries per flight).

   **HISTORICAL-CORPUS SWEEP (2026-07-29): the archive cannot size these budgets,
   and reading its zeros as "measured zero" would be a false calibration.** All 137
   `KSP.log`s in `logs/` at the time of the sweep were run through
   `hlib.count_anomaly_tokens` / `unlisted_anomaly_reasons`: ZERO `phase=Anomaly`
   lines, corpus-wide. That number is NOT evidence the raises are rare, because the
   Tier-C path was barely exercised.
   116 runs did enable `mapRenderTracing` and emitted real trace output (up to 1203
   `[MapRenderTrace]` lines in one BDOCK run), but the flown B-specs never put a
   ghost in front of the probe at all - `BDOCK-1` logged 1200 `probe frame summary`
   lines every one of which reads `ghosts=0 sampled=0`, and `B6` / `B4` are the same.
   The ONLY runs where the probe sampled a ghost are `S1.6-render-parity` and
   `S1.7-maprender-parity`, at `maxGhosts=1 maxSampled=1` across 3 rate-limited
   summaries each - far too thin to size a count budget from. **`S1.4` has never been
   collected at all** (no S1.4 folder exists in `logs/`), so the spec the deferral
   names as the deciding measurement has no historical reading whatsoever. Practical
   consequence for the next nightly: before reading any `hitCounts` as a frequency,
   confirm the run actually exercised the path - grep the collected log for
   `probe frame summary` and require `sampled>0` on a meaningful number of them. A
   `hitCounts` of zero from a run whose probe never sampled a ghost means the same
   thing this sweep does: nothing. Do NOT set `maxCount = 0` off such a run;
   that converts an unexercised path into a gate that reds the first time it works.
1. B6 20 km / B7 300 km course-correct targets - see the test-case table.
2. Runner-only kRPC behaviors are LIVE-VERIFIED ONLY (no headless guard can
   exercise MechJeb server state): intercept-only planner flags, executor
   abort-before-native-AP, deceleration_time override, Smart A.S.S. off.
   Their symptom signatures are the first triage suspects on recurrence.
3. STOCK_AWARD_PATTERNS were dead against real KSP logs: the ledger-oracle
   capture cross-check was a structural no-op. **MECHANISM CLOSED 2026-07-29
   (branch `harness-fail-open-gates`); ARMING is operator-blocked.** The patterns
   are rewritten from MEASURED lines - the CL-1 flights' `Added -9.999828 (-10)
   reputation: 'VesselLoss'.` / `Added 0.9999995 (1) reputation: 'Progression'.`,
   i.e. the real `Added <appliedDelta> (<nominal>) reputation: '<reason>'` idiom -
   replacing the invented `funds=` / `delta=` keyed forms that no KSP build emits.
   A captured award now carries the stock `TransactionReasons` key
   (`CapturedAward.reason`), which is the only identity a stock award line has. The
   BALANCE-inadmissibility rule is unchanged.

   **SECOND PASS, SAME DAY (branch `harness-gate-calibration`): the FUNDS half of
   that rewrite was itself composed, not measured, and is now RETIRED. THE CAPTURE
   IS REPUTATION-ONLY, permanently.** `Added 4800 funds: 'RecordsSpeed'` never
   appeared in any KSP.log; its cited source quotes Parsek's OWN
   `[Parsek][INFO][GameStateRecorder] Game state: MilestoneAchieved ... funds=4800`
   line, which the capture skips as `[Parsek]`-tagged. So the rewrite reproduced,
   on the funds facet, the exact defect it closed on the others. Three independent
   proofs that KSP logs no funds and no science award, all reproducible:
   (1) UTF-16LE literal counts in `Assembly-CSharp.dll`: `") reputation: '"` = 1,
   `" funds: '"` = **0**, `" science: '"` = **0** (a concatenated `Debug.Log` keeps
   its format fragment as one literal, so zero is conclusive);
   (2) decompiled `Funding.AddFunds` and `ResearchAndDevelopment.AddScience` mutate
   the pool and fire GameEvents with NO `Debug.Log` of any kind - only `Reputation`
   logs; (3) 137 collected KSP.logs, including a career run that credited +4800
   `RecordsSpeed` milestone funds (`logs/2026-07-10_2339_rerun4-green`), contain
   ZERO stock funds/science award lines and exactly the rep lines above.
   The funds pattern moves to `hlib.STOCK_AWARD_PATTERNS_DEAD` (same call, same
   reasoning, as the `icon-jump` retirement), pinned by
   `StockAwardCaptureTests.test_retired_funds_shape_is_not_captured` /
   `test_live_and_dead_pattern_tables_are_disjoint`, so a future build that DOES log
   funds reds instead of quietly re-opening the facet. SCIENCE IS CLOSED NEGATIVELY:
   there is no shape to measure, so no science pattern will ever be added; the only
   R&D line KSP writes is the DATA line `[Research & Development]: +<n> data on
   <subject>. Subject value is <v>`, which is not a currency delta and stays
   inadmissible. Pinned by
   `test_no_science_award_line_exists_in_ksp_so_none_is_enumerated`.
   TWO KNOWN CAPTURE GAPS, both real KSP lines deliberately unmatched - but for
   DIFFERENT reasons, and conflating them would mislead. (a)
   `Reputation.addReputation_discrete` logs
   `Adding <n> (<r>) reputation: '<reason>'.` - it DOES carry the
   `TransactionReasons` key in exactly the form the live pattern reads, and the only
   thing excluding it is the verb (`Adding`, not `Added`). A pattern for it is
   writable and would correlate; it is absent purely because nobody has MEASURED
   that line in the field, which is the rule governing this whole table. (b) The
   no-reason `AddReputation` branch logs
   `Added <n> (<r>) reputation. Total Rep: <total>` (period, not colon) and carries
   NO reason key, so it has no identity to correlate on even if matched. Both move
   the produced save, so the seam-declared-vs-save diff still catches them.
   CONSEQUENCE FOR `fill-from-capture`: it is legal only on the funds facet (science
   and reputation fills are rejected to preserve M-B2 leg independence), so with the
   funds capture provably empty, a `null` funds amount now ALWAYS fails ambiguous ->
   hard drift. That is the correct fail-closed outcome and is pinned by
   `test_funds_fill_from_capture_is_unreachable_in_the_field`.
   THE CORROBORATION KEY CHANGED WITH THE PATTERNS, and it had to: captured awards
   carry the GENERIC `stock-funds-award` / `stock-reputation-award` kinds while seam
   entries carry scenario kinds (`kerbal-hire`, ...), so a `kind`-based join can
   never match and EVERY captured award - including the scenario's own declared one
   - reported "unexpected" (reproduced against L1-hire-kerbal-career's own -62113
   hire debit), which would have made `gate` impossible to arm. The key is now
   (seqKey, FACET, AMOUNT within the facet tolerance), matched ONE-TO-ONE PER
   (ENTRY, POOL) - the canonical contract-complete entry declares funds AND
   reputation and stock logs those as two separate award lines, so it corroborates
   one award per pool while a second award on the SAME pool stays unexpected - with
   the structured identity (contract guid / science subject) as a fail-closed
   discriminator and an OPTIONAL per-entry `stockReason = ["CrewRecruited"]` as a
   tightener (pinned entries are tried first, so a greedy match cannot strand one).
   The rep facet needs the tolerance window rather than an exact compare:
   a seam entry declares the NOMINAL delta (-10), the stock line reports the APPLIED
   post-curve one (-9.999828). M-B2 independence is untouched - a corroborated
   amount is still never summed into EXPECTED, and the seam-declared-vs-save diff
   reds exactly as before.
   **THE GATE IS ARMED (2026-07-31) ON EXACTLY ONE SPEC, `CL-2-pod-impact-ledger`,
   and this item is now CLOSED for that scenario and OPEN for every other.** The
   history the rest of this item records still stands and is worth reading before
   arming a second one. Originally: the unexpected-award cross-check was WRITTEN as a
   hard PARSEK-FAIL(ledger) but had never once run with a working capture, so an
   unmatched captured award stayed a REPORT-ONLY oracle divergence until a scenario
   declared `[expectations.ledger] captureCrossCheck = "gate"` - declared by ZERO
   committed specs for as long as the knob existed. Arm per scenario after one green
   run shows that scenario's real award baseline (read `capturedRaw` in
   `results/<runId>.manifest.json`), declaring an entry - optionally with
   `stockReason`, and for a FLOWN scenario a `utWindow` - for each award that should
   be expected. The whole-set cells that used to assert the empty set are now
   ALLOWLISTS naming CL-2 with its arming evidence
   (`test_only_the_armed_allowlist_arms_the_capture_cross_check` and
   `test_only_the_armed_allowlist_declares_a_ut_window`), so a second spec arming the
   gate still reds until its own evidence is recorded.

   **THE ARMING PICTURE CHANGED with the reputation-only retirement above, in both
   directions - re-read this before sizing an arming session.** Smaller barrier: the
   original deferral cited "a career pad hop trips three milestone funds awards and
   two `Progression` rep awards no seam manifest declares". The three FUNDS awards
   are invisible to the capture (KSP logs none of them), so the only undeclared
   awards a career flight can surface here are the stock `Progression` rep awards.
   Larger caveat, and it is the one that matters: **no L1 spec can capture anything
   at all.** Their manifests are funds-only (`kerbal-hire` -62113,
   `facility-upgrade` -150000), science-only (`tech-unlock` -5.0 x2), all-zero
   (`kerbal-dismiss`) or absent (`L1-passive-sandbox`), and all six are `scene-ksc`
   runs that never fly, so they trip no rep award either. Arming
   `captureCrossCheck = "gate"` on an L1 spec would therefore arm a check whose input
   is provably always empty - a no-op gate that reads green forever, which is the
   same fail-open class this gate exists to close. **Arm it on a scenario that
   actually produces reputation.** `CL-1-pod-impact` is the only committed spec
   measured doing so (three awards: two `Progression`, one `VesselLoss`), but it
   deliberately carries no `[expectations.ledger]` block at all - see the "READ THIS
   BEFORE ADDING A LEDGER BLOCK" banner in its spec, whose unreachable-commit finding
   is unaffected by any of this. **The scenario-authoring half of that gap is CLOSED
   2026-07-30 by `CL-2-pod-impact-ledger`**, which carries the ledger block CL-1
   cannot, and its manifest closes to ~7e-7 exactly as the tolerance note below
   predicted. **THE FLOWN-SCENARIO KEY BLOCKER IS FIXED (2026-07-31, branch
   `harness-hardening-2`) AND THE OPERATOR ACTION HAS BEEN TAKEN - CL-2 IS ARMED;
   see the three-flight record below.** What
   CL-2's first live capture MEASURED (`stockLines=3 deduped=3 seamRejected=0`): all
   three awards reported UNEXPECTED, because `unmatched_captured_awards` joined on a
   UT-valued `seq_key` and a spec-declared entry could only match by naming the exact
   game UT the award lands on - a golden trajectory value that moved
   119.7 / 119.9 / 119.8 across three runs of this one craft. The KEY changed, as
   that finding demanded: a manifest entry may now declare `utWindow = [lo, hi]`
   (inclusive; mutually exclusive with `ut`), and the cross-check matches a windowed
   entry to a captured award whose UT falls inside the bounds - every other predicate
   (facet, amount-within-tolerance, structured identity, optional `stockReason`,
   one-to-one per (entry, pool), pinned-first order with exact-key entries ahead of
   windows) unchanged, a null-UT award never window-matches, and M-B2 independence
   untouched (the window is a matching hint; a captured amount is still never summed
   into EXPECTED). ARMING A FLOWN SCENARIO is: fly it green, read `capturedRaw` in
   `results/<runId>.manifest.json`, declare a `utWindow` (+ `stockReason`) entry per
   award from the mission's phase bounds, confirm the unexpected rows go to zero on
   the next green run, then flip `captureCrossCheck = "gate"`.

   **THAT CHECKLIST WAS WALKED END TO END AGAINST THE REAL GAME on 2026-07-31, ONE
   FLIGHT PER STEP, and `CL-2-pod-impact-ledger` is now ARMED** - the first and only
   committed spec to gate on the capture, and the first to declare a `utWindow`:
   - `2026-07-31_1630` BASELINE, PASS 168 s attempt 1, spec UNCHANGED. Reproduced the
     finding exactly: `stockLines=3 deduped=3 seamRejected=0`, all three awards
     UNEXPECTED (report-only) at ut **12.5 / 19.1 / 119.9**.
   - `2026-07-31_1638` WINDOWS DECLARED, still `report`, PASS 168 s attempt 1. ZERO
     `unexpected stock award` lines; `ledgerOracle hardDivergences=0 reportOnly=0`
     (it was `reportOnly=3`). Everything else token-identical.
   - `2026-07-31_1645` ARMED, `captureCrossCheck = "gate"`, PASS 168 s attempt 1,
     `hardDivergences=0 reportOnly=0`.
   - `2026-07-31_1759` CORRECTED + RE-FLOWN, PASS 180 s attempt 1, after review moved
     the bounds off the wrong clock and added the capture-non-emptiness tokens:
     `expectations mismatches=0` and `ledgerOracle ... crossCheck=gate` - the mode is
     now archived, so the armed claim is greppable rather than narrative. Its awards
     landed at ut 12.8 / 19.3 / 120.2: ALL THREE moved again and the impact set a new
     high above every prior run, which is the bounds-not-pins argument made a fourth
     time by the game itself.
   The windows are the mission's two PHASE BOUNDS - `[0, 100]` for the two ascent
   `Progression` awards, `[100, 400]` for the `VesselLoss` impact - and those three
   flights are themselves the argument for bounds over pins: the second `Progression`
   measured ut 19.1, then 19.0, then 19.1 across them, and the impact has now
   measured 119.7 / 119.8 / 119.9 over six archived runs. An exact-UT pin would have
   red the very next flight. THE BOUNDS ARE IN ABSOLUTE UT: the captured `ut=` is
   `fixtureUT + padDwell + T+`, so a ceiling must clear the harness's pad-dwell
   budget and not merely the flight profile - the first draft's `[100, 140]` did not,
   and was corrected before merge. **The expected totals did NOT move** (`oracle-expected
   funds=529600.0 science=100.0 rep=-7.999829000000001`, identical before and after):
   every CL-2 entry is already `ut`-less so `_sort_key` ordering is untouched, and all
   three rep entries are `repMode="applied"` so the nonlinear curve is not re-entered
   - caveat (b) below verified inert here rather than assumed. The funds milestone
   entry stays deliberately window-free (KSP logs no funds award, so it is never
   capture-matched). The PRE-LAUNCH mirror was widened in the same session to cover
   the other two entry-SHAPE keys an author hand-writes off a `capturedRaw` readout
   (a malformed per-entry `ut`, and an entry that is not a table) - both previously
   passed ADMIT and hard-failed AFTER the flight;
   `test_hlib.py::WhatThePreLaunchGateMirrorsTests` now sweeps the pre-launch gate
   and the run-time parser against each other in BOTH directions and records which
   rules stay run-time on purpose. Forensics + fix record: the struck
   corroboration-key entry in
   todo-and-known-bugs.md; shape pins:
   `test_hlib.py::FlownScenarioUtWindowCorroborationTests` (CL-2's measured lines as
   literals). ARMING CHECKLIST CAVEATS (from the PR #1397 adversarial review; both
   fail-CLOSED - a false "unexpected" row, never a false green): (a) the greedy
   matcher tries narrower acceptance first (exact keys, then windows by width), so
   declare windows as narrow as the phase bounds allow and do not pair a broad
   PINNED window with unpinned exact entries expecting the same reason - the
   pinned-first primary key dominates and can strand the exact entry; (b) converting
   a UT-stamped `repMode="nominal"` entry to a window forces `ut=null`, which moves
   it to the tail of `compute_expected`'s accumulation order, and the rep curve is
   nonlinear - re-check the expected total after conversion (`applied`-mode entries,
   i.e. every stock rep capture, commute and are unaffected). A malformed `utWindow`
   now reds PRE-LAUNCH as spec-invalid (`validate_ledger_expectations` mirrors the
   oracle's structural rules), so a window typo costs seconds, not a flight.
   TOLERANCE
   NOTE, now confirmed by CL-2's two flights: stock prints the applied rep delta at 7
   significant figures, so the three captured amounts sum to -7.999829 against a save
   carrying -7.99982834. An exact compare cannot succeed; budget the ~1e-6
   display-rounding residual (the default 0.1 rep tolerance absorbs it).
4. Flake ledgers (generated, gitignored) reset 2026-07-22 post-campaigns;
   quarantine (sticky, >0.20) is reporting-only and now reflects post-merge
   reality only.
5. INV2 double-cover recorder seam: REAL Parsek defect (first big catch),
   being fixed in its own lane.
6. No-vessel LoadGame boot contract (ledger lane): the SPACECENTER route in
   `ParsekTestCommandAddon.LoadGameImpl` now writes `persistent.sfs`
   (`GamePersistence.SaveGame(game, "persistent", save, OVERWRITE)`) AFTER
   `UpdateScenarioModules` and BEFORE `Start()`, matching stock
   `MainMenu.OnLoadDialogPipelineFinished`. Load-bearing because the KSC scene
   bootstrap `SpaceCenterMain.Start()` re-reads `persistent.sfs` from disk and
   runs `SetProtoModules` on THAT game, not the in-memory `HighLogic.CurrentGame`;
   without the write the fresh-* fixtures booted to KSC with no ParsekScenario,
   so `OnLoad` never ran and the `GameStateRecorder` never subscribed (the 5
   ACTING L1 cases reded on the missing recorded-action log line though the
   ledger oracle passed). Fixed 2026-07-23.

   **EXTENDED 2026-07-30 (R12/A1), and the gate's own lesson held.** The new
   `LoadGame scene=trackstation` route runs the SAME bootstrap as the SPACECENTER
   route - `UpdateScenarioModules` -> `SaveGame(persistent, OVERWRITE)` ->
   `Start()` - and it needs it for the identical reason, re-derived against the
   KSP 1.12.5 decompile rather than assumed: `SpaceTracking.Start()` also calls
   `GamePersistence.LoadGame("persistent", ...)` and then `SetProtoModules` on
   THAT disk game, exactly as `SpaceCenterMain.Start()` does. Without the pre-Start
   write a TRACKSTATION batch would run against a Parsek that never woke up. ZERO
   deltas from the SPACECENTER route were found. The FLIGHT route still
   deliberately does not do this, which is the asymmetry that cost CL-1 flight 1 a
   whole run (see its row); R12 did not widen it, and `scene=flight` is
   deliberately not an accepted value partly so nobody widens it by accident.
   Live-proven by `H23-tracking-station` (2026-07-30), whose in-game
   `TrackingStation` batch could not have executed a single test if the
   ScenarioModule had been missing.
7. COMMANDED-vs-OBSERVED assertion class (found 2026-07-25 via B1, fixed for
   B1 and EVA-4; the B1 half is LIVE-PROVEN as of 2026-07-29). An assertion or
   terminal that reads the machine's own "we issued the command" latch proves
   only that the machine acted, never that KSP complied - and it fails OPEN,
   which is the worst direction for a test.
   B1 shipped four months of green nightlies on a chute that never opened.
   The B1 fix stopped being an assertion about the code and became an
   observation on 2026-07-29: run `2026-07-29_1532_B1-pad-hop` emitted
   `craftCanopyObserved value=11963.610 met=True` off the OBSERVED kRPC
   ParachuteState and reached terminal phase LANDED. The contrast with the
   de-listed run is the proof that the new channel is load-bearing rather than
   decorative: `2026-07-25_0749`'s mission JSON carries exactly two assertions
   and no canopy one, and awarded `DOWN(chute-deployed impact)` anyway.
   AUDIT DEBT: `evaluate_b4_assertions`'s `chuteDeployed` is still a commanded
   latch, and the B4 fixture (`b2-lko-craft`) carries the same
   `automateSafeDeploy = 0` with the same altitude-triggered deploy at 3000 m.
   That does NOT mean B4 is broken: B4 is live-proven with a real SPLASHDOWN, so
   something did slow that craft, and the reentry profile differs from a pad hop.
   It means B4's chute claim rests on the same unfalsifiable evidence B1's did,
   and needs its own diagnosis from a B4 recording (check for `Parachute*` part
   events) before anyone concludes either way. Any new assertion over a
   part-module state must read the module, not the command.
   NOT A THIRD INSTANCE - a SIBLING class, found 2026-07-25 by the first full
   sweep and diagnosed 2026-07-26. (An earlier revision of this item opened
   "THIRD INSTANCE FOUND" and then contradicted itself two sentences later;
   the retraction is kept visible because this item is the canonical triage
   index for the class and a reader who counts EVA-4 as a member would apply
   the wrong prescription.) EVA-4's chute opened and then CUT itself, the
   kerbal fell from -11 to -109 m/s and died, and the MISSION still returned
   `MISSION-OK reason=all telemetry assertions met` - none of its four
   assertions covers kerbal survival, which is that mission's stated purpose.
   It is NOT an instance of THIS class: `EvaChuteDeploy` already had the observed channel, already debounced
   it (3 consecutive gone-reads) and already fast-failed on a distinctly named
   give-up (`eva-chute-kerbal-lost`). The observation was made, was correct, and
   was durably recorded (`driver.steps[6].verdict = ERROR`,
   `driver.allExpectedMet: false`) - and then no gate read it
   (`verifiers.driverValidity.status: "PASS"` on the same run). That is the
   failure mode which REMAINS after commanded-vs-observed is fixed, and it is
   worth its own name: **OBSERVED-BUT-UNGATED**. Closed structurally by
   `hlib.SEAM_VERB_POST_MISSION_ROLE` + the `missionOutcome` verifier row +
   `PARSEK-FAIL(mission-outcome)`, so the run reds on the outcome step's own
   verdict with no dependence on a spec author's log-token regexes. Full
   forensics (both defects) in `todo-and-known-bugs.md` under the EVA-4 section.
   AUDIT QUESTION IT RAISES: every gate that reads a step verdict should be
   checked for the same shape - a recorded observation nothing consults.
8. The no-1x-coast certification CANNOT SEE the coast warp-thrash class
   (orbit lane finding iii, 2026-07-25). `warp_audit.py` looks for a contiguous
   30-second window at 1x, and thrash 1x is FRAME-INTERLEAVED (issue, cancel on
   the next blind read, re-issue), so it never forms one; the certification is
   also a WALL-CLOCK profile check while the defect is a COMMAND invariant
   ("a healthy coast issues ONE warp command, not 3,603"). B5 flight 26 is
   certified no-1x at HEAD config and the metastable thrash would still have
   passed it. Covered for now by the machine-side `coast-warp-thrash` fast-fail
   (`MAX_PHASE_WARP_ISSUES` = 500, counted per phase entry and also armed at the
   correction aim-warp and flyby warp sites as `correction-aim-warp-thrash` /
   `flyby-warp-thrash`) plus the per-phase `warpUtilisation` block and the
   `warp-liveness-starved` floor, which judges an EPISODE-LOCAL game-s/wall-s
   ratio computed in the fly loop - NOT the per-phase `gameSecondsPerWallSecond`
   the block reports (on flight 2 those read 1.41 and ~39 respectively; see the
   re-fly sweep note under item 2 of the scenario roadmap for why the difference
   is load-bearing). The floor has never fired in the field and bounds the
   post-fix residual rather than flight 2's own thrash; its mechanism is covered
   by `test_shells.WarpLivenessRealMachineTests`. The AUDIT itself remains blind
   to the class - a real gap in an existing gate, not a new instrument.
   WARP TEARDOWN, corrected 2026-07-26 (review round 2): the floor's terminal
   used to return with the warp still armed, justified by a comment claiming
   nothing drives the game afterwards - `hlib.plan_unmet_mission_tail` does
   (StopRecording + FlushAndQuit run after ANY unmet mission, MISSION-FLAKE
   included). It now cancels the warp inline, best-effort. RESIDUAL: the other
   two fly-loop terminals (wall reaper, unexpected-warp flake) still return
   without a teardown; forensics and the reasoning for leaving them are in
   `todo-and-known-bugs.md`.
9. ~~B11 / B12 recordings-count windows are PROVISIONAL at {1, 9}~~ **CLOSED
   2026-07-25.** Both are PINNED at `{min 8, max 8}` from
   `verifiers.expectations.observed.recordings.count` on a measured green run
   (B11 flight 4, B12 flight 5), so every flown scenario is now pinned to its
   measured topology. What replaced it is a NAMED limitation, not a gate: the
   count is COMMIT-BLIND. `run.py count_recordings` counts `.prec` sidecars and
   `ParsekScenario` OnSave writes them for the ACTIVE (uncommitted) tree too, so
   two archived runs that crossed into the target SOI and NEVER committed
   (zero `CommitTreeFlight` lines) still produced exactly 8. The window guards
   recording TOPOLOGY; the commit itself is guarded by the logContract tokens,
   which now include the foreground SOI-crossing line and a per-recording
   terminal verdict naming `terminalOrbitBody`.
10. ~~NO SCENARIO EVER KILLS ITS SUBJECT IN A LIVE FLIGHT~~ - the HEADLINE is
   CLOSED 2026-07-28 by `CL-1-pod-impact`, LIVE-PROVEN on its second flight
   (FULL PASS attempt 1, 166 s, all seven verifiers PASS/SKIPPED). A scenario
   now kills its subject in a live flight and gates what Parsek records about
   it. The rest of this item STAYS OPEN, scoped below.
   WHAT IT WILL CLOSE, exactly: the HEADLINE sentence. CL-1 kills its subject in
   a live flight - a crewed pod with no chute, whose SUCCESS terminal is the
   kerbal's death read from `SpaceCenter.GetKerbal(name).RosterStatus` - and it
   makes the two things no pure decision can reach observable: what PARSEK
   RECORDS about a crew death, and what the CAREER LEDGER does about it. Neither
   is touched by any of the four xUnit cells this item names; they are about the
   EVA seam's own give-up decision, not about the recorder or the ledger.
   WHAT STAYS OPEN, and is NOT claimed: (a) the `eva-chute-kerbal-lost` path and
   the `PARSEK-FAIL(mission-outcome)` gate it feeds - CL-1 drives no EVA verb and
   asserts nothing about them, so they remain proven only by the pure decisions
   plus the fake-KSP replay; (b) the standoff's Unity-side wiring
   (`EvaluateStandoff` / `TryCompleteEvaExit`), whose only live guard is still
   EVA-4's `evaexit standoff cleared` token; (c) a crew death across a REWIND -
   `InGameTests/KerbalRecoveryOnSupersedeTest` still AUTO-SKIPS with "No
   kerbal-death actions in supersede subtree", and CL-1's committed tree is
   exactly the subtree it needs, which is what the atom is meant to be extended
   into next. **HALF-CLOSED and RE-BLOCKED 2026-07-30.** `CL-2-pod-impact-ledger`
   now produces that committed tree for real (`Committed tree ... Total committed:
   1 recordings, 1 trees`, `OnSave: saving 1 committed tree(s)`), so the subtree
   exists on disk - but the in-game test's precondition is a `KerbalAssignment`
   action carrying `KerbalEndState.Dead`, and CL-2 MEASURED that the commit writes
   `Unknown` instead, because `PopulateCrewEndStates` never runs on a
   destroyed-vessel commit. That defect (filed in todo-and-known-bugs.md) has to be
   fixed before stage B can stop the auto-skip; the rewind spec alone would not do
   it.
   THE TWO OBJECTIONS BELOW WERE ANSWERED RATHER THAN OVERRULED. "A fixture
   nobody maintains": `career-pad-craft` is `fresh-career` (maintained by the
   seven L1 ledger scenarios) plus `b1-pad-craft`'s craft copied byte-identically
   (maintained by B1 and EVA-4), spliced by a committed script with a `--check`
   mode that re-verifies the committed bytes. "Artifacts indistinguishable from a
   real regression": the death is CL-1's DECLARED terminal with its own phase
   name, its own assertions and its own required tokens, and a run whose crew
   SURVIVES reds by name (`crew-survived-impact`) instead of passing quietly.
   The original text follows, unedited, because it is still the correct
   description of everything CL-1 does not cover:
   NO SCENARIO EVER KILLS ITS SUBJECT IN A LIVE FLIGHT (recorded 2026-07-26
   with the EVA-4 re-prove). Flight 4 passed, but a LIVE kerbal proves nothing
   about a DEAD one: the `eva-chute-kerbal-lost` path and the
   `PARSEK-FAIL(mission-outcome)` gate it feeds are proven end to end only by
   xUnit over the pure decision (`KerbalTreatedAlive_RealLossSurvivesTheDebounce`,
   `Completion_KerbalLost_BeatsEverything`,
   `Completion_LossWinsOnceTheDebounceExpires`,
   `Completion_TimesOutHonestly_WhenBudgetEndsMidLossDebounce`) plus the fake-KSP
   smoke run that replays flight 3's step stream, plus a code read. The green
   flight exercised the OTHER branch of every one of those decisions.
   DELIBERATELY NOT CLOSED by flying a fatal profile: a scenario engineered to
   kill a kerbal buys one bit at the cost of a fixture nobody maintains and a
   run whose artifacts are indistinguishable from a real regression. The pure
   decision is the right home for it. What DOES need saying is the narrower
   in-game gap: the standoff's Unity-side wiring (`EvaluateStandoff`,
   `TryCompleteEvaExit`) has no headless cell, so a refactor dropping the
   `&& standoffSatisfied` conjunct would keep every suite green - the live
   `evaexit standoff cleared` logContract token is the only thing that catches
   it, and it now has one green run behind it.
11. RAW UNITY EXCEPTIONS are unjudged. Nothing in the verifier chain has ever
   read a KSP.log line Parsek did not write: every committed spec's
   `logContracts.forbidden` list carries Parsek-authored tokens only
   (`\[Parsek\]\[ERROR\]` and relatives), and the layer below it
   (`scripts/validate-ksp-log.ps1` -> `ParsekLogContractChecker`) parses ONLY
   `[Parsek]`-tagged lines - its rules (SES-000/001, FMT-001/002, WRN-001,
   REC-001/003) are about session markers, the Parsek line FORMAT, WARN content
   and recording start/stop pairing. So a run whose log is full of raw
   `NullReferenceException` stack traces, or an IMGUI
   `ArgumentException: GUILayout` storm, passed every gate the harness has.
   INSTRUMENTED 2026-07-29 (branch `harness-fail-open-gates`), REPORT-ONLY:
   `hlib.scan_unity_exceptions` counts four patterns
   (`NullReferenceException`, `MissingReferenceException`,
   `IndexOutOfRangeException`, `ArgumentException: GUILayout`), skipping
   `[Parsek]`-tagged lines so a caught-and-reported exception is not
   double-signalled, and run.py records a `unityExceptions` row (status
   `REPORT`, per-pattern counts, total) in every result JSON - including on a
   KILLED attempt, where a storm is a leading suspect for the hang. NOT GATING,
   and that is the operator-blocked half: nobody has ever measured how many a
   healthy KSP 1.12 + Parsek boot emits (stock KSP itself throws during scene
   loads), so any ceiling picked now would be a guess that could red
   live-proven scenarios. ARMING: add `[expectations.unityExceptions] maxTotal =
   N` to a spec - declared by ZERO committed specs - after reading the counts
   off a few green runs. Over-budget classifies `PARSEK-FAIL(unity-exception)`.
   Pinned by `UnityExceptionScanTests` (including a cell asserting no committed
   spec arms it).

   **HISTORICAL-CORPUS BASELINE (2026-07-29), the measurement this gate was waiting
   for - available now, without a new run.** All 137 `KSP.log`s present in `logs/` at
   the time of the sweep, scanned with `hlib.scan_unity_exceptions`:
   - `NullReferenceException` **284 total**; `MissingReferenceException`,
     `IndexOutOfRangeException` and `ArgumentException: GUILayout` are **0 across the
     entire corpus**. So the only pattern with any field signal today is the NRE, and
     the IMGUI-storm pattern the budget was partly designed for has never fired.
   - Per-run total: **73 of 137 runs are exactly 0**; median 0, p90 3, max 158.
   - The 158 is a single old outlier (`2026-07-10_2339_rerun4-green`, a long career
     run predating most of the suite). Excluding it the corpus max is **7**
     (`BDOCK-1-station-interceptor`, the longest/most complex flown scenario), with
     the next band at 4 (`H5`, `B5`) and a broad cluster at 3 spanning `B1`, `B2`,
     `B5`, `B6`, `B7`, `EVA-1`, `BDOCK-1` and `FORGE-bdock-station`.
   CAVEAT before arming off these numbers: the corpus is not segmented green-vs-red,
   it spans many builds, and per-spec sample counts are uneven (27 `B5` runs, 1
   `S1.7`). Treat it as an order-of-magnitude floor, not a per-spec baseline. On that
   basis a first ceiling in the **8-10** range is defensible for the heavy flown
   specs and **3-5** for the short KSC/L1 runs, which leaves real headroom over
   observed behaviour while still catching a storm (the failure mode is hundreds, not
   a handful). Arming remains the operator's call and still wants a couple of
   confirmed-green post-#1377 runs per spec; nothing here is armed.

12. ~~AN ORBITAL EVA RECORDS NOTHING~~: the per-physics-frame finalization-cache
    refresh throws out of `BallisticExtrapolator`, every frame, for the whole EVA.
    **PRODUCT DEFECT FIXED 2026-08-01 (branch `fix-orbital-eva-kepler`); the GATE
    HALF OF THIS ROW STAYS OPEN.** The fix guards the element set at the solver
    boundary (`TwoBodyOrbit.AreSegmentElementsPropagatable` - non-finite, negative
    `e`, parabolic `e == 1`, and `a`-sign-vs-conic-class disagreement), refuses a
    non-finite stock patch at the source (`PatchedConicSnapshot`, new
    `NonFinitePatchElements` reason, `MissingPatchBody` partial-keep semantics), and
    stops any finalizer throw from costing a sample
    (`RecordingFinalizationCacheProducer.TryBuildFromLiveVessel` fails loud once and
    declines through the existing `Fail(...)` path). Full reasoning - including why
    this is NOT a residual of ORBITSEGMENT-ANGLE-UNITS - in
    `todo-and-known-bugs.md`. LIVE PROOF, 2026-08-01 - both lanes flown against this
    build on `stock-minimal`, with the automation DLL sha256-verified against the
    branch build immediately before each launch (a sibling worktree clobbered it
    once mid-session, so this is checked, not assumed).
    `2026-08-01_1626_EVA-2-orbital-board`: PASS attempt 1, 61 s, every verifier
    PASS/SKIPPED; the EVA kerbal finalized `points=5 orbitSegs=0 maxDist=1888m`
    across a 10.068 s dwell (2026-07-30: `points=1 maxDist=0m`), with ZERO
    `ArithmeticException`, ZERO `SolveHyperbolicKepler` frames, ZERO
    `[FinalizerCache] Refresh threw` (layer 3 never fired) and ZERO
    `[Parsek][ERROR]`. THE GUARD DEMONSTRABLY FIRED IN FLIGHT: stock produced a
    degenerate patch at `snapshotUT=422.15` - the same UT as the 2026-07-30 first
    exception - and layer 1 refused it (`has non-finite elements (endUT=NaN);
    aborting predicted snapshot capture`), after which the tail declined with
    `NonFinitePatchElements` and the next snapshot 80 ms later was clean.
    `2026-08-01_1630_S0.7-exit-auto-commit`: PASS attempt 1, 48 s, every verifier
    PASS/SKIPPED, zero exceptions - no regression on the transition path.
    WHAT THE LIVE PROOF DOES NOT SETTLE, measured rather than assumed. A deliberate
    A/B control - same spec, same fixture, flown on a PRE-FIX DLL built from the
    merge base and byte-verified to carry none of the fix's strings, run
    `2026-08-01_1634_EVA-2-orbital-board` - ALSO flew clean (`points=4`, zero
    exceptions), because stock's degenerate-conic window never opened that run
    (`dT is NaN` count 0). The trigger is INTERMITTENT across runs of one fixture:
    `dT is NaN` occurred 2196x on 2026-07-30, 6x on the fixed-build run and 0x on
    the control. So the fixed-build run proves the guard is deployed, active and
    correct on a real degenerate patch, and proves no regression - but it is NOT a
    controlled reproduction of the 501-exception crash, and the absence of the
    exception there is CONSISTENT WITH the fix rather than solely attributable to
    it. Note what that intermittency does to the arming argument below: a points
    assertion only reds when the stock trigger actually fires, so it bounds the
    blast radius of the next such defect rather than guaranteeing detection.
    WHAT THE GATE IS ABOUT, and what is NOT fixed: nothing in the verifier chain
    asserts a recording has POINTS. `EVA-2-orbital-board`'s `recordings.count = { min = 2,
    max = 2 }` counts `.prec` FILES, and two empty recordings are still two files,
    so the same vacuous PASS is available to any future defect that empties a
    recording. ARMING: an `[expectations.recordings]` assertion on points, declared
    by ZERO committed specs. Until it exists this row stays open even though the
    2026-07-30 defect behind it is closed.
    FOUND 2026-07-30 while authoring `S0.7-exit-auto-commit`, on a run that used
    EVA-2's profile purely as a dwell primitive. MEASURED, run
    `2026-07-30_1532_S0.7-exit-auto-commit` (collected at
    `logs/2026-07-30_1833_S0.7-exit-auto-commit`): the EVA kerbal's recording ran
    from UT 422.11 to UT 432.21 - ten full seconds of orbital flight - and finalized
    with `points=1 orbitSegs=0 maxDist=0m`. The log carries **501**
    `[EXC] ArithmeticException: Function does not accept floating point Not-a-Number
    values` lines spanning 18:33:01.552 to 18:33:11.560, one per physics frame, all
    on the identical stack: `FlightRecorder.OnPhysicsFrame` ->
    `RefreshFinalizationCache` -> `RecordingFinalizationCacheProducer.TryBuildFromLiveVessel`
    -> `IncompleteBallisticSceneExitFinalizer.TryFinalizeRecording` ->
    `TryBuildStartStateFromSegment` -> `BallisticExtrapolator.TwoBodyOrbit.SolveHyperbolicKepler`
    -> `System.Math.Sign(NaN)`. Upstream of it, stock logs `CheckEncounter: failed to
    find any intercepts at all` and `dT is NaN! tA: NaN, E: NaN, M: NaN, T: NaN` out
    of `PatchedConicSolver.Update` on the kerbal's own patched conic, so the NaN
    enters through the snapshot rather than being manufactured by the extrapolator.
    PRE-EXISTING, not an R12 regression: R12 touched only `TestCommands/`, docs,
    tests and `harness/`. WHY NOBODY SAW IT: `EVA-2-orbital-board` is green and stays
    green - its `recordings.count = 2` counts `.prec` FILES, and two empty recordings
    are still two files - so this is the inventory doc's "fourth trap" (a vacuous PASS
    the tally cannot see) in the recordings dimension rather than the batch one.
    NOT REPRODUCED on the pad: `EVA-4-atmo-chute`'s archived log has zero occurrences
    of `SolveHyperbolicKepler` and recordings of 278 / 96 points, so the trigger looks
    specific to an orbital (or otherwise degenerate-conic) EVA. Forensics, the chosen
    fix and its three layers in `todo-and-known-bugs.md`.
13. INTERMITTENT stock `SpaceTracking.buildVesselsList` NRE during TRACKSTATION
    synthetic-ghost teardown, and Parsek's own classifier does not recognise it.
    Observed 2026-07-30 on the FIRST `H23-tracking-station` flight (2 raw Unity
    exceptions) and NOT on the second, identical, pinned flight (0) - which is why
    `[expectations.unityExceptions] maxTotal` is deliberately NOT armed on that spec.
    The signature: `[Parsek][WARN][GhostMap] SpaceTracking.buildVesselsList exception
    left visible: type=NullReferenceException totalVessels=0 ghostVessels=0 ...
    scanError="NullReferenceException..."` immediately followed by
    `[ERR] Exception handling event onVesselDestroy in class SpaceTracking`.
    `GhostTrackingStationPatch.IsKnownGhostProtoVesselNre` declined to suppress it
    because the CONTEXT SCAN ITSELF threw (hence `totalVessels=0` and the populated
    `scanError`), so the classifier saw a zero-ghost context and correctly refused to
    swallow an exception it could not attribute. Report-only today; the open question
    is whether a scan that fails should be classified from the scanError instead of
    from its empty counts. Costs nothing today - the tests it interrupts still pass.

## Operator items outstanding

1. Career fixture saves (3) - DONE + LIVE-PROVEN (no operator session): file-
   constructed (fresh-career / fresh-science / fresh-sandbox), 7/7 ledger
   scenarios green, re-tiered pending-fixture -> daily, hire/upgrade author
   constants confirmed, the seed-baseline no-pools gate resolved.
2. EVA fixture saves (2) - DONE + LIVE-PROVEN 2026-07-24 (no operator flying):
   both `eva2-lko-crewed` and `eva3-pad-3crew` were FORGED HEADLESSLY
   (FORGE-eva2-lko / FORGE-eva3-pad) and are committed, EVA-2 re-tiered
   pending-fixture -> daily, and all three EVA scenarios flew green, which
   settled P1/P3/P4/P5/P6 (count windows now pinned exactly, log-token wording
   confirmed, flag capture proven). The only EVA item left is the optional
   promotion of EVA-1 / EVA-3 nightly -> daily once flake data exists.
3. ~~Stock-award real-line capture session (unblocks the pattern rewrite)~~ -
   the CAPTURE SESSION IS NO LONGER NEEDED. Reputation was measured from the
   CL-1 flights and the pattern rewritten from it 2026-07-29 (gate 3).
   (a) ~~one collected KSP.log from an L1 SCIENCE scenario, to measure the
   science award shape~~ - **CLOSED NEGATIVELY 2026-07-29, no run required.**
   There is no science award line to measure and no funds one either:
   `Assembly-CSharp.dll` contains ZERO occurrences of `" science: '"` and
   `" funds: '"`, and `ResearchAndDevelopment.AddScience` /
   `Funding.AddFunds` carry no `Debug.Log`. The capture is reputation-only,
   permanently; the funds pattern is retired (gate 3). Do not schedule a run
   for this, and do not add a science pattern on the strength of the
   `[Research & Development]: +<n> data` line - that is experiment DATA, not a
   science-currency delta.
   (b) STILL OPEN: per-scenario arming of `[expectations.ledger]
   captureCrossCheck = "gate"` once a green run's
   `results/<runId>.manifest.json` `capturedRaw` shows that scenario's real
   award baseline - but note it CANNOT be armed on any L1 spec (none of the six
   can produce a captured award; see the arming-picture note in gate 3). It
   wants a reputation-producing scenario, which today means giving
   `CL-1-pod-impact` a ledger block it deliberately does not have.
4. ~~B9 rewind observation session (S1.5 + S4.1)~~ - CLOSED. No operator session
   was ever needed (established 2026-07-26), and both have now FLOWN GREEN
   unattended: S4.1 on 2026-07-28 (`2026-07-28_1639_..._a2`, 61 s) and again
   clean on attempt 1 2026-07-29 (`2026-07-29_1530`, 71 s); S1.5 on 2026-07-29
   (`2026-07-29_1528_..._a2`, 69 s) - its first execution ever. The re-tier's
   contested premise is now settled by observation, not argument: `LoadGame` did
   focus `gloops-airshow`'s active vessel into FLIGHT and every verb EXECUTED,
   so the LIVE-LOAD fidelity of the B9 fixture's cloned per-slot vessels is
   established. ONE thing stayed open and it was not an operator session: S4.1's
   supersede-row / tombstone asserts under `[expectations.rewind]` were
   PENDING-VERIFIER until the M-C2 rewind save-parse verifier landed. That
   verifier landed report-only 2026-07-31, and the PROMOTION step it left is
   now **CLOSED the same day** (branch `r9-arm-s41`): the reading run
   `2026-07-31_1628` (PASS, 59 s) confirmed the report-only readings match the
   spec's declared windows exactly (`supersedeRows 0` / `tombstones 0` for the
   unflown-provisional route, `mismatches=[]`), `gating = true` went into
   `[expectations.rewind]`, and the armed gate was then proven live in BOTH
   directions - `2026-07-31_1635` PASS armed, and a `min = 1` negative control
   reddening `2026-07-31_1637` `PARSEK-FAIL(save-structure)`. This item is
   CLOSED: not VERIFIER-LANDED / LIVE-PROOF-PENDING but ARMED / LIVE-PROVEN. The other -
   S4.1's FLAKE QUARANTINE - is CLOSED 2026-07-30. S4.1-IDLE-DISCARD was ruled a
   real defect and fixed on branch `fix-s41-idle-discard`, and the deliberate
   multi-run sweep this doc demanded then flew five consecutive runs, all PASS on
   attempt 1 (`2026-07-30_0940` 72 s, `_0942` 58 s, `_0944` 57 s, `_0945` 57 s,
   `_0947` 58 s), taking the lifetime to 7 PASS / 3 INVALID with the generated
   flake ledger at total=5 numerator=0 rate=0.0 quarantined=false. Caveat carried
   in full by the S4.1 row: no run ENTERED the new refusal guard (zero
   `idle detected` lines; every run concluded through the post-transition deferred
   dialog), so the guard's proof is its three behavioral xUnit cells and S4.1's
   current driven route may no longer reach the idle fast path at all - candidate
   cause seam commit `f97717744`.
5. ~~B1 chute re-prove~~ - CLOSED 2026-07-29 by run `2026-07-29_1532_B1-pad-hop`
   (PASS, attempt 1, wall 408 s). B1 is back on the live-proven list, and the
   re-prove landed the thing the de-listing existed for: `craftCanopyObserved`
   MET at 11,963 m off the OBSERVED ParachuteState, terminal phase LANDED. P1
   (the final full-canopy leg to the ground) and P2 (which end it reaches) are
   pinned by this flight.
6. ~~H7-H20 tally measurement~~ DONE 2026-07-27, fully closed: all 14 flown via
   `python run.py --tag ingame-batch`, all 14 PASS on attempt 1, 805 s (13.4 min)
   total, thirteen pinned splits confirmed token for token. The residue -
   `H20-eva-spawn-position`, whose loose interim pin could not distinguish
   `passed=2 skipped=0` from `passed=1 skipped=1` - was closed by re-flying that one
   scenario ALONE (59 s) so its log survived to be read: `passed=2 skipped=0`, the
   overlap probe fired, walkback executed. All 14 now pin whole off measured lines.
7. ~~R1 rewind-loop-flown~~ - CLOSED 2026-07-28 by FLIGHT 4, run
   `2026-07-28_1509_R1-rewind-loop-flown` (PASS, attempt 1, wall 304 s). This item
   read "It is not green and should not be" until 2026-07-29; that was true of
   flights 1-3 (2026-07-26) and stale the moment flight 4 landed. Flight 4 WAS the
   "one cheap experiment" this item asked for, and it answered the question in the
   direction nobody had bet on: the `Added [1-9][0-9]* supersede relations` contract
   PASSED (`Added 1 supersede relations for subtree rooted at b9-booster-a`), so
   there was no Parsek-side fix to wait for - `R1-EMPTY-PROVISIONAL` resolved as a
   FIXTURE artifact, not a product defect. R1 is on the live-proven list. Its tier
   promotion is EARNED but deliberately NOT APPLIED (the spec still reads
   `tier = "operator"`); see the R1 row for why that is a human call. The runbook
   below is retained because it was executed verbatim and works.

### R1-rewind-loop-flown: operator runbook [EXECUTED END-TO-END 2026-07-28, works verbatim]

**Verified 2026-07-28**: these three steps were run in this exact order on a fresh
worktree and produced the lane's first PASS (run `2026-07-28_1509`, wall 304 s). Step 1
was genuinely required (the worktree had no `.venv`), and step 2 was genuinely required
(the automation instance was carrying a DLL from before the branch under test - verify it
afterwards by grepping the DEPLOYED `automation/stock-minimal/.../Parsek.dll` for a
distinctive new UTF-16 string, NOT the dev instance's copy).


**Step 1 - the mission venv (DO NOT SKIP on a fresh worktree).** Every autopilot
scenario spawns its mission in `harness/missions/.venv`, which is gitignored and
therefore ABSENT in a newly created worktree. Without it the run dies at
pre-launch ADMIT with `INVALID tooling-venv (terminal, no KSP boot)` - which is
exactly what killed attempt 1 of the first R1 flight (2026-07-26). It is cheap
and idempotent, so run it every time rather than guessing:

```bash
cd harness && python missions/bootstrap_venv.py
```

**Step 2 - provision** (the harness runs a DIFFERENT KSP instance from the dev
one; needed whenever the automation instance is stale relative to the branch
under test, and always when the branch changed C#):

```bash
cd harness && python provision/provision.py --profile stock-minimal
```

**Step 3 - fly it** (one scenario, explicit `--id`; `operator` tier is in no
cadence):

```bash
cd harness && python run.py --id R1-rewind-loop-flown
```

Live watch while it flies:

```bash
cd harness && python status.py            # phase, gates, last events
tail -f results/<runId>_mission.stdout.log
```

WHAT CONFIRMS THE CYCLE. Four independent surfaces; require ALL of them, and
treat the first two as the load-bearing pair (the rest can be true while the
rewind did not happen):

1. The mission's OWN observations - `results/<runId>_mission.json`. FOUR rows
   carry `channel: "observed"` and all four must be `met: true`:
   `recorderIdleBeforeRewind` (`value: "false"`), `clockRewound` (a POSITIVE
   `value`, the seconds the clock ran backward), `vesselStateChanged`, and the
   two loop rows `postRewindFlightObserved` (metres climbed after the rewind) +
   `postRewindFlightRecordedSomewhere` (a POSITIVE point count; `-1` is the UNREAD
   sentinel, `0` means the second flight recorded nothing). If
   `rewindSeamAccepted` is met and `clockRewound` is not, the verb returned OK
   and nothing rewound - the exact failure this lane exists to make visible. If
   the rewind rows are met and the LOOP rows are not, the rewind worked and the
   re-flight did not, which is the R1-EMPTY-PROVISIONAL shape.
2. The mission log, in order (`results/<runId>_mission.stdout.log`). Note the
   FOUR seam commands and the FOUR distinct ids - the STOP + RECORDER-IDLE pair
   is what flight 1 was missing:
   ```
   [Mission][Info][Seam] seam command written [id=<step>.commit cmd=CommitTree]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.commit cmd=CommitTree verdict=OK -> OK
   [Mission][Info][COMMIT] phase COMMIT -> STOP ...
   [Mission][Info][Seam] seam command written [id=<step>.stop cmd=StopRecording]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.stop cmd=StopRecording verdict=OK -> OK payload=...stopped=true,idle=false...
   [Mission][Info][STOP] phase STOP -> RECORDER-IDLE ...
   [Mission][Info][Seam] seam command written [id=<step>.state0 cmd=RecordingState]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.state0 cmd=RecordingState verdict=OK -> OK payload=...recording=false...
   [Mission][Info][RECORDER-IDLE] phase RECORDER-IDLE -> REWIND ...
   [Mission][Info][Seam] seam command written [id=<step>.rewind cmd=InvokeRewind rp=rp_b9_root slot=1]; polling 420s
   [Mission][Info][Seam] seam command response id=<step>.rewind cmd=InvokeRewind verdict=OK -> OK payload=...rewound=true...
   [Mission][Info][REWIND] phase REWIND -> VERIFY ...
   [Mission][Info][VERIFY] phase VERIFY -> REWOUND ut=<LOWER than the RECORDER-IDLE->REWIND ut>
   [Mission][Info][REWOUND] phase REWOUND -> RELAUNCH ...
   [Mission][Info][RELAUNCH] action set_throttle value=1.000
   [Mission][Info][RELAUNCH] action activate_stage ...
   [Mission][Info][RELAUNCH] phase RELAUNCH -> LOOP-POINTS ...
   [Mission][Info][Seam] seam command written [id=<step>.loop0 cmd=RecordingState]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.loop0 cmd=RecordingState verdict=OK -> OK payload=...points=<NON-ZERO>...
   [Mission][Info][LOOP-POINTS] phase LOOP-POINTS -> LOOP-CLOSED ...
   ```
   `recording=false` on the `state0` reply is the load-bearing token in the first
   half: it is the dispatcher's own `recording-active` gate, READ rather than
   assumed. If the log shows `recording=true` probes marching up `state1`,
   `state2`, ... the post-commit promotion re-armed a recorder that will not stop
   - capture the KSP.log, that is a Parsek finding.
   `points=<NON-ZERO>` on the `loop0` reply is the load-bearing token in the
   second half. A rewind that is never flown again leaves the re-fly provisional
   EMPTY, which is exactly what made flight 2 red (R1-EMPTY-PROVISIONAL). If the
   probes march up `loop1`, `loop2`, ... still reading `points=0`, the craft flew
   but nothing recorded it.
   All FIVE seam ids MUST differ (`.commit` / `.stop` / `.state0` / `.rewind` /
   `.loop0`): the C# seam skips duplicate ids, so identical ids mean a command was
   never executed. The `ut=` on the `VERIFY -> REWOUND` line must be LOWER than
   the one on the `RECORDER-IDLE -> REWIND` line - that is the backward clock,
   readable straight off the log without opening the result JSON.
3. Parsek's own re-fly milestones in the collected `KSP.log` (these are the
   spec's required logContracts, so a miss reds the run):
   ```
   Re-Fly (Rewind-to-Separation) StartInvoke
   Invocation complete
   ConsumePostLoad: restoring bundle with route-retire cutoffUT=
   Restored: recs=... pendingScience=...
   Added <N (>= 1)> supersede relations for subtree rooted at ...
   ```
   The last one is THE loop-closed proof and the count is pinned NON-ZERO on
   purpose: `Added 0 supersede relations` is what a refused batch emits, and
   flight 2's log had no `Added ...` line at all because the invariant check
   refused it first.
4. The run verdict: `results/<runId>.json` PASS with all seven verifiers green,
   `expectations` mismatches 0, `anomalySweep` hits `[]`.

IF IT REDS: the mission verdict now NAMES the cause. Since the 2026-07-26
follow-up, a non-OK seam response surfaces PARSEK's own `msg` reason verbatim
(`Parsek's reason: <reason>`) instead of the machine speculating, and the same
string rides `assertions[rewindSeamAccepted].rejectReason` in the result JSON.
Read that first. Known causes, in likelihood order:
- `Parsek's reason: recording-active` - the flight-1 failure. It should now be
  unreachable: the machine stops the recorder and OBSERVES `recording=false`
  before commanding the rewind. If it reappears, something re-armed a recorder
  between the idle probe and the dispatch, which is a new finding.
- `REJECTED unknown-rp` on the InvokeRewind response: the `rewind-b9` injection
  did not land in the staged run save (check the `[Stage] inject=rewind-b9` line
  and the inject exit code). The spec composes the preset with the `b2-lko-craft`
  template; `run.py`'s inject step targets the RUN save, not the template.
  (Flight 1 CLEARED this one: `rp_b9_root` resolved fine.)
- `Parsek's reason: refly-gate <reason>` - `RewindInvoker.CanInvoke` declined.
  The reason is verbatim; the likely one on this composed fixture is the
  deep-parse PartLoader precondition over the cloned sidecar vessels.
- `rewind-not-observed`: the verb returned OK but the clock never moved back.
  That is a REAL finding, not a harness fault - capture the KSP.log and do not
  "fix" it by relaxing `minUtRegressionSeconds`.
- `never climbed ... m` (RELAUNCH): the rewind put the craft back but the second
  flight never happened. On the `rp_b9_root` slot the craft lands PRE_LAUNCH on
  the pad, so throttle + stage should fly it; if the stage is spent or the craft
  is not the one expected, that is a fixture question, not a tolerance question.
- `still read points=0` (LOOP-POINTS): the craft flew and nothing recorded it.
  Check `autoRecordOnLaunch` is still true after the re-fly load - this is a real
  Parsek question, not a harness one.
- `forbidden matched \[Parsek\]\[ERROR\]` with `AppendRelations invariant
  violation ... reason=empty Points`: that is R1-EMPTY-PROVISIONAL. It should now
  be unreachable in R1 (the loop closes), and it is deliberately still reachable
  in `S4.1-rewind-merge`, which carries it as `expectedFail.bugId`.
- `INVALID tooling-venv` with no KSP boot: step 1 was skipped.

## Roadmap (agreed order; each item named by its Parsek utility)

**The FORWARD build order now lives in `docs/dev/autotest-roadmap.md`** (what we
cannot reproduce yet grouped by cause, plus the ranked dependency-justified sequence
R1-R14 starting from the D1 basics). Items 1-6 below stay as the HISTORICAL record of
the mission lanes that closed and the findings each produced; item 7 stays as the
unscheduled-candidates list. Consult the roadmap doc for what to build next.

1. M-C2 in-game proof - DONE 2026-07-24. The verbs + hlib companions +
   EVA-1/2/3 specs are implemented and the whole live-prove list (P1-P6) is
   closed: both fixtures (`eva2-lko-crewed`, `eva3-pad-3crew`) forged
   headlessly and committed, all three EVA scenarios flown green, count windows
   pinned exactly, log-token wording confirmed, ladder-drop + flag-capture
   proven. The crew/EVA/flag recording surface no flight can reach is now
   covered.
2. Mun/Minmus ORBIT missions - capture burn + commit-in-target-orbit terminal:
   recordings that END in a foreign SOI (new commit/BG-handoff surface vs the
   free-return shape). **DONE 2026-07-25** as **B11-mun-orbit** +
   **B12-minmus-orbit**, both LIVE-PROVEN and the lane's re-fly debt paid:
   - B11 FULL PASS three times - flight 2, flight 3 as the confirmation the
     changed TARGET-FLYBY profile owed, and flight 4 as the count-pin run. Wall
     1,269-1,271 s, all six assertions met, capture eccentricity 0.000127,
     TARGET-FLYBY warp 27 game-s / 2 commands (was 8,213 game-s).
   - B12 FULL PASS on flights 4 and 5 (5 is the count-pin run). Wall 580 s, all
     six assertions met, capture eccentricity 0.00026, a 194,543 game-second
     coast in 26 wall-s at ratio 7,535 on 3 warp commands.
   - B6-minmus-flyby, exposed to two of the shared-machine defects, re-flew
     green (wall 359 s), so its LIVE-PROVEN mark is honest at HEAD again.
   Four shared-machine findings came out of the lane (no-start watchdog vs
   MechJeb's own 600 s pre-ignition hold; a GAME-time correction budget spent by
   the aim-then-warp it waits on; the metastable coast warp-thrash behind KSP's
   NaN `time_to_soi` under a warp ramp; warp inherited across the SOI boundary at
   10,000x) - full forensics in `todo-and-known-bugs.md`. B5/B7 exposure was
   RE-ASSESSED after review (the earlier "both changes gate on `capture_enabled`
   / opt-in fields" claim was WRONG: two of the four fixes - the correction-budget
   re-anchor and the coast native-warp latch - are ungated shared-machine
   changes, which is exactly why B6 owed a re-fly). Corrected position:
   - B5 is covered by PROXY, not by a gate. `B11-mun-orbit` carries params
     IDENTICAL to `B5-mun-flyby` (same `targetBodyName`, `correctionTriggerAlts`,
     `transferBurnTimeout`, `coastTimeout`, `coastWarpFactor`, `flybyWarpFactor`,
     `soiLead`, `nodeArrivalMargin`) and flew that coast + correction
     configuration green on the fixed machine.
   - B7 was FLOWN at HEAD 2026-07-25 rather than argued about, and it does NOT
     pass - but for a PRE-EXISTING reason, not a lane regression. See the B7 row
     and the todo entry: it is the first flight of HEAD's 300 km periapsis target
     (the gate that row already carried), the target was hit correctly
     (`pe=310089`), and the approach was captured by IKE on both attempts. The
     lane's changes are exonerated on the evidence: `corrBudgetAnchorUt=none`
     (the re-anchor never engaged, so it ran main's exact bound) and
     `phaseWarpIssues=1` (the coast latch worked, which is WHY the flight reached
     Duna at all - every archived pre-fix B7 run died at `Kerbin to Sun` and
     never reached the target SOI).
   POST-REVIEW RE-FLIGHT SWEEP (2026-07-25, all four green on attempt 1 on the
   fixed machine + the redeployed DLL). Three Opus reviewers returned FIX-FIRST;
   the machine fixes changed frames the green flights had taken (a
   `time_to_periapsis > 0` arming conjunct, three new named warp terminals armed
   on the SHARED machine), so every affected mission was re-flown rather than
   argued about:
   | Scenario | Result | Why it was owed |
   |---|---|---|
   | B11-mun-orbit | PASS, wall 1,270.195 s | arming conjunct + first live proof of the commit-terminal token |
   | B12-minmus-orbit | PASS, wall 580.826 s | same |
   | B5-mun-flyby | PASS, wall 468.009 s | `flyby-warp-thrash` / `correction-aim-warp-thrash` / `warp-liveness-starved` are new terminals reachable on the flyby family |
   | B6-minmus-flyby | PASS, wall 359.425 s | same |
   None of the three new terminals fired on a healthy flight, so they bound the
   broken case without narrowing the correct one. Read that per terminal: for
   the two THRASH terminals it is real evidence (`action warp_to_ut` counts
   exactly 1 per phase on all four flights, against a cap of 500), and for
   `warp-liveness-starved` it is worth nothing, because no archived episode was
   ever even JUDGED. Measured from `warpUtilisation` across every archived
   `harness/results/*_mission.json`, no phase that armed a NATIVE warp reached
   the floor's 180 wall-second minimum window: the longest is COAST-TO-TARGET at
   76.4 s (B7), then CORRECTION-BURN 69.6 s, TARGET-FLYBY 30.2 s,
   PLAN-CORRECTION 3.7 s, PLAN-CAPTURE 0.6 s.
   CLOSED 2026-07-26, and the answer was NOT the one the caveat assumed. Three
   findings, all from primary evidence:
   1. **This floor could not have caught B12 flight 2 either, and the window is
      not why.** Flight 2's thrash CANCELLED the command every other frame
      (3,603 `warp_to_ut` against 3,602 `cancel_warp`, the
      `gate warpToCmd <target>->none` / `none-><target>` pair alternating frame
      by frame), and the fly loop resets the liveness episode the moment
      `warp_to_cmd` clears. The episode never lasted two frames. The THRASH
      counter is what bounds that shape; this floor bounds the POST-FIX
      RESIDUAL, where `coast_native_warp_hold` removed the cancel half of the
      cycle but a crawling rails rate is untouched by that fix. That shape has
      never been flown, is reachable, and nothing else in the stack can see it.
   2. **The floor does not consume `gameSecondsPerWallSecond`**, despite what
      its own rationale used to say. That is a PER-PHASE average; the floor
      computes an EPISODE-LOCAL ratio in the fly loop. On flight 2 the two
      differ by 27x - the phase row reads ~39 (one successful 146,070
      game-second warp burst earlier in the same phase dominates the mean),
      the thrashing episode reads 1.41. Fed the phase number a 5.0 floor is
      silent on the defect it exists for. Both numbers in the docs are correct;
      they measure different things, and only the episode one is a give-up.
   3. **The disarm, not the window, is what protects the long 1x holds** - so
      the window must never be described as that margin. MEASURED, 31 archived
      phase rows across SEVEN phase names run PAST 180 wall-seconds at a ratio
      BELOW the 5.0 floor (REENTRY 428.4 s @ 1.45, DEORBIT 349.8 s @ 1.00, DOCK
      247.1 s @ 1.00, MJ-ASCENT 198.5-199.3 s @ 1.33 across 17 rows, INT-ASCENT
      194.6 s @ 1.55, STATION-ASCENT 194.3 s @ 1.83, PARK 180.2-180.6 s @ 1.00
      across 9 rows), and CAPTURE-BURN has been measured at 138.0 s @ 1.10, only
      42 seconds short of being judged. Every one would FIRE if `warp_to_cmd`
      were left armed across it. CAPTURE-BURN reads `warpCommands=0` on all ten
      archived captures because `_b5_enter_plan_capture` clears the command and
      the PARK entry clears it again; both clears are now pinned by tests.
   WHAT SHIPPED: no constant changed (180.0 / 5.0 stand, so no frame any flown
   mission took can move), both are now anchored on the measurement above rather
   than round, the PROVISIONAL note is gone, and the mechanism is covered by
   `test_shells.WarpLivenessRealMachineTests` - the REAL b5 machine driven
   through `fly_loop` on flight 2's post-fix telemetry (same body, same altitude
   band, same 2.76x rails rate, same measured 1.41 game-s per wall-s), which
   fires the named give-up while the thrash counter stays at 1 issue of 500.
   FIELD STATUS stays unflown, and that is now recorded as the CORRECT state
   rather than a debt: every healthy armed episode we fly is 0.5-76.4 wall-s and
   finishes far inside the window, and reaching the floor in the field would
   mean reintroducing the defect.
   ID NOTE: this item was informally called "B8", but B8/B9/B10 are already
   taken in `automated-testing-scenario-catalog.md` section 2 (loop-B7-as-
   mission / crash-rewind-refly / career passive safety) and B3 is the EVA
   branch, so the lane is B11 + B12. The count follow-up is CLOSED: both windows
   are PINNED at {8, 8} off a measured
   `verifiers.expectations.observed.recordings.count` (B11 flight 4, B12 flight
   5). The pin is COMMIT-BLIND by construction - it guards recording topology,
   not the commit - so the commit claim is carried by the logContract tokens
   instead (see known-gate item 8).
3. Mun/Minmus LANDING missions - upper stage landed: landed-on-other-body
   recording, surface TrackSections off Kerbin, the landing FSM seam.
   **DONE 2026-07-25 - BOTH AXES LIVE-PROVEN ON THEIR FIRST FLIGHT** as
   **B13-mun-landing** + **B14-minmus-landing** (branch
   `autotest-landing-missions`, stacked on `autotest-orbit-missions`).
   | Scenario | Result | Landed terminal | Airless surface entry |
   |---|---|---|---|
   | B14-minmus-landing | FULL PASS attempt 1, wall 2,083.9 s | `terminalState=Landed terminalOrbitBody=Minmus` | `Approach -> SurfaceMobile` |
   | B13-mun-landing | FULL PASS attempt 1, wall 2,747.9 s | `terminalState=Landed terminalOrbitBody=Mun` | `Approach -> SurfaceStationary` |
   Both measured `observed.recordings.count = 8`, pinning both windows from
   measurement, both with `verifiers.expectations.status = PASS` and
   `mismatches = []`. The DERIVED topology is identical to B11/B12 - 6 Destroyed
   boosters, 1 Orbiting/Kerbin flameout core - with ONLY the root's terminal
   changed, which is precisely the discriminator this lane exists to move; the
   per-recording breakdown was read off the live log during the run and is not
   re-verifiable, because `collectLogs.ran = false` kept no KSP.log on either
   flight. B14 touched
   down at -0.25 m/s vertical / 0.06 m/s horizontal on the craft's own three
   landing legs; no craft modification, no separate lander stage, no downloaded
   craft (the stock Kerbal X upper stage already carries legs, heat shield,
   chute, ladders and solar panels, and the measured fuel at PARK - lf 592.8 at
   Mun, 650.6 at Minmus - leaves better than 3x the landing cost).
   A PARSEK FIX WAS REQUIRED for the lane to be verifiable at all: a LANDED
   recording never carries `TerminalOrbitBody` (`CaptureTerminalOrbit` returns
   early for surface situations and `UsesTerminalOrbitMetadata` excludes
   `Landed`), so every landed commit line read `terminalOrbitBody=(null)` and
   the ONE fact this lane proves - WHICH body - was unprovable from logs.
   `FormatCommitTerminalLine` (and the over-cap summary line) now resolve the
   body TERMINAL-STATE-AWARE: `TerminalPosition.body` first for the SURFACE
   terminals (`Landed` / `Splashed`), `TerminalOrbitBody` first otherwise, each
   falling back to the other. The state-awareness is load-bearing - NOTHING ever
   CLEARS `TerminalOrbitBody`, so a craft that orbited the Mun and later landed
   on Kerbin would otherwise print `terminalState=Landed terminalOrbitBody=Mun`.
   9 xUnit cells.
   MECHJEB CAVEAT CONFIRMED LIVE: the untargeted descent runs at 1x with ZERO
   warp commands under the installed 2.15.1 pin, exactly as the decompile
   predicted, so the wall budget was sized for the no-warp case and the flight
   finished at 48% of it rather than being reaped.
   COST: B13 at 2,825 s is now the most expensive scenario in the suite
   (previously BDOCK-1 at 2,164 s), and the pair adds ~83 minutes, taking the
   full-suite p50 from ~137 to ~219 minutes. A nightly rotation probably cannot
   afford B13 + B14 + BDOCK-1 together; the duration ledger now prices that
   decision instead of leaving it to guesswork. Same id reasoning as B11/B12: B8/B9/B10 are taken
   in the catalog and B3 is the EVA branch, so the LANDING lane takes B13/B14.
   - MACHINE: the LIVE-PROVEN `mlib.b5_decide` with ONE new param,
     `landingEnabled`, on top of `captureEnabled`. PRELAUNCH through PARK is
     byte-identical to the five B11 and six B12 flights; `landingEnabled`
     implies `captureEnabled` by construction, because the only door into
     DESCENT is the capture lane's PARK dwell. NEW is the four-phase tail
     DESCENT (MechJeb `LandUntargeted`, warp-PASSIVE - MechJeb's landing states
     own the warp via the shared `Core.Node.Autowarp` flag the runner sets
     explicitly) -> LANDED-SETTLE (throttle cut, autopilot released, SAS held,
     rails 1x, a held settled dwell) -> SURFACE-COMMIT (the same route-1
     mid-mission seam CommitTree) -> SURFACE-COMMITTED.
   - NEW SURFACES (the reason the lane exists): terminal classification `Landed`
     for a foreign-body tree, SURFACE-class TrackSections OFF Kerbin (the
     classifier's AIRLESS `Approach -> Surface*` path, unreachable where an
     atmosphere classifies first), landing-leg part events, and the
     landed-vessel ghost / playback surface. NEW registry cells: D1
     `commit-landed-foreign-body`, D4 `surface-stationary` (B13) and D4
     `surface-mobile` (B14) - each claimed by the scenario that MEASURED it, and
     each gated by a class-specific log token so neither can be satisfied by the
     other's reading. Both D4 values were previously unclaimed by any scenario.
   - LIVENESS: DESCENT carries FOUR distinctly named give-ups on top of its
     GAME budget - `landing-autopilot-not-enabled` (COMMANDED-vs-OBSERVED off
     `LandingAutopilot.Enabled`), `landing-no-progress` (the independent
     altitude-trend channel, with a separate `altitude-unreadable` name;
     debounced over `LANDING_STALL_DEBOUNCE_FRAMES`, and disarmed while its
     anchor sits below `landingProgressMinDropMeters` AGL where the drop it
     asks for does not exist below the craft),
     `landing-touchdown-timeout` and `landing-vessel-lost` (a crash must read
     as neither a timeout nor a success). LANDED-SETTLE adds
     `landed-never-stable`.
     WHERE THE TOUCHDOWN CARVE-OUT ACTUALLY LIVES (corrected 2026-07-26): the
     hazard is real - MechJeb's `FinalDescent` calls `StopLanding()` on the
     frame it observes `LandedOrSplashed`, so an observed FALSE after touchdown
     is the module reporting SUCCESS - but the guarantee is provided by
     `b5_decide`'s ORDERING, which exits DESCENT on the first landed situation
     BEFORE the supervisor runs. `classify_landing_autopilot` is therefore only
     ever called with `touched_down=False` in flight, and its own touchdown
     conjunct is an order-independent backstop for callers that do not own that
     ordering, not a live path. Both halves are pinned by their own cell; the
     earlier "REQUIRED, not defensive" wording described the hazard correctly
     and the implementation incorrectly.
   - ONE KNOWN GAP, filed rather than papered over (full text in
     `todo-and-known-bugs.md`): DESCENT has no WALL-time bound of its own,
     because the `warp-liveness-starved` floor is armed only by OUR OWN native
     warp and arming it across a phase that legitimately runs at 1x would
     false-flake a healthy landing. It is theoretical on the current pin -
     MechJeb issued zero warp commands across both descents, so there is no
     MechJeb warp to wedge. (The lane originally filed a SECOND gap, that the
     commit-terminal line could not name the body for a `Landed` terminal. That
     one was CLOSED by the Parsek fix described above, not carried.)
   - COVERAGE HONESTY: the DESCENT autopilot supervisor's debounce / re-issue /
     DEAD ladder has NO live coverage - neither flight emitted a non-zero
     `landingApDownStreak` or `landingApReissues` line, because
     `LandingAutopilot.Enabled` read 1 on every DESCENT frame THE SUPERVISOR
     EVALUATED. Not on every polled frame, and the difference is worth stating:
     the archived telemetry shows `landAP=0` on B13's PARK -> DESCENT entry frame
     (ut 21,734.345, decided in PARK, before the engage went out) and on the
     TOUCHDOWN frame of BOTH flights (B13 ut 23,088.285, B14 ut 278,581.702) -
     MechJeb disabling its own module on the landed frame, exactly as the
     touchdown-before-supervisor ordering in `b5_decide` assumes. Same for all
     four named DESCENT give-ups and `landed-never-stable`: never fired live. What
     is live-proven is the happy path plus the two exit gates it crossed
     (touchdown detection on the frame MechJeb disables its own module, and the
     settled dwell).
     WHAT THE LADDER DOES CARRY (2026-07-26): the whole debounce -> bounded
     re-issue -> named DEAD fast-fail now runs through the REAL fly loop
     against a scripted control
     (`test_shells.LandingAutopilotLadderFlightTests`, 6 cells), so everything
     on OUR side of the seam is measured rather than assumed - the debounce
     DEPTH (a flickering channel never reaches a re-issue, which the
     all-disabled cells could not distinguish from a 1-frame debounce), the
     re-issue ACTION reaching the seam carrying the spec's vehicle
     configuration, the bound on how many can be issued, the give-up NAME, and
     the `landingApDownStreak` / `landingApReissues` gate lines an operator
     would grep. Two mutations were used to prove the cells bite: a 1-frame
     debounce reds 2 cells, an unbounded re-issue reds 3. That leaves MechJeb's
     OWN behaviour as the only untested variable, which is exactly what a live
     firing would add and what no harness-side test can substitute for. Do not
     promote the ladder to LIVE-PROVEN on the strength of these cells.
   - PINS CLOSED: four of the five first-flight pins are closed from measured
     data (recordings count re-pinned `{8, 9}` -> `{8, 8}`;
     `descentTimeoutSeconds` trimmed 3000 -> 2200 against measured spans of
     1,353.9 / 1,381.3 game-s; `landedMaxHorizontalSpeedMps` tightened 1.0 ->
     0.5 against a worst measured 0.195 m/s, with the vertical floor left at
     1.0 by decision; `landedDwellSeconds` confirmed at 120). The fifth, the
     wall budgets, is closed AS SIZED: B13 finished at 55% of its mission
     budget and B14 at 50%, so B13 5000 / 5600 and B14 4200 / 4800 stand.
     B13 IS the MOST EXPENSIVE scenario in the suite (2,825 s measured against
     BDOCK-1's 2,164 s).
4. Ledger campaign resumption once career fixtures exist (L1 -> L2+): the
   initiative's END GOAL.
5. B-DOCK first flight - the docking/rendezvous lane (dock-undock recording
   structure) is now IMPLEMENTED (`autotest-bdock-impl`); remaining is the
   headless fixture-forge run (`FORGE-bdock-station` -> harvest -> commit
   `bdock-station-pad`), re-tier BDOCK-1 pending-fixture -> nightly, and the
   first flight (P1-P9 live-proves). It unlocks the D10 route-candidate +
   D5 cross-tree-dock/undock-split recording surface.
6. Eve lane: B15-eve-flyby is LIVE-PROVEN (2026-07-26, full scenario PASS on
   attempt 1 after seven flights; 86 `nextBody=Eve` reads against zero on every
   earlier attempt, flyby periapsis 22,032,532 m, exit to Sun). The INWARD
   unknown is ANSWERED - MechJeb plans an inner window fine, but its interplanetary
   ejection planner sizes the burn at the parking orbit's SEMI-MAJOR AXIS while
   burning at a different radius, so an eccentric park under-ejects badly
   (MEASURED: the same planner priced the same Eve ejection at 652.843 m/s from
   flight 3's ecc-0.08495 park and 775.873 m/s from flight 5's round one, and
   that 123.0 m/s shortfall left the heliocentric leg missing Eve's orbit by
   2.46e9 m). Fixed by the param-gated park round-out trim (`parkTrimEccMax`),
   with a plan-time `reachesTargetOrbit=` verdict so a mis-aimed transfer is
   named at the plan rather than 11.8M game seconds later. See the todo doc's
   B15/B16 section for the full derivation. B16 inherits the fix and still needs
   its first run - it sits at `tier = "operator"` until that flight is green
   (an unflown 4,700 s x retry lane would red a nightly sweep for ~2.6 h a
   night), with a PROMOTE note in its spec. The real Kerbin->Eve
   ejection-window wait (the only budget term with a 14,700,000 game-second
   range) MEASURES at ~11.83e6 game seconds.
7. Candidates (unscheduled): stock-award pattern
   rewrite, nightly rotation shakedown, EVA registry growth (D5/D12 cells),
   an orbital-rendezvous-dock D10 registry value + a same-craft-twice
   identity D18 value (the two B-DOCK coverage gaps).
