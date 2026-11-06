import importlib.util
import json
import pathlib
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location(
    "album_art", pathlib.Path(__file__).resolve().parent.parent / "album-art.py")
mod = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mod)


class Licence(unittest.TestCase):
    def test_cc_by_cc0_and_public_domain_pass(self):
        for ok in ["CC BY 4.0", "CC BY 2.0", "CC BY", "CC0", "CC0 1.0", "Public domain"]:
            self.assertTrue(mod.licence_ok(ok), ok)

    def test_share_alike_non_commercial_no_derivatives_and_unknown_fail(self):
        for bad in ["CC BY-SA 4.0", "CC BY-NC 2.0", "CC BY-ND 3.0", "GFDL", "", None]:
            self.assertFalse(mod.licence_ok(bad), bad)


class Naming(unittest.TestCase):
    def test_art_names_sort_in_book_order(self):
        self.assertEqual(mod.art_name("M31"), "m031.webp")
        self.assertEqual(mod.art_name("C9"), "c009.webp")
        with self.assertRaises(ValueError):
            mod.art_name("NGC224")

    def test_every_object_once_in_book_order(self):
        ids = mod.expected_ids()
        self.assertEqual(len(ids), 219)
        self.assertEqual((ids[0], ids[109], ids[110], ids[-1]), ("M1", "M110", "C1", "C109"))


class Upright(unittest.TestCase):
    def test_a_landscape_source_turns_upright_unless_the_row_says_otherwise(self):
        self.assertTrue(mod.wants_upright(1600, 900, None))
        self.assertFalse(mod.wants_upright(1000, 1000, None))
        self.assertFalse(mod.wants_upright(1600, 900, False))
        self.assertTrue(mod.wants_upright(900, 1600, True))


class Check(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = pathlib.Path(self.tmp.name)
        self._art, self._manifest = mod.ART, mod.MANIFEST
        mod.ART, mod.MANIFEST = root / "art", root / "manifest.json"
        self.addCleanup(lambda: (setattr(mod, "ART", self._art), setattr(mod, "MANIFEST", self._manifest)))
        mod.ART.mkdir()

    def rows(self):
        return [{"id": i, "art": None} for i in mod.expected_ids()]

    def test_placeholders_only_is_sound(self):
        self.assertEqual(mod.check(self.rows()), [])

    def test_art_needs_its_file_an_allowed_licence_a_credit_and_a_source(self):
        rows = self.rows()
        rows[30].update(art="m031.webp", licence="CC BY-SA 4.0")
        problems = mod.check(rows)
        self.assertTrue(any("m031.webp is missing" in p for p in problems))
        self.assertTrue(any("licence" in p for p in problems))
        self.assertTrue(any("credit and source" in p for p in problems))

    def test_an_unreferenced_file_and_a_wrong_order_are_problems(self):
        (mod.ART / "c001.webp").write_bytes(b"x")
        rows = self.rows()
        rows[0], rows[1] = rows[1], rows[0]
        problems = mod.check(rows)
        self.assertIn("c001.webp: no manifest row points at it", problems)
        self.assertIn("ids are not M1..M110 then C1..C109 in order", problems)

    def test_the_manifest_round_trips(self):
        mod.save(self.rows())
        self.assertEqual(mod.load(), self.rows())
        self.assertTrue(mod.MANIFEST.read_text().endswith("\n"))
        json.loads(mod.MANIFEST.read_text())


if __name__ == "__main__":
    unittest.main()
