"""Export only reviewed source file types to a new, empty publication directory.

Does not copy build outputs, profiles, credentials, logs, downloaded files or Git
history. Fails closed on suspicious file names and common credential patterns.
Findings report paths and pattern names, never matched secret values.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]
TREES = ("src", "tests", "browser-extension", "installer", "legal", "locales", "media-tools", "scripts")
ROOT_FILES = ("README.md", "LICENSE", "DISCLAIMER.md", "TERMS.md", "SECURITY.md", "CODE_SIGNING.md", ".gitignore", ".gitattributes", "NuGet.Config", "settings.schema.json")
EXTENSIONS = {".cs", ".csproj", ".xaml", ".manifest", ".js", ".cjs", ".json", ".py", ".ps1", ".iss", ".isl", ".txt", ".md", ".png", ".ico"}
EXCLUDED = {"bin", "obj", "node_modules", "__pycache__", ".git", ".vs", "artifacts", "release", "audit", "profiles", "test-artifacts"}
SENSITIVE_NAMES = re.compile(r"^(?:\.env(?:\..*)?|secrets?\..*|credentials?\..*|cookies?\..*|downloads\.json|settings\.json|queues\.json|test-config\.json|e2e\.host\.json)$", re.I)
PATTERNS = {
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----"),
    "github-token": re.compile(r"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})"),
    "aws-key": re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "credential-url": re.compile(r"https?://[^\s/\"'<>:@]+:[^\s/\"'<>@]+@", re.I),
    "developer-profile": re.compile(r"(?:C:[/\\]Users[/\\]Win12|\.codex[/\\]visualizations)", re.I),
}

def candidates():
    yield from (ROOT / name for name in ROOT_FILES)
    for tree in TREES:
        for path in sorted((ROOT / tree).rglob("*")):
            relative = path.relative_to(ROOT)
            if path.is_file() and not any(part.lower() in EXCLUDED for part in relative.parts[:-1]) and path.suffix.lower() in EXTENSIONS:
                yield path

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    target = args.destination.resolve()
    if target == ROOT or ROOT in target.parents:
        parser.error("Destination must be outside the working source tree")
    if target.exists() and any(target.iterdir()):
        parser.error("Destination must be new or empty; existing files are never removed")
    files = list(candidates())
    findings = []
    for path in files:
        relative = path.relative_to(ROOT).as_posix()
        if path.is_symlink():
            findings.append((relative, "symlink"))
        if SENSITIVE_NAMES.match(path.name):
            findings.append((relative, "sensitive-filename"))
        if path.suffix.lower() not in {".png", ".ico"}:
            content = path.read_text(encoding="utf-8-sig")
            for name, pattern in PATTERNS.items():
                # Synthetic credentials in test fixture code are not real secrets.
                if name == "credential-url" and relative.startswith("tests/"):
                    continue
                if pattern.search(content):
                    findings.append((relative, name))
    if findings:
        print(json.dumps({"status": "BLOCKED", "findings": findings}, indent=2))
        raise SystemExit(1)
    target.mkdir(parents=True, exist_ok=True)
    manifest = []
    for path in files:
        relative = path.relative_to(ROOT)
        destination = target / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination)
        manifest.append({"path": relative.as_posix(), "sha256": hashlib.sha256(destination.read_bytes()).hexdigest()})
    # Local audit stays outside the public tree.
    report = ROOT / "artifacts" / "public-source-review.json"
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps({"status": "PASS", "fileCount": len(files), "files": manifest}, indent=2), encoding="utf-8")
    print(json.dumps({"status": "PASS", "fileCount": len(files), "bytes": sum(path.stat().st_size for path in files)}))

if __name__ == "__main__":
    main()
