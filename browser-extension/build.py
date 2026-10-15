"""Sync the canonical page-free extension source; never generate old UI assets."""
from pathlib import Path
import argparse
import json
import re

root = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--check", action="store_true")
parser.add_argument("--version")
args = parser.parse_args()
if args.version and not re.fullmatch(r"[0-9]+(?:\.[0-9]+){2,3}", args.version):
    parser.error("Invalid extension version")
shared = root / "shared"
files = ["background.js", "intercept.js", "_locales/en/messages.json", "_locales/fa/messages.json"]
files += [str(path.relative_to(shared)).replace("\\", "/") for path in sorted((shared / "icons").glob("*.png"))]
retired = ["capture.html", "capture.js", "popup.html", "popup.js", "popup.css"]
versions = set()
for browser in ["chromium", "firefox"]:
    target = root / browser
    manifest_path = target / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if args.version:
        manifest["version"] = args.version
        if not args.check:
            manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    versions.add(manifest["version"])
    assert "default_popup" not in manifest["action"]
    assert not any(key in manifest for key in ["options_ui", "options_page", "web_accessible_resources"])
    assert "contextMenus" not in manifest["permissions"]
    assert "downloads" not in manifest["permissions"]
    assert manifest["host_permissions"] == ["http://*/*", "https://*/*"]
    assert manifest["optional_permissions"] == ["cookies"]
    for name in files:
        source = shared / name
        destination = target / name
        if args.check:
            assert source.read_bytes() == destination.read_bytes(), f"Out of sync: {destination}"
        else:
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(source.read_bytes())
    for name in retired:
        destination = target / name
        if args.check:
            assert not destination.exists(), f"Obsolete page remains: {destination}"
        else:
            destination.unlink(missing_ok=True)
assert len(versions) == 1, "Browser versions differ"
print("PASS: synchronized page-free Chrome/Firefox extension " + next(iter(versions)))
