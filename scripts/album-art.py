#!/usr/bin/env python3
"""Build the Sky Album card Art from its manifest (docs/specs/sky-album-v1.md,
"Art and data"; #1594).

The manifest, client/openastroara_client/assets/album/manifest.json, holds one
row per Messier and Caldwell object: its Wikimedia Commons source page, credit,
licence, an optional crop box, the curated distance and the "Did you know?"
line. This script downloads each source, zooms it to fill the card's 5:6
picture (wide objects turned upright first, so less is trimmed), and writes
one 560x672 WebP per card into assets/album/art/. The outputs are
committed, so app builds need no network.

    python3 scripts/album-art.py build              # every row with a source
    python3 scripts/album-art.py build M31 C14      # just these
    python3 scripts/album-art.py sheet out.png      # contact sheet for review
    python3 scripts/album-art.py check              # what CI could run

Row fields the script reads:
  id        "M31", "C14"
  source    the Commons file page (https://commons.wikimedia.org/wiki/File:...)
  licence   CC BY (any version), CC0 or Public domain; anything else is refused
  crop      optional [x, y, w, h], fractions of the source, applied first
  rotate    optional true/false; default turns a landscape source upright
  fit       optional "contain": the whole picture on black, edges faded, for an object the fill would cut
  angle     optional degrees to turn anticlockwise before the crop; the crop box may then
            reach past the source edges, which are filled with mirrored sky (C34)
  fetch_px  optional long side to download, for a tight crop of a huge original (M104)
  level     optional white point in percent (45 brightens a dim source: -level 0%,45%),
            or [black, white] ([25, 100] dims a washed-out one), or [black, white, gamma]
  erode     optional disk radius that thins faint stars in a crowded field (1.5)
  reject    optional true: the maintainer turned the card down (Placeholder)
  file      optional: an author-supplied image already in the cache, used instead of
            downloading `source` (which then names where it is published)
It writes `art` (the WebP's file name) on every row it built, null otherwise.

Needs ImageMagick (`magick`) and libwebp (`cwebp`) on PATH. Downloads are
cached in ~/.cache/openastro-ara-album so a rebuild after a crop tweak is local.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ALBUM = REPO_ROOT / "client" / "openastroara_client" / "assets" / "album"
MANIFEST = ALBUM / "manifest.json"
ART = ALBUM / "art"
CACHE = Path.home() / ".cache" / "openastro-ara-album"
USER_AGENT = "OpenAstroAra-SkyAlbum/1.0 (https://github.com/open-astro/openastro-ara)"
COMMONS_API = "https://commons.wikimedia.org/w/api.php"

CARD_W, CARD_H = 560, 672
# About 31 KB a card, 7 MB for 219 (the spec's budget).
TARGET_BYTES = 31_000
# Sources at least this much wider than tall are turned upright.
UPRIGHT_ASPECT = 1.15
# Downloaded at this long side: plenty for 672 px, small enough to fetch 219.
FETCH_PX = 1600
SHEET_FONTS = ["/System/Library/Fonts/Supplemental/Arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]

ALLOWED_LICENCE = re.compile(r"^(CC BY( \d(\.\d)?)?|CC0( 1\.0)?|Public domain)$")


def licence_ok(licence: str | None) -> bool:
    """CC BY (any version), CC0 or public domain only: the card crops and dims the Art,
    so no ShareAlike, NonCommercial or NoDerivatives."""
    return bool(licence) and ALLOWED_LICENCE.match(licence) is not None


def art_name(object_id: str) -> str:
    """M31 -> m031.webp, C9 -> c009.webp: sorts in Book order."""
    m = re.fullmatch(r"([MC])(\d{1,3})", object_id)
    if not m:
        raise ValueError(f"not a Messier or Caldwell id: {object_id!r}")
    return f"{m[1].lower()}{int(m[2]):03d}.webp"


def expected_ids() -> list[str]:
    return [f"M{n}" for n in range(1, 111)] + [f"C{n}" for n in range(1, 110)]


def wants_upright(width: int, height: int, rotate: bool | None) -> bool:
    return rotate if rotate is not None else width > height * UPRIGHT_ASPECT


def load() -> list[dict]:
    return json.loads(MANIFEST.read_text(encoding="utf-8"))


def save(rows: list[dict]) -> None:
    MANIFEST.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def check(rows: list[dict]) -> list[str]:
    """Every problem with the manifest and the committed Art, empty when sound."""
    problems = []
    ids = [r.get("id") for r in rows]
    if ids != expected_ids():
        problems.append("ids are not M1..M110 then C1..C109 in order")
    for r in rows:
        rid = str(r.get("id"))
        art = r.get("art")
        if art is None:
            continue
        if not re.fullmatch(r"[MC]\d{1,3}", rid) or art != art_name(rid):
            problems.append(f"{rid}: art {art!r} does not match the id")
        if not (ART / art).is_file():
            problems.append(f"{rid}: {art} is missing")
        if r.get("reject"):
            problems.append(f"{rid}: rejected but still has art")
        if not licence_ok(r.get("licence")):
            problems.append(f"{rid}: licence {r.get('licence')!r} is not CC BY, CC0 or public domain")
        if not r.get("credit") or not r.get("source"):
            problems.append(f"{rid}: art without a credit and source")
    named = {r["art"] for r in rows if r.get("art")}
    for f in ART.glob("*.webp"):
        if f.name not in named:
            problems.append(f"{f.name}: no manifest row points at it")
    return problems


def _get(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=60) as res:
                return res.read()
        except urllib.error.HTTPError as e:
            if e.code != 429 or attempt == 3:
                raise
            time.sleep(5 * (attempt + 1))
    raise AssertionError("unreachable")


def fetch_source(source: str, px: int = FETCH_PX) -> Path:
    """The Commons file at px on its long side, cached by file name."""
    title = urllib.parse.unquote(source.rsplit("/wiki/", 1)[-1])
    cached = CACHE / (re.sub(r"[^A-Za-z0-9._-]", "_", title) + (f".{px}px" if px != FETCH_PX else ""))
    if cached.exists():
        return cached
    q = urllib.parse.urlencode({
        "action": "query", "titles": title, "prop": "imageinfo", "iiprop": "url|size",
        "iiurlwidth": px, "format": "json",
    })
    info = json.loads(_get(f"{COMMONS_API}?{q}"))
    page = next(iter(info["query"]["pages"].values()))
    ii = page["imageinfo"][0]
    # A portrait original asks for px on its height instead.
    url = ii.get("thumburl") or ii["url"]
    if ii["height"] > ii["width"]:
        q = urllib.parse.urlencode({
            "action": "query", "titles": title, "prop": "imageinfo", "iiprop": "url",
            "iiurlheight": px, "format": "json",
        })
        page = next(iter(json.loads(_get(f"{COMMONS_API}?{q}"))["query"]["pages"].values()))
        url = page["imageinfo"][0].get("thumburl") or url
    CACHE.mkdir(parents=True, exist_ok=True)
    cached.write_bytes(_get(url))
    time.sleep(0.5)  # be polite to Commons
    return cached


def _size(path: Path) -> tuple[int, int]:
    out = subprocess.run(["magick", "identify", "-format", "%w %h", f"{path}[0]"],
                         check=True, capture_output=True, text=True).stdout.split()
    return int(out[0]), int(out[1])


FADE_PX = 40


def fade_edges(inner: Path, out: Path) -> None:
    """Centres a contained picture on the black card, fading its edges that face the bars
    into black over FADE_PX so the picture's sky has no hard line against them."""
    iw, ih = _size(inner)
    sideways = iw < CARD_W  # bars left and right: build the mask upright, then turn it
    mw, mh = (ih, iw) if sideways else (iw, ih)
    mask = ["(", "-size", f"{mw}x{mh}", "xc:white",
            "(", "-size", f"{mw}x{FADE_PX}", "gradient:black-white", ")", "-gravity", "north", "-composite",
            "(", "-size", f"{mw}x{FADE_PX}", "gradient:white-black", ")", "-gravity", "south", "-composite",
            *(["-rotate", "90"] if sideways else []), ")"]
    subprocess.run(["magick", "-size", f"{CARD_W}x{CARD_H}", "xc:black",
                    "(", str(inner), *mask, "-alpha", "off", "-compose", "CopyOpacity", "-composite", ")",
                    "-gravity", "center", "-compose", "over", "-composite", str(out)], check=True)


