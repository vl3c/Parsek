"""The far-watch site gate and handler gate (design-far-watch.md section 11.4).

The DLL-free cells drive the pure decider (far_watch_sites.py) over small
synthetic facts models and check the committed allowlist's own shape. The
real-tree cells need PARSEK_ARCH_IL (a Debug Parsek.dll or an .il dump), like
test_ilfacts' and test_ilanalysis' ratchets, and read the same cached parse;
they red on every site finding and prove the gate reds when an entry's count
is lowered by one (the section 17.5 mutation).
"""

import copy
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import far_watch_sites as fws  # noqa: E402
import test_ilfacts  # noqa: E402
import test_semantic  # noqa: E402

ALLOWLIST = Path(__file__).resolve().parent / "far_watch_sites.toml"
IL_ENV = test_semantic.IL_ENV


def method(type_path, name, external=None, calls=None, statics=None, delegates=(), returns="void"):
    calls = dict(calls or {})
    return {
        "type": type_path,
        "name": name,
        "returns": returns,
        "calls": sorted(calls),
        "callCounts": calls,
        "externalMemberCounts": dict(external or {}),
        "staticCounts": dict(statics or {}),
        "delegates": sorted(delegates),
    }


def facts_model(methods, modules=None):
    modules = modules or {}
    types = {}
    for key, entry in methods.items():
        outer = entry["type"].split("/")[0]
        types.setdefault(outer, {"module": modules.get(outer, "Controllers")})
    return {"types": types, "methods": methods}


AV = fws.GET_ACTIVE_VESSEL
GHOST = fws.IS_GHOST_MAP_VESSEL + "(uint32)"
WRAPPER = fws.DEFAULT_WRAPPERS[0]
ON_CHANGE = "Assembly-CSharp|GameEvents::onVesselChange"


def sample_facts():
    return facts_model({
        "Parsek.Flight::Reads()": method("Parsek.Flight", "Reads", external={AV: 2}),
        "Parsek.Flight::Checks(Vessel)": method("Parsek.Flight", "Checks", calls={GHOST: 1},
                                                statics={fws.GHOST_MAP_VESSEL_PIDS: 1}),
        "Parsek.Flight::Restores()": method("Parsek.Flight", "Restores", calls={WRAPPER: 1}),
        WRAPPER: method("Parsek.WatchModeController", "GetActiveVesselSafe", returns="Vessel"),
        fws.DEFAULT_WRAPPERS[1]: method("Parsek.WatchModeController", "ReadActiveVesselCore",
                                        external={AV: 1}, returns="Vessel"),
        fws.DEFAULT_WRAPPERS[2]: method("Parsek.PredictedCrashReconcilerHost", "ReadActiveVesselCore",
                                        external={AV: 1}, returns="Vessel"),
        GHOST: method("Parsek.GhostMapPresence", "IsGhostMapVessel", statics={fws.GHOST_MAP_VESSEL_PIDS: 1},
                      returns="bool"),
        "Parsek.InGameTests.T::Run()": method("Parsek.InGameTests.T", "Run", external={AV: 5}),
    }, modules={"Parsek.InGameTests.T": "InGameTests", "Parsek.GhostMapPresence": "Ghost"})


def sample_config():
    def entry(count, codes):
        return {"count": count, "codes": codes, "audit": "A row"}
    return {
        "wrappers": list(fws.DEFAULT_WRAPPERS),
        "notWrappers": {},
        "sites": {
            ("Parsek.Flight::Reads()", "ActiveVessel"): entry(2, ["CAMERA-OK"]),
            ("Parsek.Flight::Checks(Vessel)", "IsGhostMapVessel"): entry(1, ["DRONE-SAME"]),
            ("Parsek.Flight::Checks(Vessel)", "ghostMapVesselPids"): entry(1, ["DRONE-NA"]),
            ("Parsek.Flight::Restores()", "wrapper"): entry(1, ["WATCH-INTERNAL"]),
            (fws.DEFAULT_WRAPPERS[1], "ActiveVessel"): entry(1, ["WATCH-INTERNAL"]),
            (fws.DEFAULT_WRAPPERS[2], "ActiveVessel"): entry(1, ["PLAYER-SHIP"]),
            (GHOST, "ghostMapVesselPids"): entry(1, ["DRONE-NA"]),
        },
        "guards": list(fws.DEFAULT_GUARDS),
        "handlers": {},
    }


def kinds(findings):
    return sorted(f.split(":", 1)[0] for f in findings)


