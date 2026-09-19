#!/usr/bin/env python3
"""Track the Flutter stable channel against the pin the client builds with.

The pin lives in two files that must agree:

  * client/openastroara_client/.flutter-version — a bare version, read by CI
    (.github/workflows/ci.yml) and fed to subosito/flutter-action.
  * client/openastroara_client/pubspec.yaml — environment.flutter, a
    '>=X.Y.0 <X.(Y+1).0' range, plus environment.sdk, the Dart constraint that
    ships with that Flutter release.

Dependabot has no Flutter-SDK ecosystem, so .github/workflows/check-flutter.yml
runs this weekly (playbook §12.1) and opens a bump PR when stable moves.

Modes:
    --check   compare the pin against the current stable release; print a
              summary and, under GitHub Actions, emit step outputs. Exit 0
              whether or not an update exists — "no update" is not a failure.
    --apply   rewrite both files to the current stable release.

Major-version bumps (3.x → 4.x) are never applied automatically: they carry
breaking API changes that want a human. --check reports one and stops.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

RELEASES_URL = (
    "https://storage.googleapis.com/flutter_infra_release/releases/releases_linux.json"
)

REPO_ROOT = Path(__file__).resolve().parent.parent
CLIENT = REPO_ROOT / "client" / "openastroara_client"
VERSION_FILE = CLIENT / ".flutter-version"
PUBSPEC = CLIENT / "pubspec.yaml"


def fail(msg: str) -> None:
    print(f"error: {msg}", file=sys.stderr)
    raise SystemExit(1)


def parse_version(text: str) -> tuple[int, int, int]:
    """'3.47.5' -> (3, 47, 5). Rejects anything that is not a bare X.Y.Z."""
    m = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", text.strip())
    if not m:
        fail(f"not a bare X.Y.Z version: {text!r}")
    return tuple(int(g) for g in m.groups())  # type: ignore[return-value]


def read_pin() -> str:
    if not VERSION_FILE.exists():
        fail(f"missing {VERSION_FILE.relative_to(REPO_ROOT)}")
    pin = VERSION_FILE.read_text().strip()
    if not pin:
        fail(f"{VERSION_FILE.relative_to(REPO_ROOT)} is empty")
    parse_version(pin)
    return pin


def fetch_current_stable() -> tuple[str, str]:
    """Return (flutter_version, dart_sdk_version) for the current stable release."""
    try:
        with urllib.request.urlopen(RELEASES_URL, timeout=60) as resp:
            data = json.load(resp)
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        fail(f"could not read the Flutter release feed: {exc}")

    current_hash = data.get("current_release", {}).get("stable")
    if not current_hash:
        fail("release feed has no current_release.stable")

    for release in data.get("releases", []):
        if release.get("hash") == current_hash and release.get("channel") == "stable":
            version = release.get("version", "")
            dart = release.get("dart_sdk_version", "")
            if not version or not dart:
                fail(f"release {current_hash} is missing version or dart_sdk_version")
            # Some entries carry build metadata, e.g. "3.13.4 (build 3.13.4-...)".
            dart = dart.split()[0]
            return version, dart

    fail(f"no stable release in the feed matches current_release.stable ({current_hash})")
    raise AssertionError("unreachable")


def flutter_constraint(version: str) -> str:
    major, minor, _ = parse_version(version)
    return f"'>={major}.{minor}.0 <{major}.{minor + 1}.0'"


def dart_constraint(dart_version: str) -> str:
    major, minor, _ = parse_version(dart_version)
    return f"^{major}.{minor}.0"


def _environment_block(text: str) -> tuple[int, int]:
    """Return the (start, end) offsets of pubspec.yaml's top-level environment: block.

    Scoped deliberately: 'sdk:' also appears under dependencies (as the Flutter
    SDK source) and 'flutter:' is a top-level key of its own, so a file-wide
    substitution could rewrite the wrong line on a pubspec we have not seen.
    """
    m = re.search(r"(?m)^environment:[ \t]*$", text)
    if not m:
        fail("pubspec.yaml has no top-level 'environment:' block")
    start = m.end()
    # The block ends at the next line that starts in column 0 (a new top-level key).
    nxt = re.search(r"(?m)^\S", text[start:])
    end = start + nxt.start() if nxt else len(text)
    return start, end


def _set_in_environment(text: str, key: str, value: str) -> tuple[str, bool]:
    start, end = _environment_block(text)
    block = text[start:end]
    new_block, n = re.subn(
        rf"(?m)^([ \t]+{re.escape(key)}:[ \t]*).*$",
        lambda m: m.group(1) + value,
        block,
        count=1,
    )
    if n != 1:
        fail(f"could not find a single '{key}:' line in pubspec.yaml's environment block")
    return text[:start] + new_block + text[end:], new_block != block


def apply(version: str, dart_version: str) -> list[str]:
    """Rewrite both pin files. Returns a list of human-readable changes."""
    changed: list[str] = []
    old_pin = read_pin()

    # Work out the whole pubspec rewrite before writing anything. A malformed
    # pubspec fails here, leaving the tree untouched — otherwise someone running
    # --apply by hand ends up with a bumped .flutter-version and stale
    # constraints, which is worse than not having run it.
    text = PUBSPEC.read_text()

    want_flutter = flutter_constraint(version)
    text, flutter_changed = _set_in_environment(text, "flutter", want_flutter)

    want_sdk = dart_constraint(dart_version)
    text, sdk_changed = _set_in_environment(text, "sdk", want_sdk)

    if old_pin != version:
        VERSION_FILE.write_text(f"{version}\n")
        changed.append(f".flutter-version: {old_pin} -> {version}")
    if flutter_changed:
        changed.append(f"pubspec.yaml environment.flutter -> {want_flutter}")
    if sdk_changed:
        changed.append(f"pubspec.yaml environment.sdk -> {want_sdk}")

    PUBSPEC.write_text(text)
    return changed


def emit_output(**values: str) -> None:
    """Write step outputs when running under GitHub Actions."""
    # Plain key=value is safe here only because every value is a literal or has
    # been through parse_version(): no newlines, so none can forge a line. An
    # output carrying free text (a release note, a feed error) would need the
    # heredoc form instead.
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as fh:
        for key, value in values.items():
            fh.write(f"{key}={value}\n")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="report without writing")
    mode.add_argument("--apply", action="store_true", help="rewrite the pin files")
    ap.add_argument(
        "--version",
        help="Flutter version to apply, instead of re-reading the feed. Pass the "
        "value --check resolved so a release landing between the two runs cannot "
        "write one version while the rest of the job cites another.",
    )
    ap.add_argument("--dart", help="Dart SDK version that ships with --version")
    args = ap.parse_args()

    if (args.version is None) != (args.dart is None):
        fail("--version and --dart must be given together")
    if args.version and args.check:
        fail("--version/--dart only apply to --apply")

    pin = read_pin()
    if args.version:
        latest, dart = args.version, args.dart
        parse_version(latest)
        parse_version(dart)
    else:
        latest, dart = fetch_current_stable()

    pin_v = parse_version(pin)
    latest_v = parse_version(latest)

    if args.check:
        if latest_v[0] != pin_v[0]:
            # Majors are a human decision; report and stop rather than propose.
            print(
                f"Flutter {latest} is a major bump from the pinned {pin} — "
                "not automated (breaking API changes); bump by hand."
            )
            emit_output(update="false", major="true", latest=latest, pinned=pin)
            return 0
        if latest_v <= pin_v:
            print(f"Flutter pin {pin} is current (stable is {latest}).")
            emit_output(update="false", major="false", latest=latest, pinned=pin)
            return 0
        print(f"Flutter {latest} (Dart {dart}) is newer than the pinned {pin}.")
        emit_output(
            update="true",
            major="false",
            latest=latest,
            pinned=pin,
            dart=dart,
            branch=f"ci/flutter-{latest}",
        )
        return 0

    if latest_v[0] != pin_v[0]:
        fail(f"refusing to apply a major bump ({pin} -> {latest}); bump by hand")
    if latest_v <= pin_v:
        print(f"nothing to do: pin {pin} is current (stable is {latest}).")
        return 0

    changes = apply(latest, dart)
    if not changes:
        print("nothing to do: files already match the current stable release.")
        return 0
    for line in changes:
        print(f"  {line}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
