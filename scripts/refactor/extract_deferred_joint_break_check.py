"""W8b-2: extract the decision blocks of the DeferredJointBreakCheck coroutine.

Usage: extract_deferred_joint_break_check.py <ParsekFlight.SplitBranching.cs at the PR base>
<out path>

The coroutine keeps its one yield, every Unity / FlightGlobals read (ActiveVessel, Vessels,
FindVesselByPid, the live snapshot and trajectory sample), every instance-state mutation, the
try / finally and the order of every call. Six yield-free blocks move, line for line from the
base (re-indented), into internal static units placed after the coroutine:

  ClassifyDeferredScanCandidate          the joint-break scan's per-vessel prefilter (recorded
                                         pid, ghost map, pre-break snapshot, background map,
                                         already captured, foreign decouple origin); each
                                         `continue` becomes a DeferredScanCandidate return
  ReportDeferredScanForeignOriginSkips   the foreign-origin skip summary line
  DescribeDeferredSplitCheckSource       the decouple-only / joint-break source label
  EmitWithinSegmentBreakageSegmentEvents the PartDestroyed segment events for the Decoupled
                                         events among the parked recorder's last five
  ResolveForegroundSplitChildCause       a split child's decouple-created / decouple-only
                                         flags and its split cause
  FormatCoalescerSplitFeedLine           the per-child "Feeding split to coalescer" line

The script asserts each range's anchor lines, applies only the substitutions listed below
(each asserted to hit the expected count), and prints every base line of the coroutine
that is missing from the result.
"""
import collections
import sys

src, out = sys.argv[1], sys.argv[2]
L = open(src, encoding='utf-8').read().split('\n')


def ln(n):
    return L[n - 1]


def rng(a, b):
    return L[a - 1:b]


def expect(n, text):
    assert ln(n).strip().startswith(text), (n, ln(n), text)


def dedent(lines, k):
    res = []
    for s in lines:
        if s.strip() == '':
            res.append('')
        else:
            assert s.startswith(' ' * k), s
            res.append(s[k:])
    return res


def sub(lines, old, new, count):
    hits = sum(s.count(old) for s in lines)
    assert hits == count, (old, hits, count)
    return [s.replace(old, new) for s in lines]


expect(1649, 'internal IEnumerator DeferredJointBreakCheck()')
expect(1727, 'int foreignOriginSkipped = 0;')
expect(1731, 'for (int i = 0; i < FlightGlobals.Vessels.Count; i++)')
expect(1733, 'Vessel v = FlightGlobals.Vessels[i];')
assert ln(1734).strip() == 'if (v == null || v.persistentId == recordedPid) continue;', ln(1734)
expect(1735, 'if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;')
expect(1749, '// Skip a fragment that came off ANOTHER vessel')
expect(1752, 'if (SegmentBoundaryLogic.IsForeignDecoupleOrigin(')
assert ln(1755).strip() == 'foreignOriginSkipped++;', ln(1755)
assert ln(1756).strip() == 'continue;', ln(1756)
assert ln(1757).strip() == '}', ln(1757)
assert ln(1758).strip() == '', ln(1758)
expect(1759, 'if (!ShouldIncludeVesselInDeferredBreakupScan(v, out string rejectReason))')
expect(1777, 'if (foreignOriginSkipped > 0)')
assert ln(1783).strip() == '}', ln(1783)
assert ln(1784).strip() == '', ln(1784)
expect(1785, 'string splitCheckSource =')
expect(1788, ': "joint-break";')
expect(1813, '// Emit SegmentEvent.PartDestroyed for parts that broke off')
expect(1815, 'if (pendingSplitRecorder != null)')
assert ln(1831).strip() == '}', ln(1831)
assert ln(1832).strip() == '', ln(1832)
expect(1833, 'SplitBrancher.ResumeSplitRecorder(pendingSplitRecorder, "joint break was within-segment')
expect(1875, '// Classify decoupler-initiated (intentional staging / decoupler fire)')
expect(1881, 'bool childWasDecoupleCreated = decoupleControllerStatus != null')
expect(1883, 'bool triggerWasDecoupleOnly =')
assert ln(1885).strip() == 'uint childRootPartPid = childVessel?.rootPart?.persistentId ?? 0u;', ln(1885)
expect(1886, 'string childSplitCause = SegmentBoundaryLogic.ClassifyForegroundSplitChildCause(')
expect(1892, 'childRootPartPid: childRootPartPid);')
expect(1893, '// Best-available proxy for BranchPoint.DecouplerPartId')
expect(1903, 'ParsekLog.Info("Coalescer",')
expect(1904, '$"Feeding split to coalescer: childPid={newPid}')
assert ln(1912).strip() == ': string.Empty));', ln(1912)
expect(1913, 'crashCoalescer.OnSplitEvent(')
assert ln(2006) == '        }', ln(2006)
assert ln(2007).strip() == '', ln(2007)

