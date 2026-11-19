"""Exercise partition coverage and real child-process failure/timeout handling."""
import collections
import contextlib
import importlib.util
import io
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location("stacker_test_runner", pathlib.Path(__file__).resolve().parents[1] / "scripts/test.py")
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)

CHILD = """
import pathlib, sys, time
folder = pathlib.Path(sys.argv[sys.argv.index('--results-directory') + 1])
mode = sys.argv[1]
if mode == 'timeout':
    time.sleep(30)
if mode != 'empty':
    (folder / 'results.trx').write_text('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
    <Results><UnitTestResult testName="Example" outcome="Passed" duration="00:00:00.125" /></Results></TestRun>''')
sys.exit(7 if mode == 'fail' else 0)
"""


def matches(expression, name):
    if expression is None:
        return True
    def term(value):
        if "!=" in value:
            return name != value.split("!=", 1)[1]
        return name == value.split("=", 1)[1]
    return any(all(term(value) for value in branch.split("&")) for branch in expression.split("|"))


class TestPartitioning(unittest.TestCase):
    def test_every_theory_case_and_new_test_matches_exactly_one_group(self):
        counts = collections.Counter({"Stacker.Tests.Ui.A": 4, "Stacker.Tests.Ui.B": 2,
                                      "Stacker.Tests.Git.A": 1, "Stacker.Tests.Git.B": 1})
        for workers in (1, 2, 3, 8):
            with self.subTest(workers=workers):
                filters = RUNNER.split_filters(counts, {"Stacker.Tests.Ui"}, workers)
                for name in [*counts.elements(), "Stacker.Tests.NewClass.NewTest"]:
                    self.assertEqual(1, sum(matches(expression, name) for expression in filters), name)

    def test_discovery_order_does_not_change_the_partition(self):
        names = {f"Stacker.Tests.Example.Test{i}": 1 for i in range(10)}
        self.assertEqual(RUNNER.split_filters(names, set(), 3),
                         RUNNER.split_filters(dict(reversed(list(names.items()))), set(), 3))


class TestProcessHandling(unittest.TestCase):
    def run_child(self, mode, folder, timeout=10):
        with contextlib.redirect_stdout(io.StringIO()):
            return RUNNER.run_groups([sys.executable, "-c", CHILD, mode], [None], folder, timeout)

    def test_success_reads_results_from_the_child(self):
        with tempfile.TemporaryDirectory() as directory:
            failed, results = self.run_child("pass", pathlib.Path(directory))
        self.assertFalse(failed)
        self.assertEqual([(0.125, "Example", "Passed")], results)

    def test_nonzero_exit_fails_even_with_a_passing_trx(self):
        with tempfile.TemporaryDirectory() as directory:
            failed, results = self.run_child("fail", pathlib.Path(directory))
        self.assertTrue(failed)
        self.assertEqual(1, len(results))

    def test_empty_run_cannot_reuse_previous_success(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = pathlib.Path(directory)
            self.run_child("pass", folder)
            failed, results = self.run_child("empty", folder)
        self.assertTrue(failed)
        self.assertEqual([], results)

    def test_timeout_terminates_the_child_and_fails_the_group(self):
        with tempfile.TemporaryDirectory() as directory:
            failed, results = self.run_child("timeout", pathlib.Path(directory), timeout=0.2)
        self.assertTrue(failed)
        self.assertEqual([], results)


class TestResultCoverage(unittest.TestCase):
    def test_duplicate_results_cannot_hide_a_missing_method(self):
        listing = "    Stacker.Tests.Example.First\n    Stacker.Tests.Example.Second\n"
        results = [(0.1, "Stacker.Tests.Example.First", "Passed")] * 2
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()) as output:
            with mock.patch.object(sys, "argv", ["test.py", "--no-build", "--results-directory", directory]), \
                 mock.patch.object(RUNNER, "capture", return_value=listing), \
                 mock.patch.object(RUNNER, "run_groups", return_value=(False, results)):
                self.assertEqual(1, RUNNER.main())
            self.assertIn("Results do not match discovery", output.getvalue())


if __name__ == "__main__":
    unittest.main()