def build_card(row: dict) -> str:
    """Writes the row's WebP and returns its file name."""
    # An author's own image not on Commons (C82) is read from the cache by the file name in `file`.
    src = CACHE / row["file"] if row.get("file") else fetch_source(row["source"], row.get("fetch_px", FETCH_PX))
    w, h = _size(src)
    args = ["magick", f"{src}[0]", "-auto-orient", "-colorspace", "sRGB"]
    if angle := row.get("angle"):
        # Turned about the source centre, then the crop box (which may reach past the edges)
        # is the viewport; past the edges the sky is mirrored, so no black wedges (C34).
        x, y, cw, ch = row["crop"]
        args += ["-virtual-pixel", "mirror",
                 "-define", f"distort:viewport={round(cw * w)}x{round(ch * h)}{round(x * w):+d}{round(y * h):+d}",
                 "-distort", "SRT", str(-angle), "+repage"]
        w, h = round(cw * w), round(ch * h)
    elif crop := row.get("crop"):
        x, y, cw, ch = crop
        args += ["-crop", f"{round(cw * w)}x{round(ch * h)}+{round(x * w)}+{round(y * h)}", "+repage"]
        w, h = round(cw * w), round(ch * h)
    if erode := row.get("erode"):
        # Thins the faint star field so a crowded Milky Way frame reads calmer (C89).
        args += ["-morphology", "Erode", f"Disk:{erode}"]
    if level := row.get("level"):
        black, white, *gamma = level if isinstance(level, list) else (0, level)
        args += ["-level", ",".join([f"{black}%", f"{white}%", *map(str, gamma)])]
    if wants_upright(w, h, row.get("rotate")):
        args += ["-rotate", "90"]
    # Fill the card, trimming the overflow evenly (no black bars); a row whose object the trim
    # would cut sets "fit": "contain" to keep the whole picture on black instead (M104).
    contain = row.get("fit") == "contain"
    size = f"{CARD_W}x{CARD_H}" if contain else f"{CARD_W}x{CARD_H}^"
    args += ["-resize", size, "-strip"]
    name = art_name(row["id"])
    with tempfile.TemporaryDirectory() as tmp:
        png = Path(tmp) / "card.png"
        if contain:
            inner = Path(tmp) / "inner.png"
            subprocess.run([*args, str(inner)], check=True)
            fade_edges(inner, png)
        else:
            subprocess.run([*args, "-gravity", "center", "-extent", f"{CARD_W}x{CARD_H}", str(png)], check=True)
        ART.mkdir(parents=True, exist_ok=True)
        out = ART / name
        # The highest quality that fits the budget. cwebp's own -size overshoots badly on
        # mostly black frames, so search -q instead.
        lo, hi, best = 5, 95, 5
        while lo <= hi:
            q = (lo + hi) // 2
            subprocess.run(["cwebp", "-quiet", "-q", str(q), "-m", "6", str(png), "-o", str(out)], check=True)
            if out.stat().st_size <= TARGET_BYTES:
                best, lo = q, q + 1
            else:
                hi = q - 1
        subprocess.run(["cwebp", "-quiet", "-q", str(best), "-m", "6", str(png), "-o", str(out)], check=True)
    return name


