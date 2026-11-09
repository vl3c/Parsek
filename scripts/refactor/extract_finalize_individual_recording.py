"""W8b: extract FinalizeIndividualRecording's phases into named helpers.

Usage: extract_finalize_individual_recording.py <ParsekFlight.Finalization.cs at the PR
base bc6c835fd> <out path>

Every moved body is copied line for line from the base (only re-indented); the three lines
that declared a local which became an out parameter or a helper local are the only edits.
The script asserts each range's anchor lines before slicing and prints every base line of
the method that is missing from the result (exactly those three).
"""
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


expect(399, 'internal static bool FinalizeIndividualRecording(')
expect(409, 'var vesselAccess = liveVesselAccess')
expect(411, '// Set ExplicitStartUT')
expect(429, '// Look up the live vessel once')
expect(439, 'bool isLeaf = rec.ChildBranchPointId == null')
expect(452, 'Vessel finalizeVessel = (isLeaf')
expect(457, 'bool finalizeVesselFound = vesselAccess.IsFound(finalizeVessel);')
expect(481, '}')
expect(482, 'string restoredSceneExitReason = null;')
expect(514, '{')
expect(515, 'ConfigNode vesselSnapshotBefore')
expect(518, 'sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(')
expect(530, '}')
expect(551, 'if (isLeaf && !rec.TerminalStateValue.HasValue)')
expect(553, 'if (finalizeVesselFound)')
expect(555, 'if (isSceneExit)')
expect(574, '}')
expect(575, 'else if (!preserveRestoredSceneExitTerminalState')
expect(592, 'else if (isSceneExit)')
expect(594, '// Scene exit: vessel unloaded')
expect(659, '}')
expect(660, 'else')
expect(662, '// Not a scene exit')
expect(677, '}')
expect(678, '}')
expect(715, 'if (isLeaf && rec.TerminalStateValue.HasValue')
expect(717, '{')
expect(718, 'bool preserveSceneExitTerminalOrbit =')
expect(775, '}')
expect(777, 'RecordingEndpointResolver.RefreshEndpointDecision')
expect(784, '// Bug #290d: backfill MaxDistanceFromLaunch')
expect(818, '}')
expect(820, '// Warn if leaf has no playback data.')
expect(864, 'return sceneExitLifetimeExtended && sceneExitSuppliedSnapshots;')
expect(865, '}')

I8 = ' ' * 8
I12 = ' ' * 12
I16 = ' ' * 16
I20 = ' ' * 20

main = []
main += rng(399, 410)
main += [
    I12 + 'DefaultFinalizeExplicitUTs(rec, commitUT);',
    '',
    I12 + 'bool isLeaf = ResolveFinalizeLeafVessel(',
    I16 + 'rec,',
    I16 + 'treeContext,',
    I16 + 'findVesselByPid,',
    I16 + 'vesselAccess,',
    I16 + 'out Vessel finalizeVessel,',
    I16 + 'out bool finalizeVesselFound);',
]
main += rng(482, 514)
main += [
    I16 + 'sceneExitLifetimeExtended = ApplySceneExitLifetimeFinalizer(',
    I20 + 'rec,',
    I20 + 'finalizeVessel,',
    I20 + 'commitUT,',
    I20 + 'treeContext,',
    I20 + 'out sceneExitSuppliedSnapshots,',
    I20 + 'out sceneExitSuppliedTerminalOrbit);',
]
main += rng(530, 553)
main += [I20 + 'StampTerminalStateFromLiveVessel(rec, finalizeVessel, isSceneExit, vesselAccess);']
main += rng(575, 591)
main += [ln(592)]
main += [I16 + '{',
         I20 + 'InferSceneExitTerminalState(',
         I20 + '    rec,',
         I20 + '    preserveRestoredSceneExitTerminalState,',
         I20 + '    restoredSceneExitReason);',
         I16 + '}']
main += [ln(660)]
main += [I20 + 'MarkMissingVesselDestroyed(rec);']
main += rng(678, 717)
main += [
    I16 + 'RefreshLeafTerminalOrbit(',
    I20 + 'rec,',
    I20 + 'finalizeVessel,',
    I20 + 'finalizeVesselFound,',
    I20 + 'sceneExitLifetimeExtended,',
    I20 + 'sceneExitSuppliedTerminalOrbit,',
    I20 + 'cacheFinalizationApplied,',
    I20 + 'cacheSuppliedTerminalOrbit,',
    I20 + 'vesselAccess);',
]
main += rng(775, 783)
main += [
    I12 + 'BackfillFinalizeMaxDistance(rec);',
    '',
    I12 + 'LogFinalizeOutcome(rec, isLeaf);',
]
main += rng(864, 865)

helpers = []


def helper(doc, sig, body):
    helpers.append('')
    helpers.extend(I8 + d if d else I8.rstrip() for d in doc)
    helpers.extend(sig)
    helpers.append(I8 + '{')
    helpers.extend(body)
    helpers.append(I8 + '}')


helper(['/// <summary>',
        '/// Defaults ExplicitStartUT to the first point or orbit segment and ExplicitEndUT to',
        '/// the last point (else <paramref name="commitUT"/>) when they are unset.',
        '/// </summary>'],
       [I8 + 'private static void DefaultFinalizeExplicitUTs(Recording rec, double commitUT)'],
       rng(411, 427))