I8, I12, I16, I20, I24, I28, I32 = (' ' * n for n in (8, 12, 16, 20, 24, 28, 32))

# ---- the coroutine shell ----------------------------------------------------------------
main = []
main += rng(1649, 1733)
main += [
    I24 + 'if (v == null) continue;',
    I24 + 'DeferredScanCandidate candidate = ClassifyDeferredScanCandidate(',
    I28 + 'v.persistentId,',
    I28 + 'recordedPid,',
    I28 + 'preBreakVesselPids,',
    I28 + 'activeTree,',
    I28 + 'newVesselPids,',
    I28 + 'decoupleOriginByNewPid);',
    I24 + 'if (candidate == DeferredScanCandidate.ForeignOrigin)',
    I24 + '{',
    I28 + 'foreignOriginSkipped++;',
    I28 + 'continue;',
    I24 + '}',
    I24 + 'if (candidate == DeferredScanCandidate.Skip)',
    I28 + 'continue;',
]
main += rng(1758, 1776)
main += [I16 + 'ReportDeferredScanForeignOriginSkips(foreignOriginSkipped, recordedPid);']
main += rng(1784, 1784)
main += [I16 + 'string splitCheckSource = DescribeDeferredSplitCheckSource(pendingDeferredSplitCheckTrigger);']
main += rng(1789, 1812)
main += [I20 + 'EmitWithinSegmentBreakageSegmentEvents(pendingSplitRecorder, branchUT);']
main += rng(1832, 1874)
main += [
    I24 + 'uint childRootPartPid = childVessel?.rootPart?.persistentId ?? 0u;',
    I24 + 'string childSplitCause = ResolveForegroundSplitChildCause(',
    I28 + 'newPid,',
    I28 + 'decoupleControllerStatus,',
    I28 + 'pendingDeferredSplitCheckTrigger,',
    I28 + 'pendingSplitRecorder,',
    I28 + 'childRootPartPid,',
    I28 + 'out bool childWasDecoupleCreated,',
    I28 + 'out bool triggerWasDecoupleOnly);',
]
main += rng(1893, 1902)
main += [
    I24 + 'ParsekLog.Info("Coalescer", FormatCoalescerSplitFeedLine(',
    I28 + 'newPid,',
    I28 + 'hasController,',
    I28 + 'childSplitCause,',
    I28 + 'childWasDecoupleCreated,',
    I28 + 'triggerWasDecoupleOnly,',
    I28 + 'childDecouplerPartId,',
    I28 + 'preSnapshot,',
    I28 + 'preTrajectoryPoint,',
    I28 + 'trajectoryPointSource,',
    I28 + 'branchUT));',
]
main += rng(1913, 2006)

# ---- the units ----------------------------------------------------------------------------
units = []