def cmd_build(ids: list[str]) -> int:
    rows = load()
    failed = []
    for r in rows:
        if ids and r["id"] not in ids:
            continue
        old = r.get("art")
        if r.get("reject") or not r.get("source"):
            r["art"] = None
        elif not licence_ok(r.get("licence")):
            print(f"{r['id']}: licence {r.get('licence')!r} refused", file=sys.stderr)
            r["art"] = None
        else:
            try:
                r["art"] = build_card(r)
                print(f"{r['id']}: {r['art']} {(ART / r['art']).stat().st_size // 1000} KB")
            except Exception as e:  # one bad source must not stop the other 218
                print(f"{r['id']}: {e}", file=sys.stderr)
                failed.append(r["id"])
                r["art"] = None
        if old and r["art"] is None and (ART / old).exists():
            (ART / old).unlink()
    save(rows)
    if failed:
        print(f"failed: {' '.join(failed)}", file=sys.stderr)
    return 1 if failed else 0


def cmd_sheet(out: str) -> int:
    rows = load()
    with tempfile.TemporaryDirectory() as tmp:
        tiles = []
        for r in rows:
            label = f"{r['id']} {r.get('name', '')}"[:28]
            if r.get("art"):
                tiles += ["-label", f"{label}\n{r.get('licence', '')}", str(ART / r["art"])]
            else:
                blank = Path(tmp) / f"{r['id']}.png"
                subprocess.run(["magick", "-size", f"{CARD_W}x{CARD_H}", "xc:#333", str(blank)], check=True)
                tiles += ["-label", f"{label}\nPLACEHOLDER", str(blank)]
        # ImageMagick has no default font on macOS; name one when it is there.
        font = next((["-font", f] for f in SHEET_FONTS if Path(f).is_file()), [])
        subprocess.run(["magick", "montage", *font, *tiles, "-tile", "12x", "-geometry", "140x168+4+4",
                        "-background", "#111", "-fill", "white", "-pointsize", "10", out], check=True)
    print(out)
    return 0


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("ids", nargs="*")
    s = sub.add_parser("sheet")
    s.add_argument("out")
    sub.add_parser("check")
    a = p.parse_args(argv)
    if a.cmd in ("build", "sheet") and not (shutil.which("magick") and shutil.which("cwebp")):
        print("needs ImageMagick (magick) and libwebp (cwebp) on PATH", file=sys.stderr)
        return 2
    if a.cmd == "build":
        return cmd_build(a.ids)
    if a.cmd == "sheet":
        return cmd_sheet(a.out)
    problems = check(load())
    for line in problems:
        print(line, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
