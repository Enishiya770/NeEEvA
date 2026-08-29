#!/usr/bin/env python3
"""Prepare/install the private SailingMoon Unity assets without using Unity's broad importer.

The source package mixes the room with VRChat/Udon examples and third-party
gimmicks. ``prepare`` extracts only the bundled visual families needed by the
Day scene into an ignored staging project. ``install`` accepts only paths below
Assets/Private/SailingMoon from the sanitized package produced by Unity.
"""

from __future__ import annotations

import argparse
import pathlib
import tarfile
from dataclasses import dataclass


DAY_SCENE = "Assets/モサンゴ屋/SailingMoon/-Day-SailingMoon.unity"
ALLOWED_SOURCE_PREFIXES = (
    "Assets/モサンゴ屋/SailingMoon",
    "Assets/Coquelicotz_Shelf",
    "Assets/CQ_Books_5th",
    "Assets/CQ_Plants",
    "Assets/CQ_Telescope",
    "Assets/world/Coquelicotz",
)
PRIVATE_PREFIX = "Assets/Private/SailingMoon"


@dataclass(frozen=True)
class PackageEntry:
    guid: str
    pathname: str
    asset_member: tarfile.TarInfo | None
    meta_member: tarfile.TarInfo | None


def _normalized_relative(pathname: str, prefix: str) -> pathlib.PurePosixPath:
    path = pathlib.PurePosixPath(pathname)
    prefix_path = pathlib.PurePosixPath(prefix)
    try:
        return path.relative_to(prefix_path)
    except ValueError as exc:
        raise ValueError(f"Path is outside {prefix}: {pathname}") from exc


def _read_entries(package_path: pathlib.Path) -> list[PackageEntry]:
    pathnames: dict[str, str] = {}
    with tarfile.open(package_path, "r:gz") as archive:
        for member in archive:
            if not member.name.endswith("/pathname"):
                continue
            stream = archive.extractfile(member)
            if stream is not None:
                pathnames[member.name.split("/", 1)[0]] = stream.read().decode(
                    "utf-8", "replace"
                ).strip()

    entries: list[PackageEntry] = []
    with tarfile.open(package_path, "r:gz") as archive:
        members = {member.name: member for member in archive.getmembers()}
        for guid, pathname in pathnames.items():
            entries.append(
                PackageEntry(
                    guid=guid,
                    pathname=pathname,
                    asset_member=members.get(f"{guid}/asset"),
                    meta_member=members.get(f"{guid}/asset.meta"),
                )
            )
    return entries


def _is_visual_source(pathname: str) -> bool:
    if not any(
        pathname == prefix or pathname.startswith(prefix + "/")
        for prefix in ALLOWED_SOURCE_PREFIXES
    ):
        return False
    if "/-Night-SailingMoon" in pathname:
        return False
    if pathname.lower().endswith(".unity") and pathname != DAY_SCENE:
        return False
    return True


def _write_entry(
    archive: tarfile.TarFile,
    entry: PackageEntry,
    destination: pathlib.Path,
) -> tuple[int, int]:
    """Write one Unity asset and its .meta. Returns (file_count, bytes_written)."""
    file_count = 0
    bytes_written = 0
    if entry.asset_member is None:
        destination.mkdir(parents=True, exist_ok=True)
        meta_destination = destination.with_name(destination.name + ".meta")
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        stream = archive.extractfile(entry.asset_member)
        if stream is None:
            raise RuntimeError(f"Unable to read asset: {entry.pathname}")
        payload = stream.read()
        destination.write_bytes(payload)
        file_count += 1
        bytes_written += len(payload)
        meta_destination = destination.with_name(destination.name + ".meta")

    if entry.meta_member is not None:
        stream = archive.extractfile(entry.meta_member)
        if stream is None:
            raise RuntimeError(f"Unable to read metadata: {entry.pathname}")
        payload = stream.read()
        meta_destination.parent.mkdir(parents=True, exist_ok=True)
        meta_destination.write_bytes(payload)
        file_count += 1
        bytes_written += len(payload)

    return file_count, bytes_written


def prepare(package_path: pathlib.Path, staging_project: pathlib.Path) -> None:
    if not package_path.is_file():
        raise FileNotFoundError(package_path)
    assets_root = staging_project / "Assets" / "Private" / "SailingMoon" / "Source"
    entries = [entry for entry in _read_entries(package_path) if _is_visual_source(entry.pathname)]
    if not any(entry.pathname == DAY_SCENE for entry in entries):
        raise RuntimeError(f"Day scene not found in {package_path}")

    file_count = 0
    bytes_written = 0
    with tarfile.open(package_path, "r:gz") as archive:
        for entry in sorted(
            entries,
            key=lambda item: (
                item.asset_member.offset if item.asset_member is not None else 0
            ),
        ):
            relative = _normalized_relative(entry.pathname, "Assets")
            destination = assets_root.joinpath(*relative.parts)
            count, size = _write_entry(archive, entry, destination)
            file_count += count
            bytes_written += size

    print(
        f"Prepared {len(entries)} Unity entries / {file_count} files / "
        f"{bytes_written / (1024 * 1024):.1f} MiB in {assets_root}"
    )


def install(package_path: pathlib.Path, project_root: pathlib.Path) -> None:
    if not package_path.is_file():
        raise FileNotFoundError(package_path)
    entries = _read_entries(package_path)
    rejected = [
        entry.pathname
        for entry in entries
        if not (
            entry.pathname == PRIVATE_PREFIX
            or entry.pathname.startswith(PRIVATE_PREFIX + "/")
        )
    ]
    if rejected:
        preview = "\n".join(rejected[:20])
        raise RuntimeError(
            "Sanitized package contains non-private paths; refusing installation:\n"
            + preview
        )

    file_count = 0
    bytes_written = 0
    with tarfile.open(package_path, "r:gz") as archive:
        for entry in sorted(
            entries,
            key=lambda item: (
                item.asset_member.offset if item.asset_member is not None else 0
            ),
        ):
            relative = _normalized_relative(entry.pathname, "Assets")
            destination = project_root / "Assets"
            destination = destination.joinpath(*relative.parts)
            count, size = _write_entry(archive, entry, destination)
            file_count += count
            bytes_written += size

    print(
        f"Installed {len(entries)} private Unity entries / {file_count} files / "
        f"{bytes_written / (1024 * 1024):.1f} MiB into {project_root}"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    prepare_parser = subparsers.add_parser("prepare")
    prepare_parser.add_argument("package", type=pathlib.Path)
    prepare_parser.add_argument("staging_project", type=pathlib.Path)

    install_parser = subparsers.add_parser("install")
    install_parser.add_argument("package", type=pathlib.Path)
    install_parser.add_argument("project_root", type=pathlib.Path)

    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.package.resolve(), args.staging_project.resolve())
    else:
        install(args.package.resolve(), args.project_root.resolve())


if __name__ == "__main__":
    main()
