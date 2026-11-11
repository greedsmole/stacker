#!/usr/bin/env python3
"""Build a self-contained Stacker archive. Requires Python 3 and .NET 10 SDK."""
import argparse
import pathlib
import platform
import plistlib
import re
import shutil
import subprocess
import tarfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
# One source of truth: the desktop project file that the application also displays at runtime.
PROJECT = ROOT / "src/Stacker.Desktop/Stacker.Desktop.csproj"
match = re.search(r"<Version>([^<]+)</Version>", PROJECT.read_text(encoding="utf-8"))
if match is None:
    raise SystemExit(f"No <Version> found in {PROJECT}")
VERSION = match.group(1)
parser = argparse.ArgumentParser()
RIDS = ["win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"]
parser.add_argument("rid", nargs="?", default="host", choices=["host", *RIDS])
args = parser.parse_args()
if args.rid == "host":
    os_name = {"Windows": "win", "Darwin": "osx", "Linux": "linux"}.get(platform.system())
    arch = {"x86_64": "x64", "amd64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    args.rid = f"{os_name}-{arch}"
    if args.rid not in RIDS:
        parser.error("Cannot detect a supported host target; specify a desktop RID explicitly.")
output = ROOT / "artifacts" / args.rid
if output.exists():
    shutil.rmtree(output)
output.mkdir(parents=True)
is_mac = args.rid.startswith("osx")
is_windows = args.rid.startswith("win")
publish = output / "Stacker.app" / "Contents" / "MacOS" if is_mac else output / "Stacker"
subprocess.run([
    "dotnet", "publish", str(ROOT / "src/Stacker.Desktop"), "-c", "Release", "-r", args.rid,
    "--self-contained", "true", f"-p:Version={VERSION}", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-o", str(publish)
], check=True, cwd=ROOT)
if not is_windows:
    (publish / "Stacker").chmod(0o755)
if is_mac:
    info = {
        "CFBundleName": "Stacker", "CFBundleDisplayName": "Stacker",
        "CFBundleIdentifier": "dev.stacker.desktop", "CFBundleVersion": VERSION,
        "CFBundleShortVersionString": VERSION, "CFBundleExecutable": "Stacker",
        "CFBundlePackageType": "APPL", "NSHighResolutionCapable": True,
        "LSMinimumSystemVersion": "12.0"
    }
    with (publish.parent / "Info.plist").open("wb") as file:
        plistlib.dump(info, file)
shutil.copy2(ROOT / "README.md", output / "README.md")
shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", output / "THIRD_PARTY_NOTICES.md")
archive_base = ROOT / "artifacts" / f"Stacker-{VERSION}-{args.rid}"
if not is_windows:
    # Preserve executable permissions and symlinks for .app bundles.
    archive = str(archive_base) + ".tar.gz"
    with tarfile.open(archive, "w:gz") as tar:
        for child in output.iterdir():
            tar.add(child, arcname=child.name)
else:
    archive = shutil.make_archive(str(archive_base), "zip", output)
print(f"Package: {archive}")
