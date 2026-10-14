#!/usr/bin/env python3
"""Build the plugin ZIP and a Jellyfin repository manifest from the actual bytes."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/Jellyfin.Plugin.Oidc/Jellyfin.Plugin.Oidc.csproj"
VERSION = ET.parse(PROJECT).findtext("./PropertyGroup/Version")
if not VERSION:
    raise SystemExit("The plugin project must declare its assembly version.")
subprocess.run(
    [os.environ.get("DOTNET_COMMAND", "dotnet"), "build", str(PROJECT), "-c", "Release", "--nologo"],
    check=True, cwd=ROOT,
)
output = ROOT / "artifacts"
output.mkdir(exist_ok=True)
archive = output / f"jellyfin-oidc_{VERSION}.zip"
with ZipFile(archive, "w", compression=ZIP_DEFLATED) as package:
    for source, name in [
        (PROJECT.parent / "bin/Release/net10.0/Jellyfin.Plugin.Oidc.dll", "Jellyfin.Plugin.Oidc.dll"),
        (ROOT / "LICENSE", "LICENSE"),
    ]:
        entry = ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
        entry.compress_type = ZIP_DEFLATED
        entry.external_attr = 0o644 << 16
        package.writestr(entry, source.read_bytes())
manifest = [{
    "guid": "b51a78b4-74b0-4e18-9f69-8e7f79203856",
    "name": "OpenID Connect",
    "description": "Phase 0 compatibility spike for Jellyfin 12. Mock login is disabled by default. For UAT use only.",
    "overview": "Native session and Quick Connect compatibility spike",
    "owner": "lissosia",
    "category": "Authentication",
    "versions": [{
        "version": VERSION,
        "changelog": "Initial installable Phase 0 compatibility spike.",
        "targetAbi": "12.2.0.0",
        "sourceUrl": f"https://github.com/lissosia/jellyfin-oidc-plugin/releases/download/v{VERSION}/{archive.name}",
        "checksum": hashlib.md5(archive.read_bytes()).hexdigest(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }],
}]
(ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"Package: {archive}")
print("Manifest: manifest.json (checksum calculated from package bytes)")
