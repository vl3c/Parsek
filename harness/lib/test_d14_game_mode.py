"""Pins every spec's D14 GAME-MODE claim to its own fixture's `Mode =` line.

WHY THIS EXISTS. D14's `sandbox` / `career` / `science-mode` values are the only
registry values a spec cannot demonstrate by anything it says about itself: the mode
is a property of the SAVE its `fixture.saveTemplate` stages, written by KSP into the
GAME node as `Mode = SANDBOX|CAREER|SCIENCE_SANDBOX`. Until this module the link was
convention only - a spec could claim `career` while staging a sandbox fixture, the
coverage report would count the cell as covered, and the flight would prove nothing
about career mode. Nothing anywhere read the two facts together.

WHAT IT PINS. For every committed spec that claims a game-mode value:
  - the fixture template resolves to a committed directory with a `persistent.sfs`;
  - that save's FIRST TOP-LEVEL `Mode` line maps (via `hlib.d14_game_mode_value`) to
    exactly the value claimed;
  - a spec claims AT MOST ONE game mode (a save has exactly one `Mode`).

WHAT IT SKIPS, AND WHY. Operator-local fixtures (`hlib.is_local_fixture_template`,
the `fixtures/local-saves/` prefix used by the GUI census) are gitignored by
construction: the directory is absent on every machine but the operator's, so the
claim is unresolvable here in the same way the other per-spec cells that read a
template off disk are. Those specs are collected and reported by id, never silently
dropped, and the cell asserts the SKIPPED SET IS EXACTLY THE LOCAL-FIXTURE SET - so a
spec that acquires a game-mode claim with no fixture at all reds here rather than
disappearing into the skip list.

THE MODE PARSE IS NOT A SECOND COPY. It reuses `harvest_bdock_station.read_game_mode`,
the tool that stamps committed fixtures' titles from the same line. That parser bounds
the indent to one tab on purpose (`Mode` is a common value name inside PART modules);
a looser regex here would read a part's setting and pass a wrong claim.

Stdlib only; no KSP, no network. ASCII only. Runnable with::

    cd harness && python -m unittest discover -s lib -q
"""

import glob
import os
import sys
import tomllib
import unittest

import hlib

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(LIB_DIR)
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")

if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)

import harvest_bdock_station as harvest  # noqa: E402


def _load_specs():
    """Every committed scenario spec as (path, parsed dict), sorted by path."""
    out = []
    for path in sorted(glob.glob(os.path.join(SCENARIOS_DIR, "*.toml"))):
        with open(path, "rb") as fh:
            out.append((path, tomllib.load(fh)))
    return out


def _save_template(spec):
    return (spec.get("fixture", {}) or {}).get("saveTemplate") or ""


def _fixture_sfs_path(save_template):
    """The committed fixture's `persistent.sfs`, resolved the way the runner
    resolves a template: relative to `harness/`."""
    return os.path.join(HARNESS_ROOT, save_template.replace("/", os.sep),
                        "persistent.sfs")


class D14GameModeClaimsMatchTheirFixtures(unittest.TestCase):

    def test_every_game_mode_claim_agrees_with_its_fixture_mode(self):
        checked, skipped = [], []
        for path, spec in _load_specs():
            sid = spec.get("id") or os.path.basename(path)
            claims = hlib.claimed_d14_game_modes(spec)
            if not claims:
                continue
            self.assertEqual(
                1, len(claims),
                "%s claims %d D14 game modes (%s); a save has exactly one `Mode`"
                % (sid, len(claims), ", ".join(claims)))
            template = _save_template(spec)
            if hlib.is_local_fixture_template(template):
                skipped.append(sid)
                continue
            self.assertTrue(
                template,
                "%s claims D14 %s but stages no fixture.saveTemplate, so nothing "
                "witnesses the game mode" % (sid, claims[0]))
            sfs = _fixture_sfs_path(template)
            self.assertTrue(
                os.path.isfile(sfs),
                "%s claims D14 %s but its fixture %r has no persistent.sfs at %s"
                % (sid, claims[0], template, sfs))
            with open(sfs, "r", encoding="utf-8", errors="replace") as fh:
                raw_mode = harvest.read_game_mode(fh.read())
            value = hlib.d14_game_mode_value(raw_mode)
            self.assertIsNotNone(
                value,
                "%s: fixture %s carries `Mode = %r`, which names no D14 game-mode "
                "value" % (sid, template, raw_mode))
            self.assertEqual(
                claims[0], value,
                "%s claims D14 %r but its fixture %s is `Mode = %s` (%r). Fix the "
                "CLAIM or the FIXTURE - they are the same assertion."
                % (sid, claims[0], template, raw_mode, value))
            checked.append(sid)

        # The population is real, not an empty sweep that would pass on a glob typo.
        self.assertGreater(len(checked), 200,
                           "expected the bulk of the spec corpus to claim a game "
                           "mode; only %d resolved" % len(checked))

        # The skip list is EXACTLY the operator-local set, by derivation not by name.
        local = [(spec.get("id") or os.path.basename(p), _save_template(spec))
                 for p, spec in _load_specs()
                 if hlib.claimed_d14_game_modes(spec)
                 and hlib.is_local_fixture_template(_save_template(spec))]
        self.assertEqual(sorted(sid for sid, _ in local), sorted(skipped))
        for sid, template in local:
            # The stated reason a skip carries: the staging command that would
            # produce the absent fixture.
            self.assertIsNotNone(hlib.local_fixture_hint(template),
                                 "%s skipped without a staging hint" % sid)


class D14GameModeTableIsHonest(unittest.TestCase):
    """The mapping itself: the three registry values, and no guessing."""

    def test_the_table_covers_exactly_the_registry_game_mode_values(self):
        with open(os.path.join(HARNESS_ROOT, "coverage", "registry.toml"),
                  "rb") as fh:
            registry = tomllib.load(fh)
        d14 = set(registry["D14"]["values"])
        self.assertEqual(hlib.D14_GAME_MODE_VALUES, d14 & hlib.D14_GAME_MODE_VALUES)
        self.assertEqual({"sandbox", "career", "science-mode"},
                         set(hlib.D14_GAME_MODE_VALUES))

    def test_unreadable_modes_resolve_to_none_rather_than_a_guess(self):
        self.assertEqual("career", hlib.d14_game_mode_value(" career "))
        self.assertEqual("science-mode",
                         hlib.d14_game_mode_value("SCIENCE_SANDBOX"))
        for junk in ("", None, "SCIENCE", "Follow", "SANDBOX_MODE"):
            self.assertIsNone(hlib.d14_game_mode_value(junk))

    def test_claims_reader_ignores_the_other_d14_axes(self):
        spec = {"dimensionsCovered": {"D14": ["kerbin", "scene-flight", "sandbox"]}}
        self.assertEqual(["sandbox"], hlib.claimed_d14_game_modes(spec))
        self.assertEqual([], hlib.claimed_d14_game_modes({}))


if __name__ == "__main__":
    unittest.main()
