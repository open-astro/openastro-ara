import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "generate_ws_events_dart", pathlib.Path(__file__).resolve().parent.parent / "generate-ws-events-dart.py")
mod = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mod)


class Render(unittest.TestCase):
    def test_names_become_lower_camel_constants(self):
        text = mod.render('{"events":[{"name":"GuiderStep","type":"guider.step"}]}')
        self.assertIn("static const String guiderStep = 'guider.step';", text)


    def test_a_declaration_past_80_columns_wraps_like_dart_format(self):
        text = mod.render('{"events":[{"name":"FilterWheelManualSwap","type":"equipment.filter_wheel.manual_swap"}]}')
        self.assertIn("static const String filterWheelManualSwap =\n      'equipment.filter_wheel.manual_swap';", text)


class Payload(unittest.TestCase):
    SNAP = ('{"events":[{"name":"CameraExposureFailed","type":"camera.exposure_failed","payload":['
            '{"field":"frame_id","type":"string","nullable":false},'
            '{"field":"exposure_sec","type":"number","nullable":true},'
            '{"field":"elapsed_ms","type":"integer","nullable":false}]}]}')

    def test_a_shaped_event_gets_a_typed_class_with_from_json(self):
        text = mod.render(self.SNAP)
        self.assertIn("final class CameraExposureFailedPayload {", text)
        self.assertIn("required this.frameId,", text)
        self.assertIn("this.exposureSec,\n", text)
        self.assertIn("frameId: json['frame_id'] as String,", text)
        self.assertIn("exposureSec: (json['exposure_sec'] as num?)?.toDouble(),", text)
        self.assertIn("elapsedMs: json['elapsed_ms'] as int,", text)
        self.assertIn("final double? exposureSec;", text)

    def test_an_event_without_a_shape_gets_no_class(self):
        text = mod.render('{"events":[{"name":"GuiderStep","type":"guider.step"}]}')
        self.assertNotIn("class ", text.replace("abstract final class WsEventTypes", ""))


class Drift(unittest.TestCase):
    def test_committed_dart_file_matches_the_snapshot(self):
        self.assertEqual(mod.main(["--check"]), 0, "run scripts/generate-ws-events-dart.py")


if __name__ == "__main__":
    unittest.main()
