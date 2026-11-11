#!/usr/bin/env python3
"""Knowledge-base linter for MetroDom.

Mechanically enforces the docs conventions described in AGENTS.md:
  1. Every relative Markdown link resolves to an existing file.
  2. AGENTS.md stays short (it's a table of contents, not a manual).
  3. No orphans: every Markdown file under docs/ is linked from some other Markdown file.
  4. Every ADR (docs/design-docs/NNNN-*.md) declares a Status and is listed in the index.
  5. Every active exec plan declares Status and Last updated.

Stdlib only. Exit code 1 on any failure.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
AGENTS_MAX_LINES = 100
SKIP_DIRS = {".git", "bin", "obj", "packages", "node_modules"}
LINK_RE = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
CODE_FENCE_RE = re.compile(r"```.*?```", re.DOTALL)
INLINE_CODE_RE = re.compile(r"`[^`\n]*`")


def md_files() -> list[Path]:
    return sorted(
        p for p in ROOT.rglob("*.md")
        if not any(part in SKIP_DIRS for part in p.relative_to(ROOT).parts)
    )


def links_in(path: Path) -> list[str]:
    text = CODE_FENCE_RE.sub("", path.read_text(encoding="utf-8"))
    text = INLINE_CODE_RE.sub("", text)
    return LINK_RE.findall(text)


def resolve(src: Path, target: str) -> Path | None:
    """Return the local path a link points to, or None for external/anchor-only links."""
    if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I) or target.startswith("#"):
        return None
    target = target.split("#", 1)[0]
    if target.startswith("/"):
        dest = ROOT / target.lstrip("/")
    else:
        dest = (src.parent / target).resolve()
    if dest.is_dir() and (dest / "README.md").exists():
        dest = dest / "README.md"
    return dest


def main() -> int:
    errors: list[str] = []
    files = md_files()
    inbound: dict[Path, int] = {f.resolve(): 0 for f in files}

    for f in files:
        rel = f.relative_to(ROOT)
        for target in links_in(f):
            dest = resolve(f, target)
            if dest is None:
                continue
            if not dest.exists():
                errors.append(f"{rel}: broken link -> {target}")
                continue
            dest = dest.resolve()
            if dest in inbound and dest != f.resolve():
                inbound[dest] += 1

    agents = ROOT / "AGENTS.md"
    if not agents.exists():
        errors.append("AGENTS.md is missing")
    else:
        n = len(agents.read_text(encoding="utf-8").splitlines())
        if n > AGENTS_MAX_LINES:
            errors.append(
                f"AGENTS.md has {n} lines (max {AGENTS_MAX_LINES}); move detail into docs/"
            )

    docs = ROOT / "docs"
    for f in files:
        if docs in f.parents and inbound[f.resolve()] == 0:
            errors.append(f"{f.relative_to(ROOT)}: orphan (no Markdown file links to it)")

    adr_dir = docs / "design-docs"
    index = adr_dir / "index.md"
    index_text = index.read_text(encoding="utf-8") if index.exists() else ""
    for adr in sorted(adr_dir.glob("[0-9][0-9][0-9][0-9]-*.md")):
        text = adr.read_text(encoding="utf-8")
        if not re.search(r"^Status:", text, re.M):
            errors.append(f"{adr.relative_to(ROOT)}: missing 'Status:' line")
        if f"({adr.name})" not in index_text:
            errors.append(f"{adr.relative_to(ROOT)}: not listed in design-docs/index.md")

    for plan in sorted((docs / "exec-plans" / "active").glob("*.md")):
        text = plan.read_text(encoding="utf-8")
        for field in ("Status:", "Last updated:"):
            if field not in text:
                errors.append(f"{plan.relative_to(ROOT)}: missing '{field}'")

    if errors:
        print(f"check_docs: {len(errors)} problem(s)")
        for e in errors:
            print(f"  - {e}")
        return 1
    print(f"check_docs: OK ({len(files)} Markdown files)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
