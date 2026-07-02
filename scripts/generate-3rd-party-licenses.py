#!/usr/bin/env python3
"""Generate 3rd-party-licenses.txt from the daemon's NuGet dependency graph.

Enumerates the full transitive package graph of OpenAstroAra.Server (the
shipped daemon) via `dotnet list package --include-transitive --format json`,
reads each package's license metadata from its .nuspec in the local NuGet
cache, and writes a deterministic third-party-notices file at the repo root.
The .deb pipeline (packaging/build-deb.sh) ships the file at
/usr/share/doc/openastroara-server/3rd-party-licenses.txt.

Modes:
    generate (default)  — (re)write 3rd-party-licenses.txt
    --check             — regenerate in memory and fail (exit 1) if the
                          committed file is stale; CI runs this after
                          `dotnet restore` in the server-build job.

Requirements: the Server project must be restored (the command reads the
restore assets, and the nuspecs come from the populated NuGet cache).

Legacy packages that predate the nuspec <license> element (licenseUrl-only
or nothing at all) are covered by the curated OVERRIDES table below. The
script fails hard on any package with neither nuspec license metadata nor
an override — a new dependency with unknown licensing must be curated here
before it can ship — and on any override made redundant by a package that
now carries its own expression (stale override).

Provenance for the audit itself: design/PORT_TODO.md "§15 / §17.2
dependency-license audit (2026-06-10)".
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PROJECT = "OpenAstroAra.Server/OpenAstroAra.Server.csproj"
OUTPUT = REPO_ROOT / "3rd-party-licenses.txt"

# Packages whose published .nuspec carries no SPDX <license> expression
# (licenseUrl-only, or in one case nothing). Each entry was verified against
# the upstream project on 2026-07-01; `source` records where. The repo-wide
# audit (design/PORT_TODO.md §15/§17.2, 2026-06-10) found the same set:
# everything permissive except Accord.NET (LGPL-2.1, dynamically linked).
OVERRIDES: dict[str, dict[str, str]] = {
    "Accord": {
        "license": "LGPL-2.1",
        "source": "http://accord-framework.net/license.txt (Accord.NET framework license)",
    },
    "Accord.Math": {
        "license": "LGPL-2.1",
        "source": "http://accord-framework.net/license.txt (Accord.NET framework license)",
    },
    "Accord.Statistics": {
        "license": "LGPL-2.1",
        "source": "http://accord-framework.net/license.txt (Accord.NET framework license)",
    },
    "Common.Logging": {
        "license": "Apache-2.0",
        "source": "https://github.com/net-commons/common-logging (README: Apache Software License)",
    },
    "Common.Logging.Core": {
        "license": "Apache-2.0",
        "source": "https://github.com/net-commons/common-logging (README: Apache Software License)",
    },
    "IPNetwork2": {
        "license": "BSD-2-Clause",
        "source": "https://github.com/lduchosal/ipnetwork/blob/master/LICENSE",
    },
    "Iconic.Zlib.Netstandard": {
        "license": "MIT AND Ms-PL",
        "source": "https://github.com/HelloKitty/Iconic.Zlib.Netstandard "
        "(port changes MIT; original DotNetZip/Ionic.Zlib code Ms-PL)",
    },
    "K4os.Compression.LZ4": {
        "license": "MIT",
        "source": "https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE",
    },
    "Makaretu.Dns": {
        "license": "MIT",
        "source": "https://github.com/richardschneider/net-dns (README: MIT)",
    },
    "Makaretu.Dns.Multicast": {
        "license": "MIT",
        "source": "https://github.com/richardschneider/net-mdns (README: MIT)",
    },
    "Microsoft.NETCore.Platforms": {
        "license": "Microsoft .NET Library License",
        "source": "http://go.microsoft.com/fwlink/?LinkId=329770 (redistributable EULA)",
    },
    "NETStandard.Library": {
        "license": "Microsoft .NET Library License",
        "source": "http://go.microsoft.com/fwlink/?LinkId=329770 (redistributable EULA)",
    },
    "SQLitePCLRaw.lib.e_sqlite3": {
        "license": "Apache-2.0",
        "source": "https://github.com/ericsink/SQLitePCL.raw (SQLitePCLRaw is Apache-2.0; "
        "the bundled SQLite engine is public domain). This version is pinned over the "
        "bundle's transitive pick for CVE-2025-6965 — see design/PORT_TODO.md.",
    },
    "SimpleBase": {
        "license": "Apache-2.0",
        "source": "https://github.com/ssg/SimpleBase (README: Apache-2.0)",
    },
    "runtime.native.System.Data.SqlClient.sni": {
        "license": "MIT",
        "source": "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT (corefx, MIT)",
    },
    "runtime.win-arm64.runtime.native.System.Data.SqlClient.sni": {
        "license": "Microsoft .NET Library License",
        "source": "http://go.microsoft.com/fwlink/?LinkId=329770 (redistributable EULA)",
    },
    "runtime.win-x64.runtime.native.System.Data.SqlClient.sni": {
        "license": "Microsoft .NET Library License",
        "source": "http://go.microsoft.com/fwlink/?LinkId=329770 (redistributable EULA)",
    },
    "runtime.win-x86.runtime.native.System.Data.SqlClient.sni": {
        "license": "Microsoft .NET Library License",
        "source": "http://go.microsoft.com/fwlink/?LinkId=329770 (redistributable EULA)",
    },
}

HEADER = """\
Third-party notices — openastroara-server
=========================================

