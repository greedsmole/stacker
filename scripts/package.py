#!/usr/bin/env python3
"""Build a self-contained Stacker archive. Requires Python 3 and .NET 10 SDK."""
import argparse
import pathlib
import plistlib
import shutil
import subprocess
import tarfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("rid", choices=["win-x64", "osx-arm64", "osx-x64", "linux-x64"])
args = parser.parse_args()
output = ROOT / "artifacts" / args.rid
if output.exists():
    shutil.rmtree(output)
output.mkdir(parents=True)
is_mac = args.rid.startswith("osx")
publish = output / "Stacker.app" / "Contents" / "MacOS" if is_mac else output / "Stacker"
subprocess.run([
    "dotnet", "publish", str(ROOT / "src/Stacker.Desktop"), "-c", "Release", "-r", args.rid,
    "--self-contained", "true", "-p:Version=0.2.1", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-o", str(publish)
], check=True, cwd=ROOT)
if is_mac:
    info = {
        "CFBundleName": "Stacker", "CFBundleDisplayName": "Stacker",
        "CFBundleIdentifier": "dev.stacker.desktop", "CFBundleVersion": "0.2.1",
        "CFBundleShortVersionString": "0.2.1", "CFBundleExecutable": "Stacker",
        "CFBundlePackageType": "APPL", "NSHighResolutionCapable": True,
        "LSMinimumSystemVersion": "12.0"
    }
    with (publish.parent / "Info.plist").open("wb") as file:
        plistlib.dump(info, file)
    (publish / "Stacker").chmod(0o755)
shutil.copy2(ROOT / "README.md", output / "README.md")
shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", output / "THIRD_PARTY_NOTICES.md")
archive_base = ROOT / "artifacts" / f"Stacker-0.2.1-{args.rid}"
if is_mac:
    # Preserve executable permissions and symlinks for .app bundles.
    archive = str(archive_base) + ".tar.gz"
    with tarfile.open(archive, "w:gz") as tar:
        for child in output.iterdir():
            tar.add(child, arcname=child.name)
elif args.rid == "linux-x64":
    archive = shutil.make_archive(str(archive_base), "gztar", output)
else:
    archive = shutil.make_archive(str(archive_base), "zip", output)
print(f"Package: {archive}")