class SiteDeciderTests(unittest.TestCase):
    def test_a_matching_allowlist_is_green_and_in_game_tests_are_out_of_scope(self):
        self.assertEqual(fws.decide_sites(sample_facts(), sample_config()), [])

    def test_a_site_with_no_entry_reds(self):
        config = sample_config()
        del config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["unlisted"])

    def test_a_new_read_in_a_decided_method_reds_as_a_count(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Flight::Reads()"]["externalMemberCounts"][AV] = 3
        self.assertEqual(kinds(fws.decide_sites(facts, sample_config())), ["count"])

    def test_the_fetch_active_vessel_field_counts_as_an_active_vessel_read(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Flight::Reads()"]["externalMemberCounts"]["Assembly-CSharp|FlightGlobals::activeVessel"] = 1
        self.assertIn("count", kinds(fws.decide_sites(facts, sample_config())))

    def test_a_count_lowered_by_one_reds(self):
        config = sample_config()
        config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]["count"] = 1
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["count"])

    def test_a_renamed_method_reds_as_stale_and_unlisted(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Flight::ReadsRenamed()"] = facts["methods"].pop("Parsek.Flight::Reads()")
        self.assertEqual(kinds(fws.decide_sites(facts, sample_config())), ["stale", "unlisted"])

    def test_an_entry_whose_read_is_gone_reds_as_stale(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Flight::Reads()"]["externalMemberCounts"] = {}
        self.assertEqual(kinds(fws.decide_sites(facts, sample_config())), ["stale"])

    def test_codes_are_checked_against_the_pattern_vocabulary(self):
        config = sample_config()
        config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]["codes"] = ["DRONE-SAME"]
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["unknown-code"])
        config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]["codes"] = ["NOT-A-CODE"]
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["unknown-code"])
        config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]["codes"] = []
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["no-codes"])

    def test_a_ruling_code_reds(self):
        config = sample_config()
        config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]["codes"] = ["RULING"]
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["ruling"])

    def test_an_entry_names_its_audit_row(self):
        config = sample_config()
        config["sites"][("Parsek.Flight::Reads()", "ActiveVessel")]["audit"] = " "
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["no-audit"])

    def test_a_new_vessel_returning_reader_reds_until_declared(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Flight::ReadShip()"] = method("Parsek.Flight", "ReadShip", external={AV: 1},
                                                              returns="Vessel")
        config = sample_config()
        config["sites"][("Parsek.Flight::ReadShip()", "ActiveVessel")] = {
            "count": 1, "codes": ["PLAYER-SHIP"], "audit": "x"}
        self.assertEqual(kinds(fws.decide_sites(facts, config)), ["wrapper"])
        config["notWrappers"]["Parsek.Flight::ReadShip()"] = "read only by its own caller"
        self.assertEqual(fws.decide_sites(facts, config), [])
        config["notWrappers"]["Parsek.Flight::ReadShip()"] = ""
        self.assertEqual(kinds(fws.decide_sites(facts, config)), ["not-wrapper-reason"])

    def test_a_declared_wrapper_makes_its_callers_sites(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Flight::ReadShip()"] = method("Parsek.Flight", "ReadShip", external={AV: 1},
                                                              returns="Vessel")
        facts["methods"]["Parsek.Flight::UsesShip()"] = method(
            "Parsek.Flight", "UsesShip", calls={"Parsek.Flight::ReadShip()": 2})
        config = sample_config()
        config["wrappers"].append("Parsek.Flight::ReadShip()")
        config["sites"][("Parsek.Flight::ReadShip()", "ActiveVessel")] = {
            "count": 1, "codes": ["PLAYER-SHIP"], "audit": "x"}
        findings = fws.decide_sites(facts, config)
        self.assertEqual(kinds(findings), ["unlisted"])
        self.assertIn("UsesShip", findings[0])
        self.assertIn("2 wrapper", findings[0])

    def test_a_wrapper_that_left_the_assembly_reds(self):
        config = sample_config()
        config["wrappers"].append("Parsek.Gone::Read()")
        self.assertEqual(kinds(fws.decide_sites(sample_facts(), config)), ["stale-wrapper"])

    def test_a_type_the_module_map_cannot_place_still_counts(self):
        facts = sample_facts()
        facts["methods"]["Parsek.Loose::Read()"] = method("Parsek.Loose", "Read", external={AV: 1})
        facts["types"]["Parsek.Loose"] = {"module": None}
        self.assertEqual(kinds(fws.decide_sites(facts, sample_config())), ["unlisted"])


