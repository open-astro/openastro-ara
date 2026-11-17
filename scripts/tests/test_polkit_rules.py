#!/usr/bin/env python3
"""ADR 0017 rule 3 for every shipped polkit rule.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each packaging/debian/usr/share/polkit-1/rules.d/*.rules file is evaluated by node
against a stub `polkit` object (node is on the CI runner and the dev hosts) over a
matrix of users, actions, units and verbs. What the rule answers YES to must be:
only the openastroara user, only org.freedesktop.systemd1.manage-units, exactly one
unit (or one unit template), and never the stop, mask or edit verbs. Every other
case must be NOT_HANDLED.
"""

from __future__ import annotations

import itertools
import json
import re
import subprocess
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
RULES = sorted((REPO_ROOT / "packaging/debian/usr/share/polkit-1/rules.d").glob("*.rules"))

USERS = ["openastroara", "root", "nobody", "openastroara2"]
ACTIONS = [
    "org.freedesktop.systemd1.manage-units",
    "org.freedesktop.systemd1.manage-unit-files",
    "org.freedesktop.login1.power-off",
]
VERBS = ["start", "restart", "reload", "try-restart", "stop", "kill", "mask", "unmask", "edit", "enable", "disable"]
FORBIDDEN_VERBS = {"stop", "mask", "edit"}
# Units no rule may ever allow, whatever else the matrix holds.
STRANGERS = ["ssh.service", "openastroara-server.service", "poweroff.target", "openastroara-poweroff.timer"]

DRIVER = r"""
const src = process.argv[1];
const matrix = JSON.parse(require("fs").readFileSync(0, "utf8"));
const rules = [];
const polkit = {Result: {YES: "YES", NOT_HANDLED: "NOT_HANDLED", NO: "NO", AUTH_ADMIN: "AUTH_ADMIN"},
                addRule: (f) => rules.push(f)};
new Function("polkit", require("fs").readFileSync(src, "utf8"))(polkit);
if (rules.length !== 1) { throw new Error("expected exactly one rule, got " + rules.length); }
const out = matrix.map(([user, id, unit, verb]) => {
  const action = {id, lookup: (k) => ({unit, verb})[k]};
  return rules[0](action, {user});
});
console.log(JSON.stringify(out));
"""


def candidate_units() -> list[str]:
    """Every unit named by any rule file (templates get an instance), plus strangers."""
    units = set(STRANGERS)
    for rule in RULES:
        for lit in re.findall(r'"(openastro[\w@.-]*)', rule.read_text()):
            if lit.endswith(".service"):
                units.add(lit)
            elif lit.endswith("@"):
                units.add(lit + "abc.service")
                units.add(lit + "abc.timer")
                units.add(lit + "abc")
    return sorted(units)


def evaluate(rule: Path, matrix: list[tuple[str, str, str, str]]) -> list[str]:
    done = subprocess.run(["node", "-e", DRIVER, str(rule)], input=json.dumps(matrix),
                          capture_output=True, text=True, check=False)
    assert done.returncode == 0, f"{rule.name}: node failed: {done.stderr}"
    return json.loads(done.stdout)


def normalise(unit: str) -> str:
    """openastroara-update@abc.service -> openastroara-update@.service (one template)."""
    return re.sub(r"@[^.]*", "@", unit)


class PolkitRulesTest(unittest.TestCase):
    def test_there_are_rules_to_check(self) -> None:
        self.assertGreaterEqual(len(RULES), 4, [r.name for r in RULES])
        self.assertIn("50-openastroara-poweroff.rules", [r.name for r in RULES])

    def test_every_rule_allows_only_the_four_adr_0017_tests(self) -> None:
        units = candidate_units()
        matrix = list(itertools.product(USERS, ACTIONS, units, VERBS))
        for rule in RULES:
            with self.subTest(rule=rule.name):
                answers = evaluate(rule, matrix)
                self.assertTrue(set(answers) <= {"YES", "NOT_HANDLED"}, set(answers))
                allowed = [m for m, a in zip(matrix, answers) if a == "YES"]
                self.assertTrue(allowed, "the rule allows nothing")
                self.assertEqual({m[0] for m in allowed}, {"openastroara"}, "user")
                self.assertEqual({m[1] for m in allowed}, {ACTIONS[0]}, "action")
                self.assertEqual(len({normalise(m[2]) for m in allowed}), 1,
                                 "one named unit or one template")
                verbs = {m[3] for m in allowed}
                self.assertTrue(verbs <= {"start", "restart"}, verbs)
                self.assertFalse(verbs & FORBIDDEN_VERBS, verbs)

    def test_the_poweroff_rule_allows_start_of_its_one_unit_and_nothing_else(self) -> None:
        rule = next(r for r in RULES if r.name == "50-openastroara-poweroff.rules")
        matrix = list(itertools.product(USERS, ACTIONS, candidate_units(), VERBS))
        allowed = [m for m, a in zip(matrix, evaluate(rule, matrix)) if a == "YES"]
        self.assertEqual(allowed, [("openastroara", ACTIONS[0], "openastroara-poweroff.service", "start")])


if __name__ == "__main__":
    unittest.main()
