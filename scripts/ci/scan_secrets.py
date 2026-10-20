#!/usr/bin/env python3
"""Secret scan for tracked files. No external service and no network.

Prints only file, line and rule name, never the matched value. A line that carries the
marker `ci-allow-secret` is skipped (use it only for fake values in tests or examples).

Usage:
    python scripts/ci/scan_secrets.py            # scan files tracked by git
    python scripts/ci/scan_secrets.py --root DIR # scan every file under DIR (used by tests)
Exit code: 0 clean, 1 findings.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ALLOW_MARKER = "ci-allow-secret"
MAX_BYTES = 1_000_000

# Placeholders that are clearly not secrets.
PLACEHOLDER = re.compile(
    r"(example|placeholder|changeme|change-me|your[-_ ]|<[^>]+>|\$\{|%\w+%|xxxx|\*\*\*|redacted|dummy|sample)",
    re.I,
)

RULES = [
    ("private-key-block", re.compile(r"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----")),
    ("aws-access-key-id", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("github-token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
    ("slack-token", re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
    (
        "connection-string-password",
        re.compile(r"(?i)\b(?:server|host|data source)\s*=[^;\n]+;[^\n]*\b(?:password|pwd)\s*=\s*[^;\s\"']{3,}"),
    ),
]
# name = "value" with a long literal value. Checked separately so placeholders can be excluded.
GENERIC = re.compile(
    r"""(?ix)
    \b(?P<name>[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token|credential)[A-Za-z0-9_.-]*)
    ["']?\s*[:=]\s*
    (?P<q>["'])(?P<val>[^"'\s]{8,})(?P=q)
    """
)
# The generic name=value rule is not applied to test code (it holds fake canary values on
# purpose) or to vendored third-party libraries. The specific rules (private keys, cloud
# and token formats, connection strings) still apply everywhere.
GENERIC_EXEMPT_PREFIXES = ("BC-LocalAI.Tests/", "tests/", "BC-LocalAI/wwwroot/lib/")
SKIP_SUFFIXES = {".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".dll", ".exe", ".pdb", ".woff", ".woff2", ".pyc"}


def tracked_files(root: Path) -> list[Path]:
    out = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"], capture_output=True, check=True
    ).stdout.decode("utf-8", errors="replace")
    return [root / p for p in out.split("\0") if p]


def walk_files(root: Path) -> list[Path]:
    return [p for p in root.rglob("*") if p.is_file()]


def scan_text(text: str, generic: bool = True):
    """Yield (line_number, rule) for each finding in the text."""
    for number, line in enumerate(text.splitlines(), start=1):
        if ALLOW_MARKER in line:
            continue
        for rule, rx in RULES:
            if rx.search(line):
                yield number, rule
        if not generic:
            continue
        for m in GENERIC.finditer(line):
            value = m.group("val")
            if PLACEHOLDER.search(value) or PLACEHOLDER.search(m.group("name")):
                continue
            if value.lower() == m.group("name").lower():
                continue  # a constant whose value is its own name
            # references to code, not literals
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*\(.*", value):
                continue
            yield number, "generic-secret-assignment"


def scan_files(files: list[Path], root: Path) -> list[tuple[str, int, str]]:
    findings = []
    for path in files:
        if path.suffix.lower() in SKIP_SUFFIXES:
            continue
        try:
            if path.stat().st_size > MAX_BYTES:
                continue
            data = path.read_bytes()
        except OSError:
            continue
        if b"\0" in data[:4096]:
            continue
        text = data.decode("utf-8", errors="replace")
        rel = path.relative_to(root).as_posix()
        generic = not rel.startswith(GENERIC_EXEMPT_PREFIXES)
        for number, rule in scan_text(text, generic):
            findings.append((rel, number, rule))
    return findings


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", help="scan every file under this directory instead of git-tracked files")
    args = ap.parse_args(argv)
    if args.root:
        root = Path(args.root).resolve()
        files = walk_files(root)
    else:
        root = Path(subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, check=True, text=True).stdout.strip())
        files = tracked_files(root)
    findings = scan_files(files, root)
    if findings:
        print(f"Secret scan found {len(findings)} possible secret(s) (values are not printed):", file=sys.stderr)
        for rel, number, rule in findings:
            print(f"  {rel}:{number}: {rule}", file=sys.stderr)
        print(f"Remove the value, or add '{ALLOW_MARKER}' on the line if it is a fake test value.", file=sys.stderr)
        return 1
    print(f"Secret scan clean: {len(files)} files checked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