OpenAstro Ara (ARA) is licensed under the Mozilla Public License 2.0 (see
LICENSE.txt) and is a derivative work of N.I.N.A. — Nighttime Imaging 'N'
Astronomy (see NOTICE.md). This file lists the third-party NuGet packages
distributed with the openastroara-server daemon, i.e. the full transitive
dependency graph of OpenAstroAra.Server, with their license terms.

This file is GENERATED — do not edit by hand. Regenerate with:
    python3 scripts/generate-3rd-party-licenses.py
CI fails if it is stale relative to the package graph.

For licenses identified by an SPDX expression, the full license text is
available at https://licenses.nuget.org/<expression> (e.g.
https://licenses.nuget.org/MIT) and https://spdx.org/licenses/. Where a
package predates SPDX metadata, the "License source" line records where its
terms were verified.

The list is the full RESOLVED graph, which deliberately over-discloses:
RID-specific native packages for other platforms (e.g. the runtime.win-*
SqlClient shims) appear here even though the linux-arm64 self-contained
publish does not ship their binaries.

Engines and companions installed as SEPARATE packages (openastro-guider,
alpacabridge, ASTAP) carry their own licenses and are not part of this
daemon's distribution; the WILMA desktop client's third-party notices are
likewise tracked separately.
"""


def nuget_cache() -> Path:
    env = os.environ.get("NUGET_PACKAGES")
    return Path(env) if env else Path.home() / ".nuget" / "packages"


def package_graph() -> dict[str, str]:
    """id -> resolved version for the Server project's full transitive graph."""
    result = subprocess.run(
        [
            "dotnet",
            "list",
            str(REPO_ROOT / PROJECT),
            "package",
            "--include-transitive",
            "--format",
            "json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        sys.exit(
            f"error: `dotnet list package` failed (is the project restored?):\n{result.stderr}"
        )
    data = json.loads(result.stdout)
    pkgs: dict[str, str] = {}
    for project in data.get("projects", []):
        frameworks = project.get("frameworks")
        if frameworks is None:
            sys.exit(
                "error: no frameworks in `dotnet list package` output — "
                "run `dotnet restore` for OpenAstroAra.Server first"
            )
        for fw in frameworks:
            for group in ("topLevelPackages", "transitivePackages"):
                for pkg in fw.get(group, []):
                    pkgs[pkg["id"]] = pkg["resolvedVersion"]
    if not pkgs:
        sys.exit("error: empty package graph — run `dotnet restore` first")
    return pkgs


def read_nuspec(pkg_id: str, version: str) -> dict[str, str | None]:
    path = (
        nuget_cache()
        / pkg_id.lower()
        / version.lower()
        / f"{pkg_id.lower()}.nuspec"
    )
    if not path.exists():
        sys.exit(
            f"error: nuspec not found for {pkg_id} {version} at {path} — "
            "run `dotnet restore` first"
        )
    root = ET.parse(path).getroot()
    ns = root.tag.split("}")[0] + "}" if root.tag.startswith("{") else ""
    meta = root.find(f"{ns}metadata")
    if meta is None:
        sys.exit(f"error: malformed nuspec (no <metadata>) for {pkg_id} {version}")

    def text(tag: str) -> str | None:
        el = meta.find(f"{ns}{tag}")
        return el.text.strip() if el is not None and el.text else None

    license_el = meta.find(f"{ns}license")
    expression = None
    if license_el is not None and license_el.get("type") == "expression":
        expression = (license_el.text or "").strip() or None
    return {
        "authors": text("authors"),
        "copyright": text("copyright"),
        "expression": expression,
        "licenseUrl": text("licenseUrl"),
        "projectUrl": text("projectUrl"),
    }


def build_document(pkgs: dict[str, str]) -> str:
    unused = sorted(set(OVERRIDES) - set(pkgs))
    if unused:
        sys.exit(
            "error: dead OVERRIDES entries for packages no longer in the graph — "
            f"remove them: {', '.join(unused)}"
        )
    entries: list[str] = []
    counts: dict[str, int] = {}
    for pkg_id in sorted(pkgs, key=str.lower):
        version = pkgs[pkg_id]
        meta = read_nuspec(pkg_id, version)
        override = OVERRIDES.get(pkg_id)

        if meta["expression"] and override:
            sys.exit(
                f"error: stale override for {pkg_id} — the nuspec now carries the "
                f"SPDX expression '{meta['expression']}'; remove it from OVERRIDES"
            )
        if meta["expression"]:
            license_name = meta["expression"]
            source = f"https://licenses.nuget.org/{license_name}"
        elif override:
            license_name = override["license"]
            source = override["source"]
        else:
            sys.exit(
                f"error: {pkg_id} {version} has no SPDX license expression and no "
                "override — verify its license upstream and add it to OVERRIDES in "
                f"{Path(__file__).name}"
            )

        counts[license_name] = counts.get(license_name, 0) + 1
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

    summary = "\n".join(
        f"    {name}: {count} package{'s' if count != 1 else ''}"
        for name, count in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    )
    separator = "\n\n" + "-" * 72 + "\n"
    return (
        HEADER
        + f"\nLicense summary ({len(pkgs)} packages):\n{summary}\n"
        + separator
        + separator.join(entries)
        + "\n"
    )


def main() -> int:
    check = "--check" in sys.argv[1:]
    document = build_document(package_graph())
    if check:
        current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else ""
        if current != document:
            print(
                "error: 3rd-party-licenses.txt is stale relative to the package "
                "graph.\nRegenerate with: python3 scripts/generate-3rd-party-licenses.py",
                file=sys.stderr,
            )
            return 1
        print("3rd-party-licenses.txt is up to date.")
        return 0
    OUTPUT.write_text(document, encoding="utf-8")
    print(f"wrote {OUTPUT} ({len(document.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
