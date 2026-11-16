#!/usr/bin/env python3
"""Guard for #1944: the shipped unit's SystemCallFilter must allow clock_settime.

CAP_SYS_TIME alone is not enough: @system-service leaves out clock_settime, so
every time push ended clockSet=False. Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v
"""

from __future__ import annotations

import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
UNIT = REPO_ROOT / "packaging" / "debian" / "etc" / "systemd" / "system" / "openastroara-server.service"
PLAYBOOK = REPO_ROOT / "design" / "PORT_PLAYBOOK.md"


def syscall_filters(text: str) -> list[str]:
    """Every SystemCallFilter= token, in order."""
    out: list[str] = []
    for raw in text.splitlines():
        line = raw.strip()
        if line.startswith(("#", ";")) or "=" not in line:
            continue
        key, _, value = line.partition("=")
        if key.strip() == "SystemCallFilter":
            out.extend(value.split())
    return out


def allows_clock_settime(tokens: list[str]) -> bool:
    allowed = any(t in ("@clock", "clock_settime") for t in tokens if not t.startswith("~"))
    denied = any(t in ("~@clock", "~clock_settime") for t in tokens)
    return allowed and not denied


class ClockSyscallTests(unittest.TestCase):
    def test_shipped_unit_filter_allows_clock_settime(self) -> None:
        tokens = syscall_filters(UNIT.read_text(encoding="utf-8"))
        self.assertTrue(allows_clock_settime(tokens), f"SystemCallFilter {tokens} blocks clock_settime")

    def test_playbook_unit_copy_allows_clock_settime(self) -> None:
        tokens = syscall_filters(PLAYBOOK.read_text(encoding="utf-8"))
        self.assertTrue(allows_clock_settime(tokens), f"playbook SystemCallFilter {tokens} blocks clock_settime")


if __name__ == "__main__":
    unittest.main()
