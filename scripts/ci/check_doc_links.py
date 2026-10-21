#!/usr/bin/env python3
"""Check that relative links in tracked Markdown files point to files that exist.

External links (http, https, mailto) are not fetched, so the result is deterministic.
Links inside fenced code blocks and inline code are ignored. Anchors (#section) are not
verified; only the file part of the link is.

Usage:
    python scripts/ci/check_doc_links.py            # tracked *.md files
    python scripts/ci/check_doc_links.py --root DIR # every *.md under DIR (used by tests)
Exit code: 0 all links resolve, 1 broken links found.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

LINK = re.compile(r"(?<!\!)\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
IMAGE = re.compile(r"\!\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
FENCE = re.compile(r"^\s*(```|~~~)")
INLINE_CODE = re.compile(r"`[^`\n]*`")
EXTERNAL = re.compile(r"^(?:[a-z][a-z0-9+.-]*:|//)", re.I)


def links_in(text: str):
    """Yield (line_number, target) for links outside code."""
    in_fence = False
    for number, line in enumerate(text.splitlines(), start=1):
        if FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        clean = INLINE_CODE.sub("", line)
        for rx in (LINK, IMAGE):
            for m in rx.finditer(clean):
                yield number, m.group(1)


def check_file(path: Path, root: Path) -> list[tuple[str, int, str]]:
    broken = []
    text = path.read_text(encoding="utf-8", errors="replace")
    for number, target in links_in(text):
        if EXTERNAL.match(target) or target.startswith("#"):
            continue
        file_part = unquote(re.split(r"[#?]", target, maxsplit=1)[0])
        if not file_part:
            continue
        base = root if file_part.startswith("/") else path.parent
        resolved = (base / file_part.lstrip("/")).resolve()
        if not resolved.exists():
            broken.append((path.relative_to(root).as_posix(), number, target))
    return broken


def markdown_files(root: Path, use_git: bool) -> list[Path]:
    if use_git:
        out = subprocess.run(["git", "-C", str(root), "ls-files", "-z", "*.md"], capture_output=True, check=True).stdout
        return [root / p for p in out.decode("utf-8", errors="replace").split("\0") if p]
    return sorted(root.rglob("*.md"))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", help="check every *.md under this directory instead of git-tracked files")
    args = ap.parse_args(argv)
    if args.root:
        root = Path(args.root).resolve()
        files = markdown_files(root, use_git=False)
    else:
        root = Path(subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, check=True, text=True).stdout.strip())
        files = markdown_files(root, use_git=True)
    broken = []
    for path in files:
        if path.exists():
            broken.extend(check_file(path, root))
    if broken:
        print(f"Found {len(broken)} broken link(s):", file=sys.stderr)
        for rel, number, target in broken:
            print(f"  {rel}:{number}: {target}", file=sys.stderr)
        return 1
    print(f"Doc links OK: {len(files)} Markdown files checked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
