#!/usr/bin/env python3
"""Download popular Codex pet spritesheets into this Unity project."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
import time
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


API_URL = "https://codex-pets.net/api/pets"
DEFAULT_OUTPUT_DIR = Path("Assets") / "StreamingAssets" / "CodexPets"
TARGET_ATLAS_SIZE = "1536x1872"
USER_AGENT = "Underwater Unity Pet Downloader/0.1"

POLITICAL_TERMS = (
    "biden",
    "bush",
    "chairman mao",
    "clinton",
    "donald trump",
    "elon",
    "erdogan",
    "governor",
    "harris",
    "hitler",
    "jinping",
    "kim jong",
    "kimlet",
    "macron",
    "mao zedong",
    "maozedong",
    "mayor",
    "merkel",
    "minister",
    "modi",
    "netanyahu",
    "obama",
    "politician",
    "president",
    "prime minister",
    "putin",
    "reagan",
    "senator",
    "stalin",
    "trump",
    "vladimir putin",
    "xi jinping",
    "xi-jinping",
    "zelensky",
    "zelenskyy",
)


def request_json(url: str) -> dict[str, Any]:
    request = Request(url, headers={"Accept": "application/json", "User-Agent": USER_AGENT})
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def download_file(url: str, output_path: Path) -> None:
    request = Request(url, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=60) as response:
        output_path.write_bytes(response.read())


def safe_filename(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "-", value.strip())
    cleaned = cleaned.strip(".-").lower()
    return cleaned or "pet"


def unity_guid(path: Path) -> str:
    return hashlib.sha1(path.as_posix().encode("utf-8")).hexdigest()[:32]


def write_unity_meta(path: Path, *, folder: bool = False) -> None:
    meta_path = Path(f"{path}.meta")
    folder_line = "folderAsset: yes\n" if folder else ""
    meta_path.write_text(
        "fileFormatVersion: 2\n"
        f"guid: {unity_guid(path)}\n"
        f"{folder_line}"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n",
        encoding="utf-8",
    )


def searchable_text(pet: dict[str, Any]) -> str:
    values: list[str] = []

    for key in ("id", "displayName", "description", "kind", "ownerHandle", "ownerName"):
        value = pet.get(key)
        if value:
            values.append(str(value))

    tags = pet.get("tags")
    if isinstance(tags, list):
        values.extend(str(tag) for tag in tags)

    return " ".join(values).casefold()


def is_political_figure(pet: dict[str, Any]) -> bool:
    text = searchable_text(pet)
    normalized = re.sub(r"[^a-z0-9]+", " ", text)
    dashed = re.sub(r"[^a-z0-9-]+", " ", text)

    for term in POLITICAL_TERMS:
        term = term.casefold()
        normalized_term = re.sub(r"[^a-z0-9]+", " ", term).strip()

        if normalized_term and f" {normalized_term} " in f" {normalized} ":
            return True

        if "-" in term and term in dashed:
            return True

    return False


def has_expected_atlas(pet: dict[str, Any]) -> bool:
    report = pet.get("validationReport")
    if not isinstance(report, dict):
        return True

    return report.get("atlasSize") == TARGET_ATLAS_SIZE


def fetch_candidates(target_count: int, page_size: int, max_pages: int) -> list[dict[str, Any]]:
    selected: list[dict[str, Any]] = []
    seen: set[str] = set()

    for page in range(1, max_pages + 1):
        query = urlencode({"page": page, "pageSize": page_size, "sort": "popular"})
        payload = request_json(f"{API_URL}?{query}")
        pets = payload.get("pets")

        if not pets:
            break

        for pet in pets:
            pet_id = str(pet.get("id") or "").strip()
            spritesheet_url = str(pet.get("spritesheetUrl") or "").strip()

            if not pet_id or pet_id in seen or not spritesheet_url:
                continue

            seen.add(pet_id)

            if is_political_figure(pet):
                print(f"skip political: {pet_id}", file=sys.stderr)
                continue

            if not has_expected_atlas(pet):
                print(f"skip atlas size: {pet_id}", file=sys.stderr)
                continue

            selected.append(pet)

            if len(selected) >= target_count:
                return selected

        time.sleep(0.2)

    return selected


def write_pet(output_dir: Path, index: int, pet: dict[str, Any]) -> None:
    pet_id = str(pet["id"]).strip()
    pet_dir = output_dir / f"{index:02d}-{safe_filename(pet_id)}"
    pet_dir.mkdir(parents=True, exist_ok=True)
    write_unity_meta(pet_dir, folder=True)

    spritesheet_path = pet_dir / "spritesheet.webp"
    download_file(str(pet["spritesheetUrl"]), spritesheet_path)
    write_unity_meta(spritesheet_path)

    manifest = {
        "id": pet_id,
        "displayName": str(pet.get("displayName") or pet_id).strip(),
        "description": str(pet.get("description") or "").strip(),
        "kind": str(pet.get("kind") or "").strip(),
        "source": "codex-pets.net popular",
        "spritesheetPath": "spritesheet.webp",
    }

    manifest_path = pet_dir / "pet.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    write_unity_meta(manifest_path)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--count", type=int, default=36, help="Number of non-political popular pets to download.")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_DIR, help="Project-local output folder.")
    parser.add_argument("--page-size", type=int, default=50, help="API page size. codex-pets.net currently accepts 50.")
    parser.add_argument("--max-pages", type=int, default=8, help="Maximum API pages to scan.")
    parser.add_argument("--keep-existing", action="store_true", help="Do not clear the output folder before downloading.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    if args.count <= 0:
        print("--count must be positive", file=sys.stderr)
        return 2

    output_dir = args.output
    selected = fetch_candidates(args.count, args.page_size, args.max_pages)

    if len(selected) < args.count:
        print(f"Only found {len(selected)} eligible pets; wanted {args.count}.", file=sys.stderr)
        return 1

    if output_dir.exists() and not args.keep_existing:
        shutil.rmtree(output_dir)

    output_dir.mkdir(parents=True, exist_ok=True)
    write_unity_meta(output_dir.parent, folder=True)
    write_unity_meta(output_dir, folder=True)

    catalog = []

    for index, pet in enumerate(selected, start=1):
        pet_id = str(pet["id"]).strip()
        print(f"download {index:02d}/{args.count}: {pet_id}")
        write_pet(output_dir, index, pet)
        catalog.append(
            {
                "id": pet_id,
                "displayName": str(pet.get("displayName") or pet_id).strip(),
                "kind": str(pet.get("kind") or "").strip(),
            }
        )

    catalog_path = output_dir / "catalog.json"
    catalog_path.write_text(json.dumps({"pets": catalog}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    write_unity_meta(catalog_path)
    print(f"Wrote {len(catalog)} pets to {output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (HTTPError, URLError, TimeoutError) as exc:
        print(f"Download failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
