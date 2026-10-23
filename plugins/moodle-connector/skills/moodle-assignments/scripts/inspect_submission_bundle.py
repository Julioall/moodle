#!/usr/bin/env python3
"""Safely inspect a local submission bundle without extracting it to disk."""

from __future__ import annotations

import argparse
import io
import json
import mimetypes
import sys
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any


DEFAULT_MAX_DEPTH = 3
DEFAULT_MAX_MEMBERS = 1000
DEFAULT_MAX_UNCOMPRESSED_BYTES = 256 * 1024 * 1024
DEFAULT_MAX_COMPRESSION_RATIO = 100.0
MAX_HEADER_BYTES = 4096


def detect_format(name: str, header: bytes, container: str | None = None) -> str:
    if container:
        return container
    if header.startswith(b"%PDF"):
        return "pdf"
    if header.startswith(b"\x89PNG\r\n\x1a\n"):
        return "png"
    if header.startswith(b"\xff\xd8\xff"):
        return "jpeg"
    if header.startswith((b"GIF87a", b"GIF89a")):
        return "gif"
    if header.startswith(b"RIFF") and header[8:12] == b"WEBP":
        return "webp"
    if header.startswith(b"ID3") or header.startswith(b"\xff\xfb"):
        return "mp3"
    if len(header) >= 12 and header[4:8] == b"ftyp":
        return "mp4"
    if header.startswith(b"PK\x03\x04"):
        return "zip"

    suffix = Path(name).suffix.lower().lstrip(".")
    return {
        "docx": "docx",
        "xlsx": "xlsx",
        "pptx": "pptx",
        "odt": "odt",
        "ods": "ods",
        "odp": "odp",
        "csv": "csv",
        "txt": "txt",
        "html": "html",
        "htm": "html",
        "json": "json",
        "xml": "xml",
    }.get(suffix, "unknown")


def detect_container(archive: zipfile.ZipFile) -> str:
    infos = archive.infolist()
    normalized = {info.filename.replace("\\", "/") for info in infos}
    if "[Content_Types].xml" in normalized:
        if any(name.startswith("word/") for name in normalized):
            return "docx"
        if any(name.startswith("xl/") for name in normalized):
            return "xlsx"
        if any(name.startswith("ppt/") for name in normalized):
            return "pptx"
    if "mimetype" in normalized:
        try:
            mime = archive.read("mimetype")[:256].decode("ascii", errors="ignore").strip()
        except (KeyError, RuntimeError, OSError):
            mime = ""
        return {
            "application/vnd.oasis.opendocument.text": "odt",
            "application/vnd.oasis.opendocument.spreadsheet": "ods",
            "application/vnd.oasis.opendocument.presentation": "odp",
        }.get(mime, "zip")
    return "zip"


def mime_for(format_name: str, filename: str) -> str | None:
    known = {
        "pdf": "application/pdf",
        "docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "zip": "application/zip",
        "png": "image/png",
        "jpeg": "image/jpeg",
        "gif": "image/gif",
        "webp": "image/webp",
        "mp3": "audio/mpeg",
        "mp4": "video/mp4",
        "txt": "text/plain",
        "csv": "text/csv",
        "html": "text/html",
        "json": "application/json",
        "xml": "application/xml",
    }
    return known.get(format_name) or mimetypes.guess_type(filename)[0]


def unsafe_member_name(name: str) -> bool:
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    return normalized.startswith("/") or any(part == ".." for part in path.parts)


class ScanState:
    def __init__(self, options: argparse.Namespace) -> None:
        self.options = options
        self.members = 0
        self.uncompressed_bytes = 0
        self.records: list[dict[str, Any]] = []
        self.warnings: set[str] = set()

    def budget_allows(self, size: int) -> bool:
        if self.members >= self.options.max_members:
            self.warnings.add("max_members_exceeded")
            return False
        if self.uncompressed_bytes + size > self.options.max_uncompressed_bytes:
            self.warnings.add("max_uncompressed_bytes_exceeded")
            return False
        self.members += 1
        self.uncompressed_bytes += size
        return True


