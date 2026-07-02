#!/usr/bin/env python3
"""Generate 3rd-party-licenses.txt from the shipped dependency graphs.

Covers BOTH distributed halves of the repo, like the hand-curated file it
replaces:

  * SERVER — the full transitive NuGet graph of OpenAstroAra.Server (the
    daemon the .deb ships), enumerated via `dotnet package list
    --include-transitive --format json`, with license metadata read from each
    package's .nuspec in the local NuGet cache. Legacy pre-SPDX packages are
    covered by the curated NUGET_OVERRIDES table (version-pinned so a bump
    forces re-verification).
  * CLIENT — the WILMA Flutter app's direct runtime dependencies ("direct
    main" entries in pubspec.lock, resolved versions), with licenses from the
    curated PUB_LICENSES table (pub.dev ships no license metadata in the
    lock). The table is self-enforcing: an unmapped direct dependency or a
    dead table entry fails the run.

Static stanzas in the header disclose the bundled platforms that no package
graph reports: the self-contained .NET runtime + ASP.NET Core (the bulk of
/opt/openastroara) and the Flutter engine/framework embedded in the client
binaries.

Modes:
    generate (default)  — (re)write 3rd-party-licenses.txt
    --check             — regenerate in memory and fail (exit 1) if the
                          committed file is stale; CI runs this after
                          `dotnet restore` in the server-build job, and
                          packaging/build-deb.sh runs it before staging.

Requirements: the Server project must be restored (the command reads the
restore assets and nuspecs come from the populated NuGet cache), and the
client's pubspec.lock must be checked in (it is).

Provenance for the audit itself: design/PORT_TODO.md "§15 / §17.2
dependency-license audit (2026-06-10)".
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path
from urllib.parse import quote

REPO_ROOT = Path(__file__).resolve().parent.parent
SERVER_PROJECT = "OpenAstroAra.Server/OpenAstroAra.Server.csproj"
CLIENT_LOCK = REPO_ROOT / "client" / "openastroara_client" / "pubspec.lock"
OUTPUT = REPO_ROOT / "3rd-party-licenses.txt"

# --- SERVER: curated overrides for NuGet packages whose published .nuspec
# carries no SPDX <license> expression (licenseUrl-only, or in one case
# nothing). Each entry is pinned to the version it was verified against
# (upstream project checked 2026-07-01; `source` records where) — a version
# bump of a curated package fails the run until a human re-verifies the
# license and updates the pin. The repo-wide audit (design/PORT_TODO.md
# §15/§17.2, 2026-06-10) found the same set: everything permissive except
# Accord.NET (LGPL-2.1, dynamically linked).
_ACCORD = {
    "license": "LGPL-2.1",
    "source": "http://accord-framework.net/license.txt (Accord.NET framework license)",
    "version": "3.8.2-alpha",
}
_COMMON_LOGGING = {
    "license": "Apache-2.0",
    "source": "https://github.com/net-commons/common-logging (README: Apache Software License)",
    "version": "3.4.1",
}
_MS_DOTNET_EULA = {
    "license": "Microsoft .NET Library License",
    "source": "http://go.microsoft.com/fwlink/?LinkId=329770 (redistributable EULA)",
}
NUGET_OVERRIDES: dict[str, dict[str, str]] = {
    "Accord": _ACCORD,
    "Accord.Math": _ACCORD,
    "Accord.Statistics": _ACCORD,
    "Common.Logging": _COMMON_LOGGING,
    "Common.Logging.Core": _COMMON_LOGGING,
    "IPNetwork2": {
        "license": "BSD-2-Clause",
        "source": "https://github.com/lduchosal/ipnetwork/blob/master/LICENSE",
        "version": "2.1.2",
    },
    "Iconic.Zlib.Netstandard": {
        "license": "MIT AND Ms-PL",
        "source": "https://github.com/HelloKitty/Iconic.Zlib.Netstandard "
        "(port changes MIT; original DotNetZip/Ionic.Zlib code Ms-PL)",
        "version": "1.0.0",
    },
    "K4os.Compression.LZ4": {
        "license": "MIT",
        "source": "https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE",
        "version": "1.3.8",
    },
    "Makaretu.Dns": {
        "license": "MIT",
        "source": "https://github.com/richardschneider/net-dns (README: MIT)",
        "version": "2.0.1",
    },
    "Makaretu.Dns.Multicast": {
        "license": "MIT",
        "source": "https://github.com/richardschneider/net-mdns (README: MIT)",
        "version": "0.27.0",
    },
    "Microsoft.NETCore.Platforms": {**_MS_DOTNET_EULA, "version": "1.1.0"},
    "NETStandard.Library": {**_MS_DOTNET_EULA, "version": "1.6.1"},
    "SQLitePCLRaw.lib.e_sqlite3": {
        "license": "Apache-2.0",
        "source": "https://github.com/ericsink/SQLitePCL.raw (SQLitePCLRaw is Apache-2.0; "
        "the bundled SQLite engine is public domain). This version is pinned over the "
        "bundle's transitive pick for CVE-2025-6965 — see design/PORT_TODO.md.",
        "version": "3.50.3",
    },
    "SimpleBase": {
        "license": "Apache-2.0",
        "source": "https://github.com/ssg/SimpleBase (README: Apache-2.0)",
        "version": "1.3.1",
    },
    "runtime.native.System.Data.SqlClient.sni": {
        "license": "MIT",
        "source": "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT (corefx, MIT)",
        "version": "4.4.0",
    },
    "runtime.win-arm64.runtime.native.System.Data.SqlClient.sni": {**_MS_DOTNET_EULA, "version": "4.4.0"},
    "runtime.win-x64.runtime.native.System.Data.SqlClient.sni": {**_MS_DOTNET_EULA, "version": "4.4.0"},
    "runtime.win-x86.runtime.native.System.Data.SqlClient.sni": {**_MS_DOTNET_EULA, "version": "4.4.0"},
}
# Case-insensitive lookup (NuGet ids are case-insensitive; `dotnet package
# list` reports whatever casing the declaring project used).
_NUGET_OVERRIDES_CI = {k.lower(): (k, v) for k, v in NUGET_OVERRIDES.items()}

# --- CLIENT: curated licenses for the WILMA app's direct runtime pub
# packages (pub.dev metadata carries no license field, so this is
# hand-verified against each resolved package's LICENSE file in the pub
# cache — every entry checked at its pinned version on 2026-07-01). Keyed by
# pub package name and version-pinned like NUGET_OVERRIDES: a pubspec.lock
# bump of a curated package fails the run until a human re-verifies the new
# version's LICENSE and updates the pin. The set is also self-enforcing both
# ways (unmapped direct dep / dead entry → hard fail).
PUB_LICENSES: dict[str, dict[str, str]] = {
    "collection": {
        "version": "1.19.1",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2015, the Dart project authors",
    },
    "cupertino_icons": {
        "version": "1.0.9",
        "license": "MIT",
        "copyright": "Copyright (c) 2016 Vladimir Kharlampidi",
    },
    "dio": {
        "version": "5.10.0",
        "license": "MIT",
        "copyright": "Copyright (c) 2018 Wen Du; Copyright (c) 2022 The CFUG Team",
    },
    "file_picker": {
        "version": "12.0.0-beta.7",
        "license": "MIT",
        "copyright": "Copyright (c) 2018 Miguel Ruivo",
    },
    "fl_chart": {
        "version": "1.2.0",
        "license": "MIT",
        "copyright": "Copyright (c) 2022 Flutter 4 Fun",
    },
    "flutter_riverpod": {
        "version": "3.3.2",
        "license": "MIT",
        "copyright": "Copyright (c) 2020 Remi Rousselet",
    },
    "flutter_secure_storage": {
        "version": "10.3.1",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2017 German Saprykin",
    },
    "multicast_dns": {
        "version": "0.3.3",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2013 The Flutter Authors",
    },
    "package_info_plus": {
        "version": "10.2.0",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2017 The Chromium Authors",
    },
    "path_provider": {
        "version": "2.1.6",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2013 The Flutter Authors",
    },
    "riverpod": {
        "version": "3.3.2",
        "license": "MIT",
        "copyright": "Copyright (c) 2020 Remi Rousselet",
    },
    "url_launcher": {
        "version": "6.3.2",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2013 The Flutter Authors",
    },
    "web_socket_channel": {
        "version": "3.0.3",
        "license": "BSD-3-Clause",
        "copyright": "Copyright 2016, the Dart project authors",
    },
    "webview_all": {
        "version": "1.2.0",
        "license": "MIT",
        "copyright": "Copyright 2021-2026 Abandoft",
    },
}

HEADER = """\
Third-party notices — OpenAstro Ara (server daemon + WILMA client)
==================================================================

