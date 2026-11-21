#!/usr/bin/env bash
# Build the source tarball of an Ara release: the only thing a release ships
# besides its notes (docs/RELEASES.md, "What a release contains").
#
#   scripts/source-archive.sh <tag-or-ref> <outdir> [<display-version>]
#
# Writes, into <outdir>:
#   openastro-ara-<display>-source.tar.gz         the tree at <ref> with every git
#                                                 submodule (third_party/erfa,
#                                                 third_party/supernovas,
#                                                 third_party/astap) filled in, all
#                                                 under one top folder
#                                                 openastro-ara-<display>/
#   openastro-ara-<display>-source.tar.gz.sha256  its `sha256sum -c` line
#
# <display> is the SemVer spelling of the version (1.0.0-beta.1, 1.0.0):
#   - a release tag vX.Y.Z[-beta.N]: the tag without its v, and the VERSION file
#     at the tag must name the same version (scripts/release-tag.py), or it fails;
#   - any other ref (a branch, a SHA, HEAD): the VERSION file at that ref in
#     display spelling, unless <display-version> is given (for a test or a dev
#     snapshot, e.g. `HEAD out 0.0.1-dev.gabc1234`; letters, digits, . + - only).
#     A <display-version> for a release tag must match it.
#
# The archive is the same bytes every time for the same ref: git archive of the
# commit and of each submodule commit the tree records (not whatever the
# submodule has checked out), merged in name order with every mtime set to the
# commit time of <ref>, owner root, and a gzip header with no name or time.
# Each submodule must be initialised in this checkout
# (`git submodule update --init --recursive`); a submodule commit that is not
# present locally is fetched from the submodule's origin once.
#
# Needs git and python3 (stdlib only). Runs from any directory inside the checkout.
set -euo pipefail

usage() {
  sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

[ "$#" -ge 2 ] && [ "$#" -le 3 ] || usage
ref="$1"
outdir="$2"
override="${3:-}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(git -C "$here" rev-parse --show-toplevel)"
cd "$root"

commit="$(git rev-parse --verify --quiet "${ref}^{commit}")" || {
  echo "ERROR: ${ref} is not a commit in ${root}" >&2
  exit 1
}

is_tag=false
if [[ "${ref}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-beta\.[0-9]+)?$ ]]; then
  is_tag=true
fi

version_display=""
if file_version="$(git show "${commit}:VERSION" 2>/dev/null)"; then
  version_display="$(printf '%s\n' "${file_version}" | python3 "${here}/version.py" --semver --file -)" || {
    echo "ERROR: the VERSION file at ${ref} is not valid" >&2
    exit 1
  }
elif [ "${is_tag}" = true ] || [ -z "${override}" ]; then
  echo "ERROR: ${ref} has no VERSION file (an Ara release tag always has one; give <display-version> for another ref)" >&2
  exit 1
fi

if [ "${is_tag}" = true ]; then
  semver="$(python3 "${here}/release-tag.py" "${ref}" | sed -n 's/^semver=//p')"
  [ -n "${semver}" ] || exit 1
  if [ "${semver}" != "${version_display}" ]; then
    echo "ERROR: tag ${ref} names ${semver}, but its VERSION file is ${file_version} (${version_display})" >&2
    exit 1
  fi
  if [ -n "${override}" ] && [ "${override}" != "${semver}" ]; then
    echo "ERROR: version ${override} given for tag ${ref}, which is ${semver}" >&2
    exit 1
  fi
  display="${semver}"
elif [ -n "${override}" ]; then
  if ! [[ "${override}" =~ ^[0-9A-Za-z][0-9A-Za-z.+-]*$ ]]; then
    echo "ERROR: version ${override} may hold only letters, digits, '.', '+' and '-'" >&2
    exit 1
  fi
  display="${override}"
else
  display="${version_display}"
fi

name="openastro-ara-${display}"
mkdir -p "${outdir}"
outdir="$(cd "${outdir}" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/ara-source-archive.XXXXXX")"
trap 'rm -rf "${work}"' EXIT

parts="${work}/parts.txt"
: > "${parts}"
n=0

# archive <repo dir> <commit> <prefix inside the tarball>
archive() {
  local dir="$1" rev="$2" prefix="$3"
  n=$((n + 1))
  local tar="${work}/${n}.tar"
  git -C "${dir}" archive --format=tar --prefix="${prefix}" "${rev}" > "${tar}"
  printf '%s\n' "${tar}" >> "${parts}"
  local mode type sha path
  while IFS=$'\t' read -r meta path; do
    read -r mode type sha <<< "${meta}"
    [ "${type}" = "commit" ] || continue
    local sub="${dir}/${path}"
    if [ ! -e "${sub}/.git" ]; then
      echo "ERROR: submodule ${prefix#"${name}"/}${path} is not initialised; run: git submodule update --init --recursive" >&2
      exit 1
    fi
    if ! git -C "${sub}" cat-file -e "${sha}^{commit}" 2>/dev/null; then
      echo "Fetching ${path} ${sha} (not in the local submodule)" >&2
      git -C "${sub}" fetch --quiet --depth 1 origin "${sha}" </dev/null || {
        echo "ERROR: submodule ${path} has no commit ${sha}; update it and retry" >&2
        exit 1
      }
    fi
    archive "${sub}" "${sha}" "${prefix}${path}/"
  done < <(git -C "${dir}" ls-tree -r "${rev}")
}

archive "${root}" "${commit}" "${name}/"

mtime="$(git log -1 --format=%ct "${commit}")"
tarball="${name}-source.tar.gz"

python3 - "${parts}" "${outdir}/${tarball}" "${mtime}" <<'PY'
import gzip
import sys
import tarfile

parts_file, out, mtime = sys.argv[1], sys.argv[2], int(sys.argv[3])
members = {}  # name -> (open part, TarInfo); the data is read again when written
opened = []
with open(parts_file, encoding="utf-8") as f:
    parts = [line.strip() for line in f if line.strip()]
for part in parts:
    t = tarfile.open(part, mode="r:")
    opened.append(t)
    for info in t:
        if info.type in (tarfile.XGLTYPE, tarfile.XHDTYPE):
            continue
        old = members.get(info.name)
        if old is not None:
            # A submodule is an empty directory in its parent's archive and the
            # top directory of its own; any other clash is a bug.
            if old[1].isdir() and info.isdir():
                continue
            sys.exit("ERROR: %s appears twice in the archive" % info.name)
        members[info.name] = (t, info)

with open(out, "wb") as raw:
    with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as gz:
        with tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as w:
            for name in sorted(members):
                src, info = members[name]
                data = src.extractfile(info) if info.isreg() else None
                info.mtime = mtime
                info.uid = info.gid = 0
                info.uname = info.gname = "root"
                info.pax_headers = {}
                w.addfile(info, data)
for t in opened:
    t.close()
PY

(
  cd "${outdir}"
  python3 - "${tarball}" <<'PY' > "${tarball}.sha256"
import hashlib
import sys

name = sys.argv[1]
h = hashlib.sha256()
with open(name, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        h.update(chunk)
print("%s  %s" % (h.hexdigest(), name))
PY
)

echo "Wrote ${outdir}/${tarball} (${ref} = ${commit:0:12}, version ${display})"
echo "Wrote ${outdir}/${tarball}.sha256"