def scan_archive(archive: zipfile.ZipFile, label: str, depth: int, state: ScanState) -> None:
    infos = archive.infolist()
    container = detect_container(archive)
    if depth > state.options.max_depth:
        state.warnings.add("max_depth_exceeded")
        return

    for info in infos:
        if info.is_dir():
            continue
        name = info.filename
        record: dict[str, Any] = {
            "path": f"{label}!/{name}",
            "name": name,
            "sizeBytes": info.file_size,
            "compressedSizeBytes": info.compress_size,
            "format": detect_format(name, b"", container if depth == 0 and name == infos[0].filename else None),
            "mimeType": mime_for(detect_format(name, b""), name),
            "encrypted": bool(info.flag_bits & 0x1),
            "nestedArchive": False,
            "warnings": [],
        }

        if unsafe_member_name(name):
            record["warnings"].append("unsafe_path")
            state.warnings.add("unsafe_path")
            state.records.append(record)
            continue
        if record["encrypted"]:
            record["warnings"].append("encrypted_member")
            state.warnings.add("encrypted_member")
        if info.compress_size and info.file_size / info.compress_size > state.options.max_compression_ratio:
            record["warnings"].append("high_compression_ratio")
            state.warnings.add("high_compression_ratio")
        if not state.budget_allows(info.file_size):
            record["warnings"].append("scan_budget_exceeded")
            state.records.append(record)
            continue

        try:
            payload = archive.read(info)
        except (RuntimeError, OSError, zipfile.BadZipFile) as exc:
            record["warnings"].append(f"read_failed:{type(exc).__name__}")
            state.warnings.add("member_read_failed")
            state.records.append(record)
            continue

        format_name = detect_format(name, payload[:MAX_HEADER_BYTES])
        record["format"] = format_name
        record["mimeType"] = mime_for(format_name, name)
        record["signatureChecked"] = True
        if format_name == "unknown":
            record["warnings"].append("unknown_signature")
            state.warnings.add("unknown_signature")
        state.records.append(record)

        if format_name == "zip":
            record["nestedArchive"] = True
            if depth >= state.options.max_depth:
                record["warnings"].append("max_depth_exceeded")
                state.warnings.add("max_depth_exceeded")
                continue
            try:
                with zipfile.ZipFile(io.BytesIO(payload)) as nested:
                    scan_archive(nested, record["path"], depth + 1, state)
            except zipfile.BadZipFile:
                record["warnings"].append("invalid_nested_zip")
                state.warnings.add("invalid_nested_zip")


def inspect(path: Path, options: argparse.Namespace) -> dict[str, Any]:
    state = ScanState(options)
    result: dict[str, Any] = {
        "path": str(path.resolve()),
        "readable": path.is_file(),
        "status": "failed",
        "format": "unknown",
        "mimeType": None,
        "archive": False,
        "maxDepth": options.max_depth,
        "warnings": [],
        "files": state.records,
    }
    if not path.is_file():
        result["warnings"] = ["file_not_found"]
        return result

    try:
        with path.open("rb") as stream:
            header = stream.read(MAX_HEADER_BYTES)
        if zipfile.is_zipfile(path):
            result["archive"] = True
            with zipfile.ZipFile(path) as archive:
                result["format"] = detect_container(archive)
                result["mimeType"] = mime_for(result["format"], path.name)
                scan_archive(archive, str(path.resolve()), 0, state)
        else:
            result["format"] = detect_format(path.name, header)
            result["mimeType"] = mime_for(result["format"], path.name)
            result["signatureChecked"] = True
            if result["format"] == "unknown":
                state.warnings.add("unknown_signature")
            state.records.append({
                "path": str(path.resolve()),
                "name": path.name,
                "sizeBytes": path.stat().st_size,
                "format": result["format"],
                "mimeType": result["mimeType"],
                "signatureChecked": True,
                "warnings": [],
            })
    except (OSError, zipfile.BadZipFile) as exc:
        state.warnings.add(f"archive_read_failed:{type(exc).__name__}")

    result["files"] = state.records
    result["summary"] = {
        "fileCount": len(state.records),
        "membersScanned": state.members,
        "uncompressedBytesCounted": state.uncompressed_bytes,
    }
    result["warnings"] = sorted(state.warnings)
    result["status"] = "complete" if not state.warnings else "partial"
    result["readable"] = result["readable"] and result["status"] != "failed"
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    parser.add_argument("--max-depth", type=int, default=DEFAULT_MAX_DEPTH)
    parser.add_argument("--max-members", type=int, default=DEFAULT_MAX_MEMBERS)
    parser.add_argument("--max-uncompressed-bytes", type=int, default=DEFAULT_MAX_UNCOMPRESSED_BYTES)
    parser.add_argument("--max-compression-ratio", type=float, default=DEFAULT_MAX_COMPRESSION_RATIO)
    parser.add_argument("--pretty", action="store_true")
    args = parser.parse_args()
    if args.max_depth < 0 or args.max_members < 1 or args.max_uncompressed_bytes < 1 or args.max_compression_ratio <= 0:
        parser.error("limits must be positive; max-depth may be zero")

    result = inspect(args.path, args)
    print(json.dumps(result, ensure_ascii=False, indent=2 if args.pretty else None, sort_keys=True))
    return 0 if result["status"] != "failed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