OpenAstro Ara (ARA) is a derivative work of N.I.N.A. — Nighttime Imaging 'N'
Astronomy (see NOTICE.md). The daemon and repository default license is the
Mozilla Public License 2.0 (LICENSE.txt); the WILMA client is AGPL-3.0-or-later
(client/openastroara_client/LICENSE). This file lists the third-party
components distributed with ARA binaries, in two generated sections: the
daemon's full transitive NuGet dependency graph, and the client's direct
runtime pub packages.

This file is GENERATED — do not edit by hand. Regenerate with:
    python3 scripts/generate-3rd-party-licenses.py
CI fails if it is stale relative to either dependency graph, and
packaging/build-deb.sh re-checks it before staging a .deb.

For licenses identified by an SPDX expression, the full license text is
available at https://licenses.nuget.org/<expression> (e.g.
https://licenses.nuget.org/MIT) and https://spdx.org/licenses/. Where a
package predates SPDX metadata, the "License source" line records where its
terms were verified.

The server list is the full RESOLVED graph, which deliberately over-discloses:
RID-specific native packages for other platforms (e.g. the runtime.win-*
SqlClient shims) appear even though the linux-arm64 self-contained publish
does not ship their binaries.

BUNDLED PLATFORMS (not reported by either package graph):

  * .NET runtime + ASP.NET Core — the .deb's self-contained publish bundles
    the entire .NET 10 runtime and ASP.NET Core framework (the bulk of
    /opt/openastroara). MIT — Copyright (c) .NET Foundation and Contributors.
    https://github.com/dotnet/runtime + https://github.com/dotnet/aspnetcore
  * Flutter engine + framework — embedded in every WILMA client binary.
    BSD-3-Clause — Copyright 2014 The Flutter Authors.
    https://github.com/flutter/flutter
  * CFITSIO — P/Invoked by OpenAstroAra.Fits but NOT bundled: the .deb
    depends on the distro's libcfitsio10 package, which supplies the shared
    library. ISC-style HEASARC license — see NOTICE.md.

