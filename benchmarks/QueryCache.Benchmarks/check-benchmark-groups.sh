#!/usr/bin/env bash
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
groups_file="$here/benchmark-groups.json"
project="$here/QueryCache.Benchmarks.csproj"

filters="$(python3 -c '
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    for group in json.load(f):
        for pattern in group["filter"].split():
            print(pattern)
' "$groups_file" | tr -d '\r')"

ids="$(dotnet run --project "$project" -c Release --no-build -- --list flat |
    tr -d '\r' | grep '^QueryCache\.Benchmarks\.')"

regexes=()
while IFS= read -r filter; do
    escaped=${filter//./\\.}
    regexes+=("^${escaped//\*/.*}$")
done <<<"$filters"

missing=()
while IFS= read -r id; do
    matched=0
    for regex in "${regexes[@]}"; do
        if [[ $id =~ $regex ]]; then
            matched=1
            break
        fi
    done
    ((matched)) || missing+=("$id")
done <<<"$ids"

if ((${#missing[@]})); then
    echo "Benchmarks matched by no group in benchmark-groups.json:" >&2
    printf '  %s\n' "${missing[@]}" >&2
    echo "Add them to a group, or add a new group." >&2
    exit 1
fi

echo "Every benchmark is covered by a group."
