import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/guider/guider_build_activity_state.dart';

WsEvent _ev(String type, [Map<String, dynamic> payload = const {}]) =>
    WsEvent(type: type, ts: DateTime.utc(2026, 7, 6), seq: 1, payload: payload);

void main() {
  group('foldGuiderBuildEvent', () {
    test('started → building for the right artifact only', () {
      final next = foldGuiderBuildEvent(const {}, _ev('guider.dark_library.started'))!;
      expect(next[CalibrationArtifact.darkLibrary]?.phase, CalibrationBuildPhase.building);
      expect(next.containsKey(CalibrationArtifact.defectMap), isFalse);
    });

    test('complete replaces building; the other artifact is untouched', () {
      var state = foldGuiderBuildEvent(const {}, _ev('guider.defect_map.started'))!;
      state = foldGuiderBuildEvent(state, _ev('guider.dark_library.started'))!;
      state = foldGuiderBuildEvent(state, _ev('guider.dark_library.complete'))!;
      expect(state[CalibrationArtifact.darkLibrary]?.phase, CalibrationBuildPhase.complete);
      expect(state[CalibrationArtifact.defectMap]?.phase, CalibrationBuildPhase.building,
          reason: 'defect map build still in flight');
    });

    test('failed carries the payload error', () {
      final next = foldGuiderBuildEvent(
          const {}, _ev('guider.defect_map.failed', {'error': 'camera timeout'}))!;
      final activity = next[CalibrationArtifact.defectMap]!;
      expect(activity.phase, CalibrationBuildPhase.failed);
      expect(activity.error, 'camera timeout');
    });

    test('failed with a non-string error folds with error null (never throws)', () {
      final next = foldGuiderBuildEvent(
          const {}, _ev('guider.dark_library.failed', {'error': 42}))!;
      expect(next[CalibrationArtifact.darkLibrary]?.error, isNull);
    });

    test('non-build events and unknown subtypes return null (no state churn)', () {
      expect(foldGuiderBuildEvent(const {}, _ev('guider.state')), isNull);
      expect(foldGuiderBuildEvent(const {}, _ev('diagnostics.cleared')), isNull);
      expect(foldGuiderBuildEvent(const {}, _ev('guider.dark_library.progress')), isNull,
          reason: 'a future subtype this build does not know is skipped, not misfiled');
    });
  });
}