Engines and companions installed as SEPARATE packages (openastro-guider,
alpacabridge, ASTAP) carry their own licenses and are not part of this
distribution. Client-embedded engines with their own disclosure files keep
them: the Stellarium Web Engine (AGPL-3.0,
client/.../assets/stellarium/README.md + the open-astro/stellarium-web-engine
fork) and Aladin Lite (GPL-3.0, client/.../assets/aladin/ALADIN_LICENSE.md).
"""

SEPARATOR = "\n\n" + "-" * 72 + "\n"


def fail(message: str) -> None:
    sys.exit(f"error: {message}")


def nuget_cache() -> Path:
    """Resolve the NuGet global-packages folder the way NuGet itself does."""
    # Authoritative: ask the CLI (covers a NuGet.Config globalPackagesFolder,
    # which the env-var/home fallback would miss).
    result = subprocess.run(
        ["dotnet", "nuget", "locals", "global-packages", "--list"],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode == 0:
        # Output shape: "global-packages: /home/user/.nuget/packages/"
        for line in result.stdout.splitlines():
            _, sep, path = line.partition(":")
            if sep and path.strip():
                candidate = Path(path.strip())
                if candidate.is_dir():
                    return candidate
    env = os.environ.get("NUGET_PACKAGES")
    return Path(env) if env else Path.home() / ".nuget" / "packages"


def server_package_graph() -> dict[str, str]:
    """id -> resolved version for the Server project's full transitive graph."""
    result = subprocess.run(
        [
            "dotnet",
            "package",
            "list",
            "--project",
            str(REPO_ROOT / SERVER_PROJECT),
            "--include-transitive",
            "--format",
            "json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        fail(f"`dotnet package list` failed (is the project restored?):\n{result.stderr}")
    # Tolerate informational lines before the JSON document (SDK notices
    # etc.) — parse from the first brace.
    brace = result.stdout.find("{")
    if brace < 0:
        fail(f"`dotnet package list` produced no JSON:\n{result.stdout[:500]}")
    data = json.loads(result.stdout[brace:])
    pkgs: dict[str, str] = {}
    for project in data.get("projects", []):
        for fw in project.get("frameworks") or []:
            for group in ("topLevelPackages", "transitivePackages"):
                for pkg in fw.get(group, []):
                    pkg_id, version = pkg["id"], pkg["resolvedVersion"]
                    existing = pkgs.get(pkg_id)
                    if existing is not None and existing != version:
                        fail(
                            f"{pkg_id} resolves to different versions across target "
                            f"frameworks ({existing} vs {version}) — the notices file "
                            "can only record one; teach the generator per-framework "
                            "sections before multi-targeting"
                        )
                    pkgs[pkg_id] = version
    if not pkgs:
        fail("empty package graph — run `dotnet restore OpenAstroAra.Server` first")
    return pkgs


def client_direct_packages() -> dict[str, str]:
    """name -> resolved version for the client's hosted "direct main" deps."""
    if not CLIENT_LOCK.is_file():
        fail(f"client lockfile not found: {CLIENT_LOCK}")
    lock = CLIENT_LOCK.read_text(encoding="utf-8")
    pkgs: dict[str, str] = {}
    for name, block in re.findall(r"^  (\S+):\n((?:    .*\n)+)", lock, re.MULTILINE):
        if '"direct main"' not in block:
            continue
        if re.search(r"^    source: sdk\b", block, re.MULTILINE):
            continue  # the Flutter SDK itself — disclosed as a static stanza
        version = re.search(r'^    version: "([^"]+)"', block, re.MULTILINE)
        if not version:
            fail(f"pubspec.lock entry for {name} has no version — lockfile format drift?")
        pkgs[name] = version.group(1)
    if not pkgs:
        fail("no direct-main packages parsed from pubspec.lock — lockfile format drift?")
    return pkgs


def read_nuspec(cache: Path, pkg_id: str, version: str) -> dict[str, str | None]:
    path = cache / pkg_id.lower() / version.lower() / f"{pkg_id.lower()}.nuspec"
    if not path.exists():
        fail(f"nuspec not found for {pkg_id} {version} at {path} — run `dotnet restore` first")
    root = ET.parse(path).getroot()
    ns = root.tag.split("}")[0] + "}" if root.tag.startswith("{") else ""
    meta = root.find(f"{ns}metadata")
    if meta is None:
        fail(f"malformed nuspec (no <metadata>) for {pkg_id} {version}")

    def text(tag: str) -> str | None:
        el = meta.find(f"{ns}{tag}")
        if el is None or not el.text:
            return None
        # Collapse internal whitespace/newlines so a multi-line nuspec field
        # can't break the fixed one-line-per-field output format.
        return " ".join(el.text.split()) or None

    license_el = meta.find(f"{ns}license")
    expression = None
    if license_el is not None and license_el.get("type") == "expression":
        expression = (license_el.text or "").strip() or None
    return {
        "authors": text("authors"),
        "copyright": text("copyright"),
        "expression": expression,
        "projectUrl": text("projectUrl"),
    }


def build_server_section(pkgs: dict[str, str]) -> tuple[str, Counter]:
    graph_ids = {p.lower() for p in pkgs}
    dead = sorted(k for k in NUGET_OVERRIDES if k.lower() not in graph_ids)
    if dead:
        fail(
            "dead NUGET_OVERRIDES entries for packages no longer in the graph — "
            f"remove them: {', '.join(dead)}"
        )
    cache = nuget_cache()
    entries: list[str] = []
    counts: Counter = Counter()
    for pkg_id in sorted(pkgs, key=str.lower):
        version = pkgs[pkg_id]
        meta = read_nuspec(cache, pkg_id, version)
        override = _NUGET_OVERRIDES_CI.get(pkg_id.lower())

        if meta["expression"] and override:
            fail(
                f"stale override for {pkg_id} — the nuspec now carries the SPDX "
                f"expression '{meta['expression']}'; remove it from NUGET_OVERRIDES"
            )
        if meta["expression"]:
            license_name = meta["expression"]
            # licenses.nuget.org resolves full SPDX expressions, including
            # compound ones ("MIT OR Apache-2.0") — but those need their
            # spaces/parens URL-encoded or the emitted link is broken.
            source = f"https://licenses.nuget.org/{quote(license_name)}"
        elif override:
            _, data = override
            if data["version"] != version:
                fail(
                    f"{pkg_id} bumped {data['version']} → {version} but its curated "
                    "NUGET_OVERRIDES entry was verified against the old version — "
                    "re-verify the new version's license upstream and update the pin"
                )
            license_name = data["license"]
            source = data["source"]
        else:
            fail(
                f"{pkg_id} {version} has no SPDX license expression and no override — "
                "verify its license upstream and add it to NUGET_OVERRIDES in "
                f"{Path(__file__).name}"
            )

        counts[license_name] += 1
        lines = [f"Package:        {pkg_id} {version}"]
        if meta["authors"]:
            lines.append(f"Authors:        {meta['authors']}")
        if meta["copyright"]:
            lines.append(f"Copyright:      {meta['copyright']}")
        lines.append(f"License:        {license_name}")
        lines.append(f"License source: {source}")
        if meta["projectUrl"]:
            lines.append(f"Project:        {meta['projectUrl']}")
        entries.append("\n".join(lines))

    title = (
        "SERVER — OpenAstroAra.Server NuGet dependency graph "
        f"({len(pkgs)} packages, transitive closure)"
    )
    body = title + "\n" + "=" * len(title) + SEPARATOR + SEPARATOR.join(entries)
    return body, counts


def build_client_section(pkgs: dict[str, str]) -> tuple[str, Counter]:
    unmapped = sorted(set(pkgs) - set(PUB_LICENSES))
    if unmapped:
        fail(
            "client direct dependencies without a curated PUB_LICENSES entry — "
            f"verify each package's LICENSE and add it: {', '.join(unmapped)}"
        )
    dead = sorted(set(PUB_LICENSES) - set(pkgs))
    if dead:
        fail(
            "dead PUB_LICENSES entries for packages no longer direct client "
            f"dependencies — remove them: {', '.join(dead)}"
        )
    entries: list[str] = []
    counts: Counter = Counter()
    for name in sorted(pkgs):
        data = PUB_LICENSES[name]
        if data["version"] != pkgs[name]:
            fail(
                f"{name} bumped {data['version']} → {pkgs[name]} in pubspec.lock but "
                "its curated PUB_LICENSES entry was verified against the old version "
                "— re-verify the new version's LICENSE and update the pin"
            )
        counts[data["license"]] += 1
        entries.append(
            "\n".join(
                [
                    f"Package:        {name} {pkgs[name]}",
                    f"Copyright:      {data['copyright']}",
                    f"License:        {data['license']}",
                    f"License source: https://pub.dev/packages/{name}/license",
                ]
            )
        )
    title = (
        "CLIENT — WILMA direct runtime pub packages "
        f"({len(pkgs)} packages; transitive pub packages ship via Flutter's "
        "in-app LicenseRegistry)"
    )
    body = title + "\n" + "=" * len(title) + SEPARATOR + SEPARATOR.join(entries)
    return body, counts


def build_document() -> str:
    server_body, server_counts = build_server_section(server_package_graph())
    client_body, client_counts = build_client_section(client_direct_packages())
    totals = server_counts + client_counts
    summary = "\n".join(
        f"    {name}: {count} package{'s' if count != 1 else ''}"
        for name, count in sorted(totals.items(), key=lambda kv: (-kv[1], kv[0]))
    )
    return (
        HEADER
        + f"\nLicense summary ({sum(totals.values())} packages):\n{summary}\n\n\n"
        + server_body
        + "\n\n\n"
        + client_body
        + "\n"
    )


def main(argv: list[str]) -> int:
    check = False
    if argv == ["--check"]:
        check = True
    elif argv:
        # Strict: an unrecognized flag must fail loudly, not silently fall
        # through to generate mode (which would neuter the CI gate).
        print(
            f"error: unrecognized arguments: {' '.join(argv)}\n"
            "usage: generate-3rd-party-licenses.py [--check]",
            file=sys.stderr,
        )
        return 2
    document = build_document()
    encoded = document.encode("utf-8")
    if check:
        current = OUTPUT.read_bytes() if OUTPUT.exists() else b""
        if current != encoded:
            print(
                "error: 3rd-party-licenses.txt is stale relative to the dependency "
                "graphs.\nRegenerate with: python3 scripts/generate-3rd-party-licenses.py",
                file=sys.stderr,
            )
            return 1
        print("3rd-party-licenses.txt is up to date.")
        return 0
    # Byte-exact output (LF newlines on every platform) so the --check
    # comparison and git diffs never churn on line endings.
    OUTPUT.write_bytes(encoded)
    print(f"wrote {OUTPUT} ({len(document.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
