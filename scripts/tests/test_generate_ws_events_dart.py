import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "generate_ws_events_dart", pathlib.Path(__file__).resolve().parent.parent / "generate-ws-events-dart.py")
mod = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mod)


class Render(unittest.TestCase):
    def test_names_become_lower_camel_constants(self):
        text = mod.render('{"events":[{"name":"GuiderDarkLibraryInvalidated","type":"guider.dark_library.invalidated"}]}')
        self.assertIn("static const String guiderDarkLibraryInvalidated = 'guider.dark_library.invalidated';", text)


class Drift(unittest.TestCase):
    def test_committed_dart_file_matches_the_snapshot(self):
        self.assertEqual(mod.main(["--check"]), 0, "run scripts/generate-ws-events-dart.py")


if __name__ == "__main__":
    unittest.main()