# ClassifyDeferredScanCandidate: lines 1735-1757 (24 spaces) -> 12 spaces.
scan = dedent(rng(1735, 1757), 12)
scan = sub(scan, 'v.persistentId', 'pid', 5)
# The foreign-origin block keeps its braces; the shell counts the skip.
fi = [i for i, s in enumerate(scan) if s.strip() == 'foreignOriginSkipped++;']
assert len(fi) == 1
assert scan[fi[0] + 1].strip() == 'continue;'
scan[fi[0] + 1] = scan[fi[0] + 1].replace('continue;', 'return DeferredScanCandidate.ForeignOrigin;')
del scan[fi[0]]
scan = sub(scan, 'continue;', 'return DeferredScanCandidate.Skip;', 4)
units += [
    '',
    I8 + '/// <summary>',
    I8 + '/// How the deferred joint-break scan treats one live vessel: <see cref="DeferredScanCandidate.Skip"/>',
    I8 + '/// for the recorded vessel, a ghost map vessel, a vessel that existed before the break, a',
    I8 + '/// background member or a pid already captured; <see cref="DeferredScanCandidate.ForeignOrigin"/>',
    I8 + '/// for a fragment whose decouple origin chain never reaches the recorded vessel (the caller',
    I8 + '/// counts it); <see cref="DeferredScanCandidate.Consider"/> otherwise.',
    I8 + '/// </summary>',
    I8 + 'internal enum DeferredScanCandidate',
    I8 + '{',
    I12 + 'Consider = 0,',
    I12 + 'Skip = 1,',
    I12 + 'ForeignOrigin = 2,',
    I8 + '}',
    '',
    I8 + '/// <summary>',
    I8 + '/// The per-vessel prefilter of <see cref="DeferredJointBreakCheck"/>\'s joint-break scan,',
    I8 + '/// over the vessel\'s persistent id. The live vessel checks (null, the breakup-scan type',
    I8 + '/// filter, the controller test) stay in the coroutine.',
    I8 + '/// </summary>',
    I8 + 'internal static DeferredScanCandidate ClassifyDeferredScanCandidate(',
    I12 + 'uint pid,',
    I12 + 'uint recordedPid,',
    I12 + 'HashSet<uint> preBreakVesselPids,',
    I12 + 'RecordingTree activeTree,',
    I12 + 'List<uint> newVesselPids,',
    I12 + 'Dictionary<uint, uint> decoupleOriginByNewPid)',
    I8 + '{',
    I12 + 'if (pid == recordedPid) return DeferredScanCandidate.Skip;',
]
units += scan
units += [
    '',
    I12 + 'return DeferredScanCandidate.Consider;',
    I8 + '}',
]

# ReportDeferredScanForeignOriginSkips: lines 1777-1783 (16 spaces) -> 12 spaces.
units += [
    '',
    I8 + '/// <summary>',
    I8 + '/// The one summary line of the joint-break scan\'s foreign-origin skips; silent at zero.',
    I8 + '/// </summary>',
    I8 + 'internal static void ReportDeferredScanForeignOriginSkips(int foreignOriginSkipped, uint recordedPid)',
    I8 + '{',
]
units += dedent(rng(1777, 1783), 4)
units += [I8 + '}']

# DescribeDeferredSplitCheckSource: the expression of lines 1786-1788.
units += [
    '',
    I8 + '/// <summary>',
    I8 + '/// The source label of a deferred split check: "decouple-only" when the decouple callback',
    I8 + '/// alone triggered it, "joint-break" otherwise.',
    I8 + '/// </summary>',
    I8 + 'internal static string DescribeDeferredSplitCheckSource(',
    I12 + 'DeferredSplitCheckTrigger pendingDeferredSplitCheckTrigger)',
    I8 + '{',
    I12 + 'return',
]
units += sub(dedent(rng(1786, 1788), 4), ': "joint-break";', ': "joint-break";', 1)
units += [I8 + '}']

