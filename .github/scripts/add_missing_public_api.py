#!/usr/bin/env python3
"""Adds every symbol RS0016 flags as missing to the right PublicAPI.Unshipped.txt.

`dotnet format analyzers --diagnostics RS0016` does NOT do this: that code fix edits an
AdditionalFile (the .txt), not a .cs document, and dotnet-format's CLI fixer only applies
fixes that land in source documents. So this replicates it: rebuild the solution with the
warning-as-error gate relaxed, parse RS0016's message (locale-agnostic: it always quotes the
symbol right after the diagnostic id), and append it to the Unshipped.txt of the project named
in the diagnostic's trailing `[...csproj]` — not the project that was built, since building one
project also compiles (and reports for) everything it references.

Run this locally whenever `dotnet build` fails on RS0016 for a member you meant to add.
"""

import glob
import os
import re
import subprocess
import sys

HEADER = "#nullable enable"
RS0016_RE = re.compile(r"RS0016[^']*'([^']+)'.*\[([^\]]+\.csproj)\]\s*$")


def discover_tracked_projects():
    """Returns each project dir that tracks a public API surface."""
    return sorted(
        os.path.dirname(os.path.dirname(p.replace("\\", "/")))
        for p in glob.glob("src/*/PublicAPI/PublicAPI.Unshipped.txt")
    )


def _key(path):
    return os.path.normcase(os.path.abspath(path))


def route_missing_symbols(build_output, projects):
    """Maps each tracked project dir to the RS0016 symbols its own compilation reported."""
    by_csproj = {_key(glob.glob(f"{p}/*.csproj")[0]): p for p in projects}
    found = {}
    for line in build_output.splitlines():
        m = RS0016_RE.search(line)
        if m:
            project_dir = by_csproj.get(_key(m.group(2)))
            if project_dir is not None:
                found.setdefault(project_dir, set()).add(m.group(1))
    return found


def read_entries(path):
    if not os.path.exists(path):
        return set()
    with open(path, encoding="utf-8") as f:
        return {line.strip() for line in f if line.strip() and line.strip() != HEADER}


def write_entries(path, entries):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n")
        for entry in sorted(entries):
            f.write(entry + "\n")


def main():
    projects = discover_tracked_projects()
    if not projects:
        print("No src/*/PublicAPI/PublicAPI.Unshipped.txt files found — nothing to track.")
        return 1

    result = subprocess.run(
        [
            "dotnet", "build", "QueryCache.slnx", "--configuration", "Release", "--nologo",
            "--no-incremental",
            "-p:TreatWarningsAsErrors=false", "-p:CodeAnalysisTreatWarningsAsErrors=false",
        ],
        capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
    )
    found = route_missing_symbols(result.stdout, projects)
    for project_dir, symbols in sorted(found.items()):
        path = f"{project_dir}/PublicAPI/PublicAPI.Unshipped.txt"
        write_entries(path, read_entries(path) | symbols)
        print(f"{path}: added {len(symbols)} entries.")

    if not found:
        print("Nothing missing — build already passes RS0016 clean.")
    return 0 if found else 1


if __name__ == "__main__":
    sys.exit(main())
