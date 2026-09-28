#!/usr/bin/env python3
"""Moves every entry out of each PublicAPI.Unshipped.txt into its PublicAPI.Shipped.txt.

Run after a release ships: everything that was "unshipped" at release time is now part of
the public API contract. Operates on every src/*/PublicAPI/ folder.

Exit codes: 0 = promoted something, 2 = nothing needed promoting (not an error — the caller
skips the rest of the job), 1 = an actual failure (an uncaught exception, same as Python's
default for any unhandled error). 2 is deliberately not 1 - an uncaught exception used
to exit 1 exactly like the intentional "nothing to promote" case, so release.yml's `if
python3 ...; then changed=true; else changed=false; fi` silently treated a crash (missing
file, permission error) the same as a no-op success, disarming the next release's
breaking-change detection instead of failing the job.
"""

import glob
import os
import sys

HEADER = "#nullable enable"


def read_entries(path):
    with open(path, encoding="utf-8") as f:
        return [line.strip() for line in f if line.strip() and line.strip() != HEADER]


def write_entries(path, entries):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n")
        for entry in sorted(entries):
            f.write(entry + "\n")


REMOVED_PREFIX = "*REMOVED*"


def promote_one(unshipped_path, shipped_path):
    """Applies unshipped_path's entries onto shipped_path and clears unshipped_path.

    A plain entry is added to Shipped. A `*REMOVED*Foo` entry deletes the plain `Foo` entry
    from Shipped (an API removal is meaningful only for one release cycle) and is itself
    discarded rather than copied - RS0024 forbids a *REMOVED* line inside Shipped.txt, since
    Shipped is the current API surface, not a removal history.

    Returns the number of entries read from unshipped_path (0 if there was nothing to do).
    """
    new_entries = read_entries(unshipped_path)
    if not new_entries:
        return 0

    shipped_entries = set(read_entries(shipped_path)) if os.path.exists(shipped_path) else set()
    for entry in new_entries:
        if entry.startswith(REMOVED_PREFIX):
            shipped_entries.discard(entry[len(REMOVED_PREFIX):])
        else:
            shipped_entries.add(entry)

    write_entries(shipped_path, shipped_entries)
    write_entries(unshipped_path, [])
    return len(new_entries)


def main():
    unshipped_files = sorted(glob.glob("src/*/PublicAPI/PublicAPI.Unshipped.txt"))
    if not unshipped_files:
        print("No src/*/PublicAPI/PublicAPI.Unshipped.txt files found.")
        return 2

    promoted_any = False
    for unshipped_path in unshipped_files:
        shipped_path = unshipped_path.replace("Unshipped.txt", "Shipped.txt")
        count = promote_one(unshipped_path, shipped_path)
        if count == 0:
            print(f"{unshipped_path}: nothing to promote.")
            continue
        print(f"{unshipped_path}: promoted {count} entries to {shipped_path}.")
        promoted_any = True

    return 0 if promoted_any else 2


if __name__ == "__main__":
    sys.exit(main())