b = rng(429, 481)
b = [s.replace('Vessel finalizeVessel = (isLeaf', 'finalizeVessel = (isLeaf')
      .replace('bool finalizeVesselFound = vesselAccess.IsFound', 'finalizeVesselFound = vesselAccess.IsFound')
     for s in b]
helper(['/// <summary>',
        '/// Whether <paramref name="rec"/> finalizes as a leaf, and its live vessel when it does:',
        '/// a non-final chain segment, and a recording whose live vessel is the terminal spawn',
        '/// of a later committed recording, are not finalized as leaves.',
        '/// </summary>'],
       [I8 + 'private static bool ResolveFinalizeLeafVessel(',
        I12 + 'Recording rec,',
        I12 + 'RecordingTree treeContext,',
        I12 + 'Func<uint, Vessel> findVesselByPid,',
        I12 + 'FinalizationLiveVesselAccess vesselAccess,',
        I12 + 'out Vessel finalizeVessel,',
        I12 + 'out bool finalizeVesselFound)'],
       b + [I12 + 'return isLeaf;'])

c = dedent(rng(515, 529), 4)
c = [s.replace('sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(',
               'bool sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(')
     for s in c]
helper(['/// <summary>',
        '/// Runs the scene-exit incomplete-ballistic finalizer on a leaf with no terminal state.',
        '/// Returns whether it extended the recording, and reports whether it supplied a new',
        '/// vessel snapshot and (when it extended) new terminal orbit metadata.',
        '/// </summary>'],
       [I8 + 'private static bool ApplySceneExitLifetimeFinalizer(',
        I12 + 'Recording rec,',
        I12 + 'Vessel finalizeVessel,',
        I12 + 'double commitUT,',
        I12 + 'RecordingTree treeContext,',
        I12 + 'out bool sceneExitSuppliedSnapshots,',
        I12 + 'out bool sceneExitSuppliedTerminalOrbit)'],
       c + [I12 + 'return sceneExitLifetimeExtended;'])

helper(['/// <summary>',
        '/// Stamps the terminal state of a leaf from its live vessel, captures the terminal',
        '/// orbit and position, and on a commit (not a scene exit) re-snapshots the vessel.',
        '/// </summary>'],
       [I8 + 'private static void StampTerminalStateFromLiveVessel(',
        I12 + 'Recording rec,',
        I12 + 'Vessel finalizeVessel,',
        I12 + 'bool isSceneExit,',
        I12 + 'FinalizationLiveVesselAccess vesselAccess)'],
       dedent(rng(555, 573), 8))

helper(['/// <summary>',
        '/// Scene exit with no live vessel and no cache: infers the terminal state from the',
        '/// trajectory, or skips the inference (restored recording, SubOrbital-only evidence).',
        '/// </summary>'],
       [I8 + 'private static void InferSceneExitTerminalState(',
        I12 + 'Recording rec,',
        I12 + 'bool preserveRestoredSceneExitTerminalState,',
        I12 + 'string restoredSceneExitReason)'],
       dedent(rng(594, 658), 8))

helper(['/// <summary>',
        '/// A commit (not a scene exit) whose leaf vessel is gone: drops the end snapshot and',
        '/// marks the recording Destroyed.',
        '/// </summary>'],
       [I8 + 'private static void MarkMissingVesselDestroyed(Recording rec)'],
       dedent(rng(662, 676), 8))

helper(['/// <summary>',
        '/// Refreshes or backfills the terminal orbit of an orbital leaf (see the call site).',
        '/// </summary>'],
       [I8 + 'private static void RefreshLeafTerminalOrbit(',
        I12 + 'Recording rec,',
        I12 + 'Vessel finalizeVessel,',
        I12 + 'bool finalizeVesselFound,',
        I12 + 'bool sceneExitLifetimeExtended,',
        I12 + 'bool sceneExitSuppliedTerminalOrbit,',
        I12 + 'bool cacheFinalizationApplied,',
        I12 + 'bool cacheSuppliedTerminalOrbit,',
        I12 + 'FinalizationLiveVesselAccess vesselAccess)'],
       dedent(rng(718, 774), 4))

helper(['/// <summary>',
        '/// Backfills MaxDistanceFromLaunch when it is not yet computed.',
        '/// </summary>'],
       [I8 + 'private static void BackfillFinalizeMaxDistance(Recording rec)'],
       rng(784, 818))

helper(['/// <summary>',
        '/// Warns for a leaf with no playback data and logs the finalize summary line.',
        '/// </summary>'],
       [I8 + 'private static void LogFinalizeOutcome(Recording rec, bool isLeaf)'],
       rng(820, 863))

new = rng(1, 398) + main + helpers + L[865:]
open(out, 'w', encoding='utf-8', newline='').write('\n'.join(new))

# Every non-blank line of the base method appears in the new file (glue aside).
import collections
old_lines = collections.Counter(s.strip() for s in rng(399, 865) if s.strip())
new_lines = collections.Counter(s.strip() for s in new[398:] if s.strip())
missing = old_lines - new_lines
print('lines of the base method not in the new text:')
for k, v in sorted(missing.items()):
    print('  %d x %s' % (v, k))
