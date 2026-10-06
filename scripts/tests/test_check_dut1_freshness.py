import datetime as dt
import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "check_dut1_freshness", pathlib.Path(__file__).resolve().parent.parent / "check-dut1-freshness.py")
mod = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mod)

HEADER = "# IERS Bulletin A UT1-UTC snapshot\n# span=2024-01-01..2027-10-02 (MJD 60310..61680)\n61680\t-0.1478001\tP\n"


class SpanEnd(unittest.TestCase):
    def test_reads_the_span_header(self):
        self.assertEqual(mod.span_end(HEADER), dt.date(2027, 10, 2))

    def test_missing_header_is_none(self):
        self.assertIsNone(mod.span_end("# bulletin=x\n61680\t-0.1\tP\n"))


class Verdict(unittest.TestCase):
    END = dt.date(2027, 10, 2)

    def test_far_from_the_end_is_a_notice(self):
        level, _ = mod.verdict(self.END, dt.date(2026, 10, 5), 90)
        self.assertEqual(level, "notice")

    def test_within_the_window_warns(self):
        level, msg = mod.verdict(self.END, dt.date(2027, 8, 1), 90)
        self.assertEqual(level, "warning")
        self.assertIn("ends in 62 day(s)", msg)

    def test_past_the_end_warns(self):
        level, msg = mod.verdict(self.END, dt.date(2027, 11, 1), 90)
        self.assertEqual(level, "warning")
        self.assertIn("ended 30 day(s) ago", msg)

    def test_no_header_warns(self):
        level, _ = mod.verdict(None, dt.date(2026, 10, 5), 90)
        self.assertEqual(level, "warning")


class Main(unittest.TestCase):
    def test_exit_code_is_always_zero(self):
        self.assertEqual(mod.main(["--table", "/nonexistent/iers.tsv"]), 0)


if __name__ == "__main__":
    unittest.main()
