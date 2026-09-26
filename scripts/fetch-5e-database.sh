#!/usr/bin/env bash
#
# Re-vendors the 5e-bits 5e-database JSON (English, 2014 + 2024) at a pinned monorepo tag and
# regenerates content/5e-database/manifest.json.
#
#   scripts/fetch-5e-database.sh [tag]        # default tag: 5e-database-v7.0.0
#
# Why this exists instead of hand-copying files:
#   * The server never calls the live API (PLAN.md D1). Everything it knows about the SRD comes from
#     these bytes, so they must be exactly what 5e-bits tagged, and provably so.
#   * The manifest's sha256 values decide when srd.db is rebuilt (Phase 2) and are pinned by
#     DndMcp.Tests/Srd. A hand edit to a vendored file fails those tests on purpose.
#   * Re-runs at the same tag must be byte-identical (no timestamps anywhere), so re-vendoring shows
#     up in git as "no change" unless upstream really changed.
#
# Where the data lives: the old 5e-bits/5e-database repo is ARCHIVED and stale. The live data is the
# monorepo 5e-bits/5e-srd-api under packages/5e-database/src/{2014,2024}/en/.
#
# Integrity: the file list and each file's sha256 come from jsDelivr's data API; the bytes come from
# raw.githubusercontent.com at the same tag. Both must agree, or the script fails before touching
# the vendored tree.
#
# Requires: bash, curl, python3, sha256sum.

set -euo pipefail

TAG="${1:-5e-database-v7.0.0}"
REPOSITORY="5e-bits/5e-srd-api"
PACKAGE_PATH="packages/5e-database"
EDITIONS=(2014 2024)
LANGUAGE="en"

# 5e-database-v7.0.0 -> v7.0.0. The directory name carries the version so a re-vendor at a new tag is
# a visible rename in the diff, not a silent in-place overwrite.
VERSION="${TAG#5e-database-}"
if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "error: tag '$TAG' is not of the form 5e-database-vX.Y.Z" >&2
    exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTENT_DIR="$REPO_ROOT/content/5e-database"
LICENSE_DEST="$REPO_ROOT/content/LICENSES/5e-database-MIT.txt"

LISTING_URL="https://data.jsdelivr.com/v1/packages/gh/${REPOSITORY}@${TAG}?structure=flat"
RAW_BASE="https://raw.githubusercontent.com/${REPOSITORY}/${TAG}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fetch() {
    # -f: an HTTP error must fail the script, never land an HTML error page in the vendored tree.
    curl -fsSL --retry 3 --retry-delay 2 "$1" -o "$2"
}

echo "Listing ${REPOSITORY}@${TAG} ..." >&2
fetch "$LISTING_URL" "$WORK/listing.json"

# One line per vendored file: <edition> <filename> <size> <sha256-hex>
python3 - "$WORK/listing.json" "$PACKAGE_PATH" "$LANGUAGE" "${EDITIONS[@]}" > "$WORK/files.tsv" <<'PY'
import base64, json, re, sys

listing_path, package_path, language, *editions = sys.argv[1:]
with open(listing_path, encoding="utf-8") as f:
    listing = json.load(f)

pattern = re.compile(
    r"^/" + re.escape(package_path) + r"/src/(" + "|".join(map(re.escape, editions)) + r")/"
    + re.escape(language) + r"/([^/]+\.json)$")

rows = []
for entry in listing["files"]:
    match = pattern.match(entry["name"])
    if match:
        sha_hex = base64.b64decode(entry["hash"]).hex()
        rows.append((match.group(1), match.group(2), int(entry["size"]), sha_hex))

for edition in editions:
    if not any(r[0] == edition for r in rows):
        sys.exit(f"error: no {edition}/{language} JSON files listed at this tag")

for row in sorted(rows):
    print("\t".join(map(str, row)))
PY

STAGE="$WORK/stage/$VERSION"
mkdir -p "$STAGE"

while IFS=$'\t' read -r edition name size sha; do
    mkdir -p "$STAGE/$edition"
    target="$STAGE/$edition/$name"
    echo "  $edition/$name" >&2
    fetch "$RAW_BASE/$PACKAGE_PATH/src/$edition/$LANGUAGE/$name" "$target"

    actual_size="$(wc -c < "$target" | tr -d ' ')"
    actual_sha="$(sha256sum "$target" | cut -d' ' -f1)"
    if [[ "$actual_size" != "$size" || "$actual_sha" != "$sha" ]]; then
        echo "error: $edition/$name from GitHub ($actual_size bytes, $actual_sha) does not match jsDelivr's listing ($size bytes, $sha)" >&2
        exit 1
    fi

    python3 -m json.tool "$target" > /dev/null
done < "$WORK/files.tsv"

# The data package carries its own LICENSE.md; fall back to the monorepo root if a later tag drops it.
if ! fetch "$RAW_BASE/$PACKAGE_PATH/LICENSE.md" "$WORK/LICENSE.md" 2> /dev/null; then
    fetch "$RAW_BASE/LICENSE.md" "$WORK/LICENSE.md"
fi
if ! grep -q "MIT License" "$WORK/LICENSE.md"; then
    echo "error: the 5e-database license at $TAG is no longer MIT; review before vendoring" >&2
    exit 1
fi

# Manifest paths are relative to content/5e-database and sorted, so the file is a pure function of the
# tag's bytes. No timestamps: a re-run must not produce a diff.
python3 - "$WORK/stage" "$TAG" "https://github.com/${REPOSITORY}" "$PACKAGE_PATH/src/{edition}/$LANGUAGE" > "$WORK/manifest.json" <<'PY'
import hashlib, json, os, sys

stage, tag, repository, source_path = sys.argv[1:]
files = []
for dirpath, _, filenames in os.walk(stage):
    for filename in filenames:
        full = os.path.join(dirpath, filename)
        with open(full, "rb") as f:
            data = f.read()
        files.append({
            "path": os.path.relpath(full, stage).replace(os.sep, "/"),
            "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": len(data),
        })

files.sort(key=lambda entry: entry["path"])
manifest = {"tag": tag, "repository": repository, "source_path": source_path, "files": files}
sys.stdout.write(json.dumps(manifest, indent=2, ensure_ascii=True) + "\n")
PY

# Everything downloaded and verified: only now replace the vendored tree. Other version directories
# are removed, because the manifest describes exactly one tag and the tests reject unlisted files.
mkdir -p "$CONTENT_DIR" "$(dirname "$LICENSE_DEST")"
find "$CONTENT_DIR" -mindepth 1 -maxdepth 1 -type d -name 'v*' -exec rm -rf {} +
mv "$STAGE" "$CONTENT_DIR/$VERSION"
mv "$WORK/manifest.json" "$CONTENT_DIR/manifest.json"
mv "$WORK/LICENSE.md" "$LICENSE_DEST"

count="$(wc -l < "$WORK/files.tsv" | tr -d ' ')"
echo "Vendored $count files at $TAG into content/5e-database/$VERSION" >&2
