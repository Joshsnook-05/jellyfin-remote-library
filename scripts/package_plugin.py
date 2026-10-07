#!/usr/bin/env python3
"""Build a deterministic Jellyfin plugin archive and optionally update manifest.json."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import zipfile
from datetime import datetime
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PLUGIN_NAME = "Jellyfin.Plugin.RemoteLibrary"
DLL = ROOT / PLUGIN_NAME / "bin" / "Release" / "net10.0" / f"{PLUGIN_NAME}.dll"
META = ROOT / "meta.json"
MANIFEST = ROOT / "manifest.json"


def repository_from_remote() -> str:
    try:
        remote = subprocess.check_output(
            ["git", "remote", "get-url", "origin"], cwd=ROOT, text=True
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return "Joshsnook-05/jellyfin-remote-library"

    match = re.search(r"github\.com[:/]([^/]+/[^/]+?)(?:\.git)?$", remote)
    return match.group(1) if match else "Joshsnook-05/jellyfin-remote-library"


def zip_timestamp(timestamp: str) -> tuple[int, int, int, int, int, int]:
    parsed = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
    # ZIP timestamps cannot represent years before 1980 and have two-second precision.
    return (parsed.year, parsed.month, parsed.day, parsed.hour, parsed.minute, parsed.second // 2 * 2)


def add_file(archive: zipfile.ZipFile, source: Path, name: str, timestamp: tuple[int, ...]) -> None:
    info = zipfile.ZipInfo(name, timestamp)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    archive.writestr(info, source.read_bytes())


def update_manifest(meta: dict[str, object], source_url: str, checksum: str) -> None:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    plugin = next((item for item in manifest if item.get("guid") == meta["guid"]), None)
    if plugin is None:
        plugin = {}
        manifest.append(plugin)

    plugin.update(
        {
            "guid": meta["guid"],
            "name": meta["name"],
            "description": meta["description"],
            "overview": meta["overview"],
            "owner": meta["owner"],
            "category": meta["category"],
        }
    )
    version_entry = {
        "version": meta["version"],
        "changelog": meta["changelog"],
        "targetAbi": meta["targetAbi"],
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": meta["timestamp"],
    }
    versions = [item for item in plugin.get("versions", []) if item.get("version") != meta["version"]]
    versions.append(version_entry)
    versions.sort(
        key=lambda item: tuple(int(part) for part in str(item["version"]).split(".")),
        reverse=True,
    )
    plugin["versions"] = versions
    manifest.sort(key=lambda item: str(item["name"]).casefold())
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", default="dist")
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY") or repository_from_remote())
    parser.add_argument("--tag")
    parser.add_argument("--update-manifest", action="store_true")
    args = parser.parse_args()

    if not DLL.is_file():
        raise SystemExit(f"Release DLL not found: {DLL}. Run dotnet build -c Release first.")

    source_files = [
        source
        for pattern in ("*.cs", "*.csproj")
        for source in (ROOT / PLUGIN_NAME).rglob(pattern)
        if "bin" not in source.parts and "obj" not in source.parts
    ]
    if any(source.stat().st_mtime > DLL.stat().st_mtime for source in source_files):
        raise SystemExit("Release DLL is older than the plugin source. Run dotnet build -c Release first.")

    meta = json.loads(META.read_text(encoding="utf-8"))
    version = str(meta["version"])
    tag = args.tag or f"v{version}"
    if tag != f"v{version}":
        raise SystemExit(f"Release tag {tag!r} must match plugin version v{version}.")
    filename = f"jellyfin-plugin-remote-library_{version}.zip"
    output_dir = (ROOT / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / filename
    timestamp = zip_timestamp(str(meta["timestamp"]))

    with zipfile.ZipFile(output, "w") as archive:
        add_file(archive, DLL, DLL.name, timestamp)
        add_file(archive, META, META.name, timestamp)

    checksum = hashlib.md5(output.read_bytes()).hexdigest()
    source_url = f"https://github.com/{args.repository}/releases/download/{tag}/{filename}"
    if args.update_manifest:
        update_manifest(meta, source_url, checksum)

    print(json.dumps({"archive": str(output), "checksum": checksum, "sourceUrl": source_url}))


if __name__ == "__main__":
    main()