class HandlerDeciderTests(unittest.TestCase):
    def handler_facts(self, guarded=True, guard_built=True):
        methods = {
            "Parsek.Flight::Start()": method("Parsek.Flight", "Start", delegates=[
                ON_CHANGE + ">Parsek.Flight::OnChange(Vessel)",
                "Assembly-CSharp|GameEvents::onGamePause>Parsek.Flight::OnPause()",
                "Assembly-CSharp|GameEvents::onPartDie>Parsek.Flight::OnDie(Part)",
            ]),
            "Parsek.Flight::OnChange(Vessel)": method(
                "Parsek.Flight", "OnChange",
                calls={"Parsek.GhostMapPresence::IsGhostOrDroneVessel(uint32)": 1} if guarded else {}),
            "Parsek.Flight::OnPause()": method("Parsek.Flight", "OnPause"),
            "Parsek.Flight::OnDie(Part)": method("Parsek.Flight", "OnDie"),
        }
        if guard_built:
            methods["Parsek.GhostMapPresence::IsGhostOrDroneVessel(uint32)"] = method(
                "Parsek.GhostMapPresence", "IsGhostOrDroneVessel", returns="bool")
        return facts_model(methods)

    def handler_config(self):
        config = sample_config()
        config["handlers"] = {
            "Parsek.Flight::OnChange(Vessel)": {"events": ["onVesselChange"], "status": "guard"},
            "Parsek.Flight::OnDie(Part)": {"events": ["onPartDie"], "status": "exempt", "reason": "no parts"},
        }
        return config

    def test_only_vessel_events_make_handlers(self):
        self.assertEqual(fws.vessel_event_handlers(self.handler_facts()), {
            "Parsek.Flight::OnChange(Vessel)": ["onVesselChange"],
            "Parsek.Flight::OnDie(Part)": ["onPartDie"],
        })

    def test_the_event_set(self):
        for event in ("onVesselChange", "onVesselWillDestroy", "onNewVesselCreated", "onStageActivate",
                      "onPartDie", "onCrewOnEva", "onCrewBoardVessel", "onVesselsUndocking"):
            self.assertTrue(fws.VESSEL_EVENT_RE.match("Assembly-CSharp|GameEvents::" + event), event)
        self.assertTrue(fws.VESSEL_EVENT_RE.match("Assembly-CSharp|GameEvents/VesselSituation::onLaunch"))
        for event in ("onGamePause", "onPartUndock", "onFlightReady", "onPartCouple"):
            self.assertFalse(fws.VESSEL_EVENT_RE.match("Assembly-CSharp|GameEvents::" + event), event)
        self.assertFalse(fws.VESSEL_EVENT_RE.match("Parsek|GameEvents::onVesselChange"))

    def test_a_guarded_handler_is_green(self):
        self.assertEqual(fws.decide_handlers(self.handler_facts(), self.handler_config()), ([], True))

    def test_an_unguarded_handler_reds_once_a_guard_is_built(self):
        findings, armed = fws.decide_handlers(self.handler_facts(guarded=False), self.handler_config())
        self.assertTrue(armed)
        self.assertEqual(kinds(findings), ["unguarded"])

    def test_before_a_guard_member_exists_only_coverage_is_checked(self):
        findings, armed = fws.decide_handlers(self.handler_facts(guarded=False, guard_built=False),
                                              self.handler_config())
        self.assertFalse(armed)
        self.assertEqual(findings, [])

    def test_a_new_handler_reds_until_listed(self):
        config = self.handler_config()
        del config["handlers"]["Parsek.Flight::OnDie(Part)"]
        findings, _armed = fws.decide_handlers(self.handler_facts(), config)
        self.assertEqual(kinds(findings), ["unlisted-handler"])

    def test_stale_and_malformed_entries_red(self):
        config = self.handler_config()
        config["handlers"]["Parsek.Flight::Gone(Vessel)"] = {"events": ["onVesselChange"], "status": "guard"}
        config["handlers"]["Parsek.Flight::OnDie(Part)"]["reason"] = ""
        config["handlers"]["Parsek.Flight::OnChange(Vessel)"]["events"] = ["onVesselLoaded"]
        findings, _armed = fws.decide_handlers(self.handler_facts(), config)
        self.assertEqual(kinds(findings), ["exempt-reason", "handler-events", "stale-handler"])
        config = self.handler_config()
        config["handlers"]["Parsek.Flight::OnChange(Vessel)"]["status"] = "maybe"
        self.assertEqual(kinds(fws.decide_handlers(self.handler_facts(), config)[0]), ["status"])

    def test_a_lambda_handler_stands_for_the_method_that_wrote_it(self):
        # ilfacts keys a lambda's delegate by its owner, so the subscribing method is the handler.
        facts = facts_model({"Parsek.Flight::Start()": method("Parsek.Flight", "Start", delegates=[
            ON_CHANGE + ">Parsek.Flight::Start()"])})
        self.assertEqual(fws.vessel_event_handlers(facts), {"Parsek.Flight::Start()": ["onVesselChange"]})


class AllowlistTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = ALLOWLIST.read_text(encoding="utf-8")
        cls.config = fws.parse_config(cls.text)

    def test_every_committed_entry_has_known_codes_and_an_audit_note(self):
        problems = []
        for (key, pattern), entry in sorted(self.config["sites"].items()):
            problems.extend(fws.check_entry_codes(key, pattern, entry))
            if entry["count"] < 1:
                problems.append("count: %s %s pins %d" % (pattern, key, entry["count"]))
        self.assertEqual(problems, [])

    def test_the_design_wrappers_and_guards_are_declared(self):
        for wrapper in fws.DEFAULT_WRAPPERS:
            self.assertIn(wrapper, self.config["wrappers"])
        self.assertEqual(sorted(self.config["guards"]), sorted(fws.DEFAULT_GUARDS))
        for key, entry in self.config["handlers"].items():
            self.assertIn(entry.get("status"), ("guard", "exempt"), key)
            if entry["status"] == "exempt":
                self.assertTrue(entry.get("reason"), key)

    def test_the_writer_round_trips(self):
        entries = {site: {"count": e["count"], "codes": e["codes"], "audit": e["audit"]}
                   for site, e in self.config["sites"].items()}
        text = fws.format_toml(entries, "x", None, wrappers=self.config["wrappers"],
                               not_wrappers=self.config["notWrappers"], guards=self.config["guards"],
                               handlers=self.config["handlers"])
        again = fws.parse_config(text)
        self.assertEqual(again["sites"], self.config["sites"])
        self.assertEqual(again["handlers"], self.config["handlers"])
        self.assertEqual(again["notWrappers"], self.config["notWrappers"])

    def test_malformed_files_are_refused(self):
        with self.assertRaises(ValueError):
            fws.parse_config("schema = 2\n[wrappers]\nmembers = ['a']\n")
        with self.assertRaises(ValueError):
            fws.parse_config("schema = 1\n[wrappers]\nmembers = []\n")
        with self.assertRaises(ValueError):
            fws.parse_config("schema = 1\n[wrappers]\nmembers = ['a']\n[sites.Other]\n'k' = { count = 1 }\n")
        with self.assertRaises(ValueError):
            fws.parse_config("schema = 1\n[wrappers]\nmembers = ['a']\n[sites.wrapper]\n'k' = { codes = [] }\n")


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_runs_these_cells(self):
        import fnmatch
        import re
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        self.assertTrue(any(fnmatch.fnmatch("test_far_watch_sites.py", p) for p in patterns), patterns)


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        cls.facts, _tooling = test_ilfacts.real_tree_facts(il_path)
        cls.config = fws.parse_config(ALLOWLIST.read_text(encoding="utf-8"))

    def test_every_site_is_decided_and_every_count_matches(self):
        findings = fws.decide_sites(self.facts, self.config)
        self.assertEqual(findings, [], "\n" + "\n".join(findings)
                         + "\n(edit scripts/arch/far_watch_sites.toml in the commit that changed the method)")

    def test_every_vessel_event_handler_is_listed_and_guarded(self):
        findings, armed = fws.decide_handlers(self.facts, self.config)
        sys.stderr.write("\nfar-watch handler gate: %d handlers, guard call check %s\n" % (
            len(fws.vessel_event_handlers(self.facts)),
            "armed (%s)" % ", ".join(fws.guards_built(self.facts, self.config["guards"])) if armed
            else "not armed (no guard member built yet)"))
        self.assertEqual(findings, [], "\n" + "\n".join(findings))

    def test_the_gate_reds_when_an_entry_count_is_lowered_by_one(self):
        site = sorted(self.config["sites"])[0]
        config = copy.deepcopy(self.config)
        config["sites"][site]["count"] -= 1
        findings = fws.decide_sites(self.facts, config)
        self.assertEqual(kinds(findings), ["count"], findings)

    def test_the_counted_members_exist(self):
        externals = {m for e in self.facts["methods"].values() for m in e.get("externalMemberCounts", {})}
        self.assertIn(fws.GET_ACTIVE_VESSEL, externals)
        self.assertTrue(any(k.startswith(fws.IS_GHOST_MAP_VESSEL + "(") for k in self.facts["methods"]))
        self.assertIn(fws.GHOST_MAP_VESSEL_PIDS, self.facts["fields"])


if __name__ == "__main__":
    unittest.main()
