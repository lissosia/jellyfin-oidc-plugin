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
    runtime = PROJECT.parent / "bin/Release/net10.0"
    included = [(runtime / "Jellyfin.Plugin.Oidc.dll", "Jellyfin.Plugin.Oidc.dll"), (ROOT / "LICENSE", "LICENSE")]
    included.extend((source, source.name) for source in sorted(runtime.glob("Microsoft.IdentityModel.*.dll")))
    included.extend((source, source.name) for source in sorted(runtime.glob("Microsoft.Bcl.Cryptography.dll")))
    jwt = runtime / "System.IdentityModel.Tokens.Jwt.dll"
    if jwt.exists():
        included.append((jwt, jwt.name))
    for source, name in included:
        if not source.exists():
            raise SystemExit(f"Required package input is missing: {source}")
        entry = ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
        entry.compress_type = ZIP_DEFLATED
        entry.external_attr = 0o644 << 16
        package.writestr(entry, source.read_bytes())
manifest = [{
    "guid": "b51a78b4-74b0-4e18-9f69-8e7f79203856",
    "name": "OpenID Connect",
    "description": "Multi-provider OpenID Connect sign-in for Jellyfin 12. Supports PKCE, guarded provisioning, mapped admin roles, and profile image import.",
    "overview": "Multi-provider OpenID Connect with native Jellyfin sessions",
    "owner": "lissosia",
    "category": "Authentication",
    "versions": [{
        "version": VERSION,
        "changelog": "Fix issuer URL normalization for root issuers and discovery URLs.",
        "targetAbi": "12.2.0.0",
        "sourceUrl": f"https://github.com/lissosia/jellyfin-oidc-plugin/releases/download/v{VERSION}/{archive.name}",
        "checksum": hashlib.md5(archive.read_bytes()).hexdigest(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }],
}]
existing_manifest = ROOT / "manifest.json"
if existing_manifest.exists():
    previous = json.loads(existing_manifest.read_text())
    if previous and previous[0].get("guid") == manifest[0]["guid"]:
        manifest[0]["versions"].extend(
            v for v in previous[0]["versions"] if v["version"] != VERSION
        )
(ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"Package: {archive}")
print("Manifest: manifest.json (checksum calculated from package bytes)")
