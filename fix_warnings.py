"""
Bulk-fix the mechanical analyzer warnings introduced by the SonarAnalyzer 10.32 upgrade.

Handles two rules, both of which have a single unambiguous edit:

  S8969  Redundant null-forgiving operator - delete the '!'.
  S8949  Cancellation token not passed - add an explicit 'CancellationToken.None'.

Everything else is reported and left alone. In particular this does NOT touch xUnit1069:
that rule wants a cancellation token threaded into whichever awaited calls accept one, which
needs the compiler's view of each call's signature. A regex cannot tell 'StartAsync()' that
takes a token from one that does not, and guessing wrong across 471 test methods is how you
get a suite that compiles and silently stops asserting.

Usage:
    python fix_warnings.py --repo C:/repos/PeerSharp [--apply]

Without --apply it prints what it would do and changes nothing.
"""

import argparse
import io
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

SEP = chr(92)
WARN_RE = re.compile(
    r"([A-Za-z]:" + re.escape(SEP) + r"[^(]+\.cs)\((\d+),(\d+)\): warning (S\d+): (.*?)(?: \[|$)"
)


def collect_warnings(repo):
    """Build the solution and return deduplicated (file, line, col, rule, message) tuples.

    The solution build reports each warning twice (once per project pass), hence the dedupe.
    --no-incremental matters: analyzers only run on a real compile, so an up-to-date build
    reports nothing and the script would cheerfully claim success.
    """
    proc = subprocess.run(
        ["dotnet", "build", "PeerSharp.slnx", "-c", "Release", "--no-incremental", "-v", "n"],
        cwd=repo, capture_output=True, text=True, errors="replace",
    )
    seen, out = set(), []
    for line in proc.stdout.splitlines():
        m = WARN_RE.search(line)
        if not m:
            continue
        key = (m.group(1), int(m.group(2)), int(m.group(3)), m.group(4))
        if key in seen:
            continue
        seen.add(key)
        out.append(key + (m.group(5),))
    return out


def close_paren(text, open_idx):
    """Index of the ')' matching the '(' at open_idx, ignoring parens inside string literals."""
    depth, i, n = 0, open_idx, len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == SEP else 1
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def fix_s8969(line, col):
    """Delete the redundant '!'. The reported column lands on or just after it."""
    for probe in (col - 1, col - 2, col, col - 3):
        if 0 <= probe < len(line) and line[probe] == "!":
            # Never touch '!=' or a prefix '!' negation.
            if probe + 1 < len(line) and line[probe + 1] == "=":
                continue
            return line[:probe] + line[probe + 1:]
    return None


def fix_s8949(line):
    """Add an explicit CancellationToken.None to the call the analyzer flagged.

    Two shapes cover the shutdown paths this fires on:
      a) '.WaitAsync(TimeSpan...)'      -> add the token as a second argument
      b) 'await Something(...).ConfigureAwait(false)' -> add it to Something's argument list
    Anything else returns None and is left for a human, because the insertion point is not
    on this line (a multi-line lambda, say) or the right answer might be a real token.
    """
    # (a) Task.WaitAsync(timeout) -> Task.WaitAsync(timeout, CancellationToken.None)
    m = re.search(r"\.WaitAsync\(", line)
    if m:
        open_idx = m.end() - 1
        close_idx = close_paren(line, open_idx)
        if close_idx > 0 and "CancellationToken" not in line[open_idx:close_idx]:
            return line[:close_idx] + ", CancellationToken.None" + line[close_idx:]

    # (b) await Call(args).ConfigureAwait(false)
    ca = line.find(".ConfigureAwait(")
    if ca > 0:
        # Walk back to the ')' that ends the awaited call's own argument list.
        j = ca - 1
        if j >= 0 and line[j] == ")":
            # Find its matching '(' by scanning backwards.
            depth = 0
            k = j
            while k >= 0:
                if line[k] == ")":
                    depth += 1
                elif line[k] == "(":
                    depth -= 1
                    if depth == 0:
                        break
                k -= 1
            if k >= 0 and "CancellationToken" not in line[k:j]:
                inner = line[k + 1:j].strip()
                sep = "" if not inner else ", "
                return line[:j] + sep + "CancellationToken.None" + line[j:]
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=".")
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    warnings = collect_warnings(args.repo)
    by_rule = Counter(w[3] for w in warnings)
    print(f"{len(warnings)} distinct warnings: {dict(by_rule)}\n")

    # Group by file, and apply edits bottom-up and right-to-left so earlier edits never
    # invalidate the positions of later ones. One line here carries both an S8969 and an
    # S8949 fix, which is exactly the case that ordering protects.
    per_file = defaultdict(list)
    for f, ln, col, rule, msg in warnings:
        if rule in ("S8969", "S8949"):
            per_file[f].append((ln, col, rule, msg))

    fixed, manual = 0, []
    for path, items in sorted(per_file.items()):
        src = io.open(path, encoding="utf-8-sig", errors="replace").read().split("\n")
        for ln, col, rule, msg in sorted(items, key=lambda t: (-t[0], -t[1])):
            original = src[ln - 1]
            new = fix_s8969(original, col) if rule == "S8969" else fix_s8949(original)
            if new is None or new == original:
                manual.append((path, ln, rule, original.strip()))
                continue
            src[ln - 1] = new
            fixed += 1
            print(f"  {os.path.basename(path)}:{ln} [{rule}]")
            print(f"    - {original.strip()[:96]}")
            print(f"    + {new.strip()[:96]}")
        if args.apply:
            io.open(path, "w", encoding="utf-8-sig", newline="").write("\n".join(src))

    print(f"\n{fixed} fixed, {len(manual)} need a human:")
    for path, ln, rule, text in manual:
        print(f"  {os.path.basename(path)}:{ln} [{rule}] {text[:88]}")

    if not args.apply:
        print("\n(dry run - re-run with --apply to write the changes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
