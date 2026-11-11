"""W8b: extract EnsureActiveRecordingTerminalState's two scene-exit blocks into helpers.

Usage: extract_ensure_active_recording_terminal_state.py <ParsekFlight.Finalization.cs at
the PR base> <out path>

The scene-exit finalizer block and the scene-exit inference block move into private static
helpers, line for line from the base (re-indented). The edits: the three method-level
scene-exit locals become helper locals or an out parameter, and the finalizer block's
`return sceneExitSuppliedSnapshots;` becomes the call site's return, the helper reporting
whether the finalizer extended the recording. The live-vessel branch, the cache fallback
and the not-found log stay where they are. The script asserts each range's anchor lines
and prints every base line of the method missing from the result.
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


expect(109, 'internal static bool EnsureActiveRecordingTerminalState(')
expect(133, 'bool sceneExitLifetimeExtended = false;')
expect(134, 'bool sceneExitSuppliedSnapshots = false;')
expect(135, 'bool sceneExitSuppliedTerminalOrbit = false;')
expect(136, 'bool preserveRestoredSceneExitTerminalState =')
expect(141, 'out string restoredSceneExitReason);')
expect(143, 'if (isSceneExit && !preserveRestoredSceneExitTerminalState)')
expect(144, '{')
expect(145, 'ConfigNode vesselSnapshotBefore = activeRec.VesselSnapshot;')
expect(148, 'sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(')
expect(154, 'sceneExitSuppliedSnapshots =')
expect(157, 'sceneExitSuppliedTerminalOrbit =')
expect(160, 'if (sceneExitLifetimeExtended)')
expect(184, 'return sceneExitSuppliedSnapshots;')
expect(185, '}')
expect(186, '}')
expect(214, 'if (isSceneExit)')
expect(215, '{')
expect(216, '// PR #572 follow-up')
expect(269, 'return false;')
expect(270, '}')
expect(277, '}')
assert ln(277) == '        }'

I8, I12, I16, I20 = ' ' * 8, ' ' * 12, ' ' * 16, ' ' * 20

main = []
main += rng(109, 132)
main += rng(136, 144)
main += [
    I16 + 'if (ApplyActiveRecordingSceneExitLifetime(',
    I20 + 'activeRec,',
    I20 + 'v,',
    I20 + 'commitUT,',
    I20 + 'tree,',
    I20 + 'out bool sceneExitSuppliedSnapshots))',
    I20 + 'return sceneExitSuppliedSnapshots;',
]
main += rng(186, 214)
main += [
    I16 + 'return InferActiveRecordingTerminalStateOnSceneExit(',
    I20 + 'activeRec,',
    I20 + 'preserveRestoredSceneExitTerminalState,',
    I20 + 'restoredSceneExitReason);',
]
main += rng(271, 277)

a = dedent(rng(145, 183), 4)
a = [s.replace('sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(',
               'bool sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(')
      .replace('sceneExitSuppliedTerminalOrbit =', 'bool sceneExitSuppliedTerminalOrbit =')
     for s in a]
helper_a = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Runs the scene-exit incomplete-ballistic finalizer on the active recording and, when',
    I8 + '/// it extended the recording, settles the terminal orbit metadata. Returns whether it',
    I8 + '/// extended the recording; <paramref name="sceneExitSuppliedSnapshots"/> reports whether',
    I8 + '/// it supplied a new vessel snapshot.',
    I8 + '/// </summary>',
    I8 + 'private static bool ApplyActiveRecordingSceneExitLifetime(',
    I12 + 'Recording activeRec,',
    I12 + 'Vessel v,',
    I12 + 'double commitUT,',
    I12 + 'RecordingTree tree,',
    I12 + 'out bool sceneExitSuppliedSnapshots)',
    I8 + '{',
] + a + [I12 + '}', I12 + 'return sceneExitLifetimeExtended;', I8 + '}']

helper_b = [
    '',
    I8 + '/// <summary>',
    I8 + '/// Scene exit with no live vessel and no cache: infers the active recording\'s terminal',
    I8 + '/// state from the trajectory, or skips the inference (restored recording, SubOrbital-only',
    I8 + '/// evidence). Always returns false, the caller\'s result.',
    I8 + '/// </summary>',
    I8 + 'private static bool InferActiveRecordingTerminalStateOnSceneExit(',
    I12 + 'Recording activeRec,',
    I12 + 'bool preserveRestoredSceneExitTerminalState,',
    I12 + 'string restoredSceneExitReason)',
    I8 + '{',
] + dedent(rng(216, 269), 4) + [I8 + '}']

new = rng(1, 108) + main + helper_a + helper_b + L[277:]
open(out, 'w', encoding='utf-8', newline='').write('\n'.join(new))

old_lines = collections.Counter(s.strip() for s in rng(109, 277) if s.strip())
new_lines = collections.Counter(
    s.strip() for s in main + helper_a + helper_b if s.strip())
missing = old_lines - new_lines
print('lines of the base method not in the new text:')
for k, v in sorted(missing.items()):
    print('  %d x %s' % (v, k))
