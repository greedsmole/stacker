#!/usr/bin/env python3
"""Build once, then run exhaustive, disjoint test groups in isolated processes."""
import argparse
import collections
import os
import pathlib
import re
import signal
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/Stacker.Tests/Stacker.Tests.csproj"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def split_filters(counts, ui_classes, workers):
    """Keep the UI serial per process; the complement also catches newly added tests."""
    if workers == 1:
        return [None]
    # UI cases cannot overlap within a process. Spread them as well as the long Git
    # scenarios across workers, including methods from the same test class.
    weights = {name: count * (3 if name.rsplit(".", 1)[0] in ui_classes else 1)
               for name, count in counts.items()}
    selected = sorted(counts, key=lambda name: (-weights[name], name))
    groups = [[] for _ in range(min(workers, len(selected)))]
    loads = [0] * len(groups)
    for name in selected:
        index = min(range(len(groups)), key=lambda i: loads[i])
        groups[index].append(name)
        loads[index] += weights[name]
    filters = ["|".join(f"FullyQualifiedName={name}" for name in group) for group in groups[:-1]]
    filters.append("&".join(f"FullyQualifiedName!={name}" for group in groups[:-1] for name in group) or None)
    return filters


def ui_classes():
    classes = set()
    for path in PROJECT.parent.glob("*.cs"):
        source = path.read_text(encoding="utf-8")
        match = re.search(r"\[Collection\(HeadlessApp\.CollectionName\)\]\s+public\s+sealed\s+(?:partial\s+)?class\s+(\w+)", source)
        if match:
            classes.add("Stacker.Tests." + match[1])
    return classes


def kill_tree(process):
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15, check=False)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass  # The child exited between poll() and killpg().
    process.wait(timeout=15)


def capture(command, timeout):
    process = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8", errors="replace", start_new_session=os.name != "nt")
    try:
        output, _ = process.communicate(timeout=timeout)
        if process.returncode:
            raise RuntimeError(output)
        return output
    finally:
        kill_tree(process)


def read_results(path):
    results = []
    for result in ET.parse(path).findall(".//t:UnitTestResult", NS):
        hours, minutes, seconds = result.get("duration", "0:0:0").split(":")
        duration = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        results.append((duration, result.attrib["testName"], result.attrib["outcome"]))
    return results


def run_groups(base, filters, directory, timeout):
    running = []
    results = []
    failed = False
    try:
        for index, test_filter in enumerate(filters, 1):
            folder = directory / f"group-{index}"
            folder.mkdir(parents=True, exist_ok=True)
            # A failed or empty invocation must never reuse an earlier successful result.
            trx = folder / "results.trx"
            trx.unlink(missing_ok=True)
            log = (folder / "console.log").open("w", encoding="utf-8")
            command = [*base, "--results-directory", str(folder), "--logger", "trx;LogFileName=results.trx",
                       "--logger", "console;verbosity=minimal", "--blame-hang-timeout", "2m", "--blame-hang-dump-type", "mini"]
            if test_filter:
                # RunSettings avoids Windows' command-line length limit as the suite grows.
                settings = ET.Element("RunSettings")
                configuration = ET.SubElement(settings, "RunConfiguration")
                ET.SubElement(configuration, "TestCaseFilter").text = test_filter
                settings_path = folder / "filter.runsettings"
                ET.ElementTree(settings).write(settings_path, encoding="utf-8", xml_declaration=True)
                command += ["--settings", str(settings_path)]
            print(f"Starting group {index}/{len(filters)}", flush=True)
            try:
                process = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT,
                                           start_new_session=os.name != "nt")
            except BaseException:
                log.close()
                raise
            running.append((process, time.monotonic(), folder, log))
        while running:
            for item in running[:]:
                process, started, folder, log = item
                elapsed = time.monotonic() - started
                expired = elapsed >= timeout and process.poll() is None
                if expired:
                    kill_tree(process)
                code = process.poll()
                if code is None:
                    continue
                log.close()
                print((folder / "console.log").read_text(encoding="utf-8", errors="replace"), flush=True)
                print(f"{folder.name}: {'TIMED OUT' if expired else 'exit ' + str(code)} in {elapsed:.1f}s", flush=True)
                trx = folder / "results.trx"
                group_results = read_results(trx) if trx.exists() else []
                failed |= expired or code != 0 or not group_results
                results.extend(group_results)
                running.remove(item)
            if running:
                time.sleep(0.1)
    finally:
        for process, _, _, log in running:
            kill_tree(process)
            log.close()
    return failed, results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-build", action="store_true", help="Use an existing Release build")
    parser.add_argument("--workers", type=int, default=3, choices=range(1, 9), metavar="1-8")
    parser.add_argument("--filter", help="Run only matching tests in one process, without discovery")
    parser.add_argument("--timeout", type=int, default=300, help="Wall-clock limit per group in seconds")
    parser.add_argument("--results-directory", type=pathlib.Path, default=ROOT / "TestResults")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    started = time.monotonic()
    if not args.no_build:
        print(capture(["dotnet", "build", str(PROJECT), "-c", "Release", "--nologo"], 180), flush=True)
    base = ["dotnet", "test", str(PROJECT), "-c", "Release", "--no-build", "--no-restore"]
    expected = None
    if args.filter:
        filters = [args.filter]
    else:
        listing = capture([*base, "--list-tests", "--verbosity", "quiet"], 30)
        names = re.findall(r"^\s+(Stacker\.Tests\.\w+\.\w+)(?:\(|\s*$)", listing, re.MULTILINE)
        if not names:
            raise RuntimeError("No tests discovered.\n" + listing)
        expected = collections.Counter(names)
        filters = split_filters(expected, ui_classes(), args.workers)
        print(f"Discovered {sum(expected.values())} tests; running {len(filters)} isolated groups.", flush=True)
    directory = args.results_directory.resolve()
    failed, results = run_groups(base, filters, directory, args.timeout)
    outcomes = collections.Counter(outcome for _, _, outcome in results)
    failed |= any(outcome != "Passed" for outcome in outcomes)
    actual = collections.Counter(name.split("(", 1)[0] for _, name, _ in results)
    if expected is not None and actual != expected:
        print(f"ERROR: Results do not match discovery. Missing: {dict(expected - actual)}; extra: {dict(actual - expected)}", flush=True)
        failed = True
    report = [f"{'FAILED' if failed else 'PASSED'}: {len(results)} tests; {outcomes['Passed']} passed; wall time: {time.monotonic() - started:.1f}s",
              "", "Slowest tests:"]
    report += [f"- {seconds:.2f}s: {name}" for seconds, name, _ in sorted(results, reverse=True)[:10]]
    print("\n".join(report), flush=True)
    (directory / "summary.md").write_text("\n".join(report) + "\n", encoding="utf-8")
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as file:
            file.write("\n".join(report) + "\n")
    return int(failed)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, subprocess.TimeoutExpired, OSError, ET.ParseError) as error:
        print(str(error), flush=True)
        raise SystemExit(1)
