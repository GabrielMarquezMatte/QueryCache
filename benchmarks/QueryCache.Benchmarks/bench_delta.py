#!/usr/bin/env python3
"""Renders the PR benchmark comment as a delta against the series published to GitHub Pages.

The PR job measures only the pull request's own build, so its absolute numbers carry no verdict on
their own: runner variance on a shared machine swamps most real changes. benchmark-pages.yml already
publishes a per-commit series for master, so a baseline costs one file read rather than a second
benchmark run.

Usage: bench_delta.py --results <dir of *-report-full.json> [--baseline <data.js>] [--threshold 10]
"""

from __future__ import annotations

import argparse
import glob
import json
import os


def load_baseline(path: str | None) -> tuple[str | None, dict[str, float]]:
    """The newest run's measurements, as (commit sha, {benchmark name: mean ns})."""
    if not path or not os.path.exists(path):
        return None, {}
    with open(path, encoding="utf-8") as handle:
        raw = handle.read()
    data = json.loads(raw[raw.index("{"):])
    runs = data.get("entries", {}).get("Benchmark", [])
    if not runs:
        return None, {}
    newest = runs[-1]
    measurements = {bench["name"]: bench["value"] for bench in newest.get("benches", [])}
    return newest.get("commit", {}).get("id"), measurements


def load_results(directory: str) -> tuple[dict[str, float], list[str]]:
    """(measured means, benchmarks that ran but produced no measurement). FullName matches the
    baseline's `name` verbatim. BenchmarkDotNet writes null Statistics when the benchmark process
    crashed or threw."""
    results: dict[str, float] = {}
    failed: list[str] = []
    for path in sorted(glob.glob(os.path.join(directory, "*-report-full.json"))):
        with open(path, encoding="utf-8") as handle:
            for bench in json.load(handle).get("Benchmarks", []):
                statistics = bench.get("Statistics")
                if statistics is None:
                    failed.append(bench["FullName"])
                else:
                    results[bench["FullName"]] = statistics["Mean"]
    return results, failed


def humanize(nanoseconds: float) -> str:
    for unit, scale in (("s", 1e9), ("ms", 1e6), ("µs", 1e3)):
        if nanoseconds >= scale:
            return f"{nanoseconds / scale:.2f} {unit}"
    return f"{nanoseconds:.0f} ns"


def render(baseline_sha, baseline, results, threshold, failed=()) -> str:
    lines = ["## Benchmark Results", ""]
    if failed:
        lines += ["### Failed to run", "", "These produced no measurement; check the job log for the crash or exception.", ""]
        lines += [f"- `{name}`" for name in failed]
        lines.append("")
    if not baseline:
        lines += [
            "_No published baseline was found, so these are absolute numbers only. They come from a "
            "shared `ubuntu-latest` runner and cannot be compared against another machine's._",
            "",
        ]
        return "\n".join(lines)

    lines += [
        f"_Baseline: master [`{baseline_sha[:8]}`](../commit/{baseline_sha}), from the series "
        "published by `benchmark-pages.yml`. Both sides ran on a shared `ubuntu-latest` runner, so "
        f"treat anything under ±{threshold:g}% as noise._",
        "",
    ]

    moved, steady, unmatched = [], 0, 0
    for name, current in results.items():
        before = baseline.get(name)
        if before is None:
            unmatched += 1
            continue
        if before == 0:
            continue
        percent = (current - before) / before * 100
        if abs(percent) >= threshold:
            moved.append((percent, name, before, current))
        else:
            steady += 1

    if moved:
        moved.sort(reverse=True)
        lines += [
            f"### Moved by at least {threshold:g}%",
            "",
            "| Benchmark | master | This PR | Delta |",
            "|---|---:|---:|---:|",
        ]
        lines += [
            f"| `{name}` | {humanize(before)} | {humanize(current)} | {percent:+.1f}% |"
            for percent, name, before, current in moved
        ]
        lines.append("")
    else:
        lines += [f"No benchmark moved by {threshold:g}% or more.", ""]

    summary = [f"{steady} within ±{threshold:g}%"]
    if unmatched:
        summary.append(f"{unmatched} with no baseline entry (new, renamed, or reparameterized)")
    lines += ["_" + ", ".join(summary) + "._", ""]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", required=True, help="Directory holding *-report-full.json")
    parser.add_argument("--baseline", help="gh-pages dev/bench/data.js; absent means no comparison")
    parser.add_argument("--threshold", type=float, default=10.0, help="Percent change worth reporting")
    args = parser.parse_args()

    baseline_sha, baseline = load_baseline(args.baseline)
    results, failed = load_results(args.results)
    print(render(baseline_sha, baseline, results, args.threshold, failed))


if __name__ == "__main__":
    main()