# EmitWithinSegmentBreakageSegmentEvents: lines 1813-1831 (20 spaces) -> 12 spaces.
units += [
    '',
    I8 + '/// <summary>',
    I8 + '/// The within-segment breakage events of a deferred split check that found no new vessel.',
    I8 + '/// </summary>',
    I8 + 'internal static void EmitWithinSegmentBreakageSegmentEvents(',
    I12 + 'FlightRecorder pendingSplitRecorder,',
    I12 + 'double branchUT)',
    I8 + '{',
]
units += dedent(rng(1813, 1831), 8)
units += [I8 + '}']

# ResolveForegroundSplitChildCause: lines 1875-1884 and 1886-1892 (24 spaces) -> 12 spaces.
cause_head = dedent(rng(1875, 1884), 12)
cause_head = sub(cause_head, 'bool childWasDecoupleCreated = ', 'childWasDecoupleCreated = ', 1)
cause_head = sub(cause_head, 'bool triggerWasDecoupleOnly =', 'triggerWasDecoupleOnly =', 1)
cause_tail = dedent(rng(1886, 1892), 12)
cause_tail = sub(cause_tail, 'string childSplitCause = ', 'return ', 1)
units += [
    '',
    I8 + '/// <summary>',
    I8 + '/// The split cause of one foreground split child fed to the crash coalescer, with the two',
    I8 + '/// decouple signals it was decided from. The child\'s root part pid is read from the live',
    I8 + '/// vessel by the caller.',
    I8 + '/// </summary>',
    I8 + 'internal static string ResolveForegroundSplitChildCause(',
    I12 + 'uint newPid,',
    I12 + 'Dictionary<uint, bool> decoupleControllerStatus,',
    I12 + 'DeferredSplitCheckTrigger pendingDeferredSplitCheckTrigger,',
    I12 + 'FlightRecorder pendingSplitRecorder,',
    I12 + 'uint childRootPartPid,',
    I12 + 'out bool childWasDecoupleCreated,',
    I12 + 'out bool triggerWasDecoupleOnly)',
    I8 + '{',
]
units += cause_head + cause_tail
units += [I8 + '}']

# FormatCoalescerSplitFeedLine: the message of lines 1904-1912 (28 spaces) -> 16 spaces.
feed = dedent(rng(1904, 1912), 12)
feed = sub(feed, ': string.Empty));', ': string.Empty);', 1)
units += [
    '',
    I8 + '/// <summary>',
    I8 + '/// The "Feeding split to coalescer" line of one split child.',
    I8 + '/// </summary>',
    I8 + 'internal static string FormatCoalescerSplitFeedLine(',
    I12 + 'uint newPid,',
    I12 + 'bool hasController,',
    I12 + 'string childSplitCause,',
    I12 + 'bool childWasDecoupleCreated,',
    I12 + 'bool triggerWasDecoupleOnly,',
    I12 + 'uint childDecouplerPartId,',
    I12 + 'ConfigNode preSnapshot,',
    I12 + 'TrajectoryPoint? preTrajectoryPoint,',
    I12 + 'string trajectoryPointSource,',
    I12 + 'double branchUT)',
    I8 + '{',
    I12 + 'return',
]
units += feed
units += [I8 + '}']

result = L[:1648] + main + units + L[2006:]
open(out, 'w', encoding='utf-8', newline='').write('\n'.join(result))

# Every base line of the coroutine must survive somewhere in the result (stripped).
base_lines = collections.Counter(s.strip() for s in rng(1649, 2006) if s.strip())
new_region = main + units
new_lines = collections.Counter(s.strip() for s in new_region if s.strip())
missing = base_lines - new_lines
print('coroutine lines before: %d, shell lines after: %d, unit lines: %d'
      % (2006 - 1648, len(main), len(units)))
print('base lines missing from the result (each must be a declared substitution):')
for k, n in sorted(missing.items()):
    print('  %dx %s' % (n, k))
