"""W8b-2: extract the decision blocks of the RestoreActiveTreeFromPending coroutine.

Usage: extract_restore_active_tree_from_pending.py <ParsekFlight.TreeRestore.cs at the PR
base> <out path>

The coroutine keeps every yield, the two Unity reads (UnityEngine.Time.time and
FlightGlobals.ActiveVessel), the reentrancy guard's try / finally, the instance-state
mutations (activeTree, recorder) and the order of every call. Eight yield-free blocks
between the yields move, line for line from the base (re-indented), into internal static
units placed after the coroutine:

  ShouldRestorePendingTreeOnFlightReady      the entry gate; `yield break` -> `return false`
  LogRewindInvokeContextWaitOutcome          the timed-out / waited log after the marker wait
  ApplyInPlaceContinuationMarkerSwap         the in-place continuation marker swap; returns
                                             the (possibly swapped) active recording id
  TryResolvePendingRestoreActiveRecording    the unresolvable active recording guard
  MatchPendingRestoreTarget                  one frame of the vessel match (pid, name, EVA
                                             parent walk); the live vessel's three fields
                                             come in as plain values
  ReportPendingRestoreGiveUp                 the give-up warn and the Re-Fly raise
  RemapRestoredRecordingPid                  the PID remap
  ApplyPendingActiveTreeResumeRewindSave     the PARSEK_ACTIVE_TREE rewind save hand-off

The script asserts each range's anchor lines, applies only the substitutions listed in
EDITS (each asserted to hit the expected number of lines), and prints every base line of
the method that is missing from the result.
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


expect(485, 'IEnumerator RestoreActiveTreeFromPending()')
expect(493, 'RecorderStateLog.RecState("Restore:start"')
expect(494, '// Re-Fly retry carve-out')
expect(516, 'bool acceptFinalizedForReFly = ShouldAcceptFinalizedPendingTreeForReFlyRetry(')
expect(528, 'yield break;')
expect(536, '}')
expect(538, 'var tree = PendingTreeSlot.PendingTree;')
expect(554, 'if (RewindInvokeContext.Pending)')
expect(557, 'const int MaxMarkerWaitFrames = 300;')
expect(561, 'yield return null;')
expect(563, 'if (RewindInvokeContext.Pending)')
expect(575, '}')
expect(576, '}')
expect(578, '// Bug #585: in-place continuation Re-Fly carve-out.')
expect(649, '}')
expect(651, 'if (string.IsNullOrEmpty(activeRecId)')
expect(652, '|| !tree.Recordings.TryGetValue(activeRecId, out var activeRec))')
expect(657, 'yield break;')
expect(658, '}')
expect(693, 'while (UnityEngine.Time.time < deadline)')
expect(695, 'var v = FlightGlobals.ActiveVessel;')
expect(696, 'if (v != null)')
expect(698, 'string liveGuid = v.id != Guid.Empty ? v.id.ToString("N") : null;')
expect(709, 'refusedFreshRolloutPid = v.persistentId;')
expect(710, 'break;')
expect(729, 'matched = v;')
expect(732, 'break;')
expect(743, 'matched = v;')
expect(746, 'break;')
expect(754, 'break;')
expect(780, 'matched = v;')
expect(788, 'tree.ActiveRecordingId = matchedRecId;')
expect(789, 'break;')
expect(793, 'if (matched != null) break;')
expect(794, '}')
expect(795, 'yield return null;')
expect(812, 'if (matched == null)')
expect(814, 'ParsekLog.Warn("Flight",')
expect(850, '}')
expect(851, 'yield break;')
expect(861, 'uint oldPid = activeRec.VesselPersistentId;')
expect(862, 'uint newPid = matched.persistentId;')
expect(863, 'if (oldPid != newPid)')
expect(874, '}')
expect(894, '// Restore recorder state persisted in the PARSEK_ACTIVE_TREE node')
expect(895, 'if (!string.IsNullOrEmpty(ParsekScenario.pendingActiveTreeResumeRewindSave))')
expect(901, '}')
expect(952, 'DispatchConsumeIntentIfArmed();')
expect(953, '}')
assert ln(953) == '        }'

I8, I12, I16, I20, I24 = ' ' * 8, ' ' * 12, ' ' * 16, ' ' * 20, ' ' * 24

# ---- the coroutine shell ----------------------------------------------------------------
main = []
main += rng(485, 493)
main += [
    I12 + 'if (!ShouldRestorePendingTreeOnFlightReady())',
    I16 + 'yield break;',
]
main += rng(537, 562)
main += [I16 + 'LogRewindInvokeContextWaitOutcome(markerWaitFrame, MaxMarkerWaitFrames);']
main += rng(576, 577)
main += [I12 + 'activeRecId = ApplyInPlaceContinuationMarkerSwap(tree, activeRecId);']
main += rng(650, 650)
main += [
    I12 + 'if (!TryResolvePendingRestoreActiveRecording(tree, activeRecId, out Recording activeRec))',
    I16 + 'yield break;',
]
main += rng(659, 697)
main += [
    I20 + 'PendingRestoreTargetMatch match = MatchPendingRestoreTarget(',
    I24 + 'tree,',
    I24 + 'activeRec,',
    I24 + 'activeRecId,',
    I24 + 'targetName,',
    I24 + 'targetPid,',
    I24 + 'v.persistentId,',
    I24 + 'v.vesselName,',
    I24 + 'v.id,',
    I24 + 'ref loggedGuidReject,',
    I24 + 'loggedParentGuidRejects,',
    I24 + 'ref matchedRec,',
    I24 + 'ref matchedRecId);',
    I20 + 'if (match == PendingRestoreTargetMatch.RefusedFreshRollout)',
    I20 + '{',
    I24 + 'refusedFreshRolloutPid = v.persistentId;',
    I24 + 'break;',
    I20 + '}',
    I20 + 'if (match == PendingRestoreTargetMatch.Matched)',
    I20 + '{',
    I24 + 'matched = v;',
    I24 + 'break;',
    I20 + '}',
]
main += rng(794, 813)
main += [I16 + 'ReportPendingRestoreGiveUp(tree, activeRecId, targetName, targetPid);']
main += rng(851, 862)
main += [I12 + 'RemapRestoredRecordingPid(tree, activeRec, activeRecId, oldPid, newPid, targetName);']
main += rng(875, 894)
main += [I12 + 'ApplyPendingActiveTreeResumeRewindSave(recorder);']
main += rng(902, 953)

# ---- the units --------------------------------------------------------------------------
gate_body = rng(494, 527) + [I16 + 'return false;'] + rng(529, 536) + [I12 + 'return true;']
u_gate = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Entry gate of <see cref="RestoreActiveTreeFromPending"/>: true when a pending tree is',
    I8 + '/// parked in Limbo, or is Finalized while an in-place Re-Fly continuation is active (the',
    I8 + '/// acceptance is logged). False, with the skip logged, ends the coroutine.',
    I8 + '/// </summary>',
    I8 + 'internal static bool ShouldRestorePendingTreeOnFlightReady()',
    I8 + '{',
] + gate_body + [I8 + '}']

wait_body = sub(dedent(rng(563, 575), 4), 'MaxMarkerWaitFrames', 'maxMarkerWaitFrames', 1)
u_wait = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Logs how the restore coroutine\'s wait for <see cref="RewindInvokeContext"/> to clear',
    I8 + '/// ended: a warn when it is still pending after the frame budget, otherwise the number',
    I8 + '/// of frames waited.',
    I8 + '/// </summary>',
    I8 + 'internal static void LogRewindInvokeContextWaitOutcome(',
    I12 + 'int markerWaitFrame,',
    I12 + 'int maxMarkerWaitFrames)',
    I8 + '{',
] + wait_body + [I8 + '}']

u_swap = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Bug #585 in-place continuation carve-out of the restore coroutine: reconciles the',
    I8 + '/// in-place fork into the pending tree and, when the live Re-Fly marker names a',
    I8 + '/// recording of this tree, points the tree (and its background map) at it. Returns the',
    I8 + '/// active recording id the coroutine waits on: the marker\'s recording after a swap, the',
    I8 + '/// given id otherwise.',
    I8 + '/// </summary>',
    I8 + 'internal static string ApplyInPlaceContinuationMarkerSwap(',
    I12 + 'RecordingTree tree,',
    I12 + 'string activeRecId)',
    I8 + '{',
] + rng(578, 649) + [I12 + 'return activeRecId;', I8 + '}']

resolve_body = (
    sub(rng(651, 653), 'out var activeRec)', 'out activeRec)', 1)
    + [I16 + 'activeRec = null;']
    + rng(654, 656)
    + [I16 + 'return false;']
    + rng(658, 658)
    + [I12 + 'return true;'])
u_resolve = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Resolves the pending tree\'s active recording for the restore coroutine. False, with a',
    I8 + '/// warn, when the id is empty or not in the tree: the tree stays in Limbo.',
    I8 + '/// </summary>',
    I8 + 'internal static bool TryResolvePendingRestoreActiveRecording(',
    I12 + 'RecordingTree tree,',
    I12 + 'string activeRecId,',
    I12 + 'out Recording activeRec)',
    I8 + '{',
] + resolve_body + [I8 + '}']

# The vessel-match frame: control flow edited by base line number.
edited = []
for n in range(698, 793):
    s = ln(n)
    if n == 709:
        continue
    if n == 710:
        edited.append(I24 + 'return PendingRestoreTargetMatch.RefusedFreshRollout;')
        continue
    if n in (729, 743, 780):
        continue
    if n in (732, 746):
        edited.append(I24 + 'return PendingRestoreTargetMatch.Matched;')
        continue
    if n == 789:
        edited.append(' ' * 28 + 'return PendingRestoreTargetMatch.Matched;')
        continue
    edited.append(s)
edited.append(I20 + 'return PendingRestoreTargetMatch.None;')
edited = dedent(edited, 8)
edited = sub(edited, 'v.id', 'liveVesselId', 2)
edited = sub(edited, 'v.persistentId', 'livePid', 6)
edited = sub(edited, 'v.vesselName', 'liveVesselName', 4)
u_match = [
    '',
    I8 + '/// <summary>What one polled frame of the restore coroutine\'s vessel match found.</summary>',
    I8 + 'internal enum PendingRestoreTargetMatch',
    I8 + '{',
    I12 + '/// <summary>No match this frame; the coroutine polls again next frame.</summary>',
    I12 + 'None = 0,',
    I12 + '/// <summary>The live vessel is the recording (or an EVA parent of it): adopt it.</summary>',
    I12 + 'Matched = 1,',
    I12 + '/// <summary>The live vessel is the scene-entry fresh rollout: never adopt it.</summary>',
    I12 + 'RefusedFreshRollout = 2,',
    I8 + '}',
    '',
    I8 + '/// <summary>',
    I8 + '/// One polled frame of the restore coroutine\'s vessel match against the live active',
    I8 + '/// vessel (pid, launch guid and name passed in): refuses the scene-entry fresh rollout,',
    I8 + '/// then matches the active recording by pid, by name for an identity-less recording, and',
    I8 + '/// by walking its EVA parent chain, every acceptance gated on the launch guid. A parent',
    I8 + '/// match points <paramref name="matchedRec"/> / <paramref name="matchedRecId"/> and the',
    I8 + '/// tree\'s active recording at the parent. The two latches keep each guid reject logged',
    I8 + '/// once across frames.',
    I8 + '/// </summary>',
    I8 + 'internal static PendingRestoreTargetMatch MatchPendingRestoreTarget(',
    I12 + 'RecordingTree tree,',
    I12 + 'Recording activeRec,',
    I12 + 'string activeRecId,',
    I12 + 'string targetName,',
    I12 + 'uint targetPid,',
    I12 + 'uint livePid,',
    I12 + 'string liveVesselName,',
    I12 + 'Guid liveVesselId,',
    I12 + 'ref bool loggedGuidReject,',
    I12 + 'HashSet<string> loggedParentGuidRejects,',
    I12 + 'ref Recording matchedRec,',
    I12 + 'ref string matchedRecId)',
    I8 + '{',
] + edited + [I8 + '}']

u_giveup = [
    '',
    I8 + '/// <summary>',
    I8 + '/// The restore coroutine gave up waiting for the vessel: warns that the tree stays in',
    I8 + '/// Limbo and, when an in-place Re-Fly session is live, raises that nothing records into',
    I8 + '/// its provisional (R1-EMPTY-PROVISIONAL detection point 1).',
    I8 + '/// </summary>',
    I8 + 'internal static void ReportPendingRestoreGiveUp(',
    I12 + 'RecordingTree tree,',
    I12 + 'string activeRecId,',
    I12 + 'string targetName,',
    I12 + 'uint targetPid)',
    I8 + '{',
] + dedent(rng(814, 850), 4) + [I8 + '}']

u_remap = [
    '',
    I8 + '/// <summary>',
    I8 + '/// PID remap after the restore matched its vessel: when KSP regenerated the persistentId',
    I8 + '/// across the quickload, the recording takes the new pid and the background map entry',
    I8 + '/// keyed by the old pid moves to it (dropped when it was the active recording\'s own).',
    I8 + '/// </summary>',
    I8 + 'internal static void RemapRestoredRecordingPid(',
    I12 + 'RecordingTree tree,',
    I12 + 'Recording activeRec,',
    I12 + 'string activeRecId,',
    I12 + 'uint oldPid,',
    I12 + 'uint newPid,',
    I12 + 'string targetName)',
    I8 + '{',
] + rng(863, 874) + [I8 + '}']

u_rewind = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Hands the rewind save name the PARSEK_ACTIVE_TREE node carried to the restored',
    I8 + '/// recorder and consumes it.',
    I8 + '/// </summary>',
    I8 + 'internal static void ApplyPendingActiveTreeResumeRewindSave(FlightRecorder recorder)',
    I8 + '{',
] + rng(895, 901) + [I8 + '}']

units = u_gate + u_wait + u_swap + u_resolve + u_match + u_giveup + u_remap + u_rewind
new = rng(1, 484) + main + units + L[953:]
open(out, 'w', encoding='utf-8', newline='').write('\n'.join(new))

old_lines = collections.Counter(s.strip() for s in rng(485, 953) if s.strip())
new_lines = collections.Counter(s.strip() for s in main + units if s.strip())
missing = old_lines - new_lines
print('lines of the base method not in the new text:')
for k, v in sorted(missing.items()):
    print('  %d x %s' % (v, k))
