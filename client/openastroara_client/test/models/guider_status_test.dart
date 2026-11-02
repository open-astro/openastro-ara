import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';

void main() {
  group('GuiderStatus.fromJson', () {
    test('parses a connected, guiding descriptor with RMS', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'OpenAstro Guider',
        'state': 'connected',
        'runtime': <String, dynamic>{
          'state': 'guiding',
          'rms_total': 0.42,
          'rms_ra': 0.31,
          'rms_dec': 0.28,
          'current_profile': 'ara-c14-cem120-a3f8',
        },
      });
      expect(s.connectionState, GuiderConnectionState.connected);
      expect(s.isConnected, isTrue);
      expect(s.runtimeState, GuiderRuntimeState.guiding);
      expect(s.isGuiding, isTrue);
      expect(s.rmsTotal, 0.42);
      expect(s.rmsRa, 0.31);
      expect(s.rmsDec, 0.28);
      expect(s.currentProfile, 'ara-c14-cem120-a3f8');
      // No arcsec figures on this payload → null, never a guessed conversion.
      expect(s.rmsTotalArcsec, isNull);
    });

    test('parses the guider profile list for the Settings picker', () {
      final s = GuiderStatus.fromJson({
        'device_id': 'PHD2_Single', 'name': 'PHD2', 'state': 'connected',
        'runtime': {
          'state': 'stopped',
          'profiles': [
            {'id': 1, 'name': 'Default'},
            {'id': 4, 'name': 'ara-main'},
            {'name': 'no id is dropped'},
          ],
        },
      });
      expect(s.profiles, {1: 'Default', 4: 'ara-main'});
      expect(GuiderStatus.fromJson(const {'name': 'PHD2', 'state': 'disconnected'}).profiles, isEmpty);
    });

    test('parses the daemon arcsec RMS alongside the pixel figures', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'name': 'PHD2',
        'state': 'connected',
        'runtime': <String, dynamic>{
          'state': 'guiding',
          'rms_total': 0.42,
          'rms_ra': 0.31,
          'rms_dec': 0.28,
          'rms_total_arcsec': 0.84,
          'rms_ra_arcsec': 0.62,
          'rms_dec_arcsec': 0.56,
        },
      });
      expect(s.rmsTotal, 0.42);
      expect(s.rmsTotalArcsec, 0.84);
      expect(s.rmsRaArcsec, 0.62);
      expect(s.rmsDecArcsec, 0.56);
    });

    test('star_lost runtime token maps to the camelCase enum', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'OpenAstro Guider',
        'state': 'connected',
        'runtime': <String, dynamic>{'state': 'star_lost'},
      });
      expect(s.runtimeState, GuiderRuntimeState.starLost);
      expect(s.isGuiding, isFalse);
    });

    test('a calibrating guider carries the leg, step and distance', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'PHD2',
        'state': 'connected',
        'runtime': <String, dynamic>{
          'state': 'calibrating',
          'calibration_direction': 'North',
          'calibration_step': 7,
          'calibration_distance_px': 11.9,
        },
      });
      expect(s.runtimeState, GuiderRuntimeState.calibrating);
      expect(s.calibrationText, 'North step 7 · 11.9 px');
      final bare = GuiderStatus.fromJson(<String, dynamic>{
        'state': 'connected',
        'runtime': <String, dynamic>{'state': 'calibrating'},
      });
      expect(bare.calibrationText, isNull, reason: 'no leg reported yet');
    });

    test('looping, a selected star and settling are their own phases', () {
      GuiderStatus withState(String token) => GuiderStatus.fromJson(<String, dynamic>{
            'state': 'connected',
            'runtime': <String, dynamic>{'state': token},
          });
      expect(withState('looping').runtimeState, GuiderRuntimeState.looping);
      expect(withState('star_selected').runtimeState, GuiderRuntimeState.starSelected);
      expect(withState('settling').runtimeState, GuiderRuntimeState.settling);
      expect(withState('assistant').runtimeState, GuiderRuntimeState.assistant);
      expect(withState('assistant').runtimeSummary, 'Guiding Assistant');
      expect(withState('looping').runtimeSummary, 'Looping');
      expect(withState('settling').runtimeSummary, 'Settling');
      expect(withState('settling').isGuiding, isFalse, reason: 'isGuiding stays the plain guiding phase');
    });

    test('the summary carries the calibration leg and says when not connected', () {
      final cal = GuiderStatus.fromJson(<String, dynamic>{
        'state': 'connected',
        'runtime': <String, dynamic>{
          'state': 'calibrating',
          'calibration_direction': 'West',
          'calibration_step': 3,
          'calibration_distance_px': 4.2,
        },
      });
      expect(cal.runtimeSummary, 'Calibrating · West step 3 · 4.2 px');
      expect(GuiderStatus.fromJson(<String, dynamic>{'state': 'disconnected'}).runtimeSummary, 'Not connected');
      for (final s in GuiderRuntimeState.values) {
        expect(guiderRuntimeText(s), isNotEmpty);
      }
    });

    test('a disconnected guider has null RMS and stopped/unknown runtime', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'OpenAstro Guider',
        'state': 'disconnected',
        'runtime': <String, dynamic>{'state': 'stopped'},
      });
      expect(s.connectionState, GuiderConnectionState.disconnected);
      expect(s.isConnected, isFalse);
      expect(s.runtimeState, GuiderRuntimeState.stopped);
      expect(s.rmsTotal, isNull);
    });

    test('unrecognised tokens degrade to unknown rather than throwing', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'state': 'reticulating',
        'runtime': <String, dynamic>{'state': 'splining'},
      });
      expect(s.connectionState, GuiderConnectionState.unknown);
      expect(s.runtimeState, GuiderRuntimeState.unknown);
      expect(s.name, 'Guider', reason: 'defaults when name absent');
    });

    test('a missing runtime object degrades to unknown runtime, not a throw', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'OpenAstro Guider',
        'state': 'connecting',
      });
      expect(s.connectionState, GuiderConnectionState.connecting);
      expect(s.runtimeState, GuiderRuntimeState.unknown);
      expect(s.rmsTotal, isNull);
    });

    test('integer RMS values coerce to double', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'state': 'connected',
        'runtime': <String, dynamic>{'state': 'guiding', 'rms_total': 1},
      });
      expect(s.rmsTotal, 1.0);
    });

    test('wrong-typed string fields degrade to null/default rather than throwing', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 42, // not a String
        'name': 7, // not a String
        'state': <String, dynamic>{}, // not a String
        'runtime': <String, dynamic>{'state': 99, 'current_profile': true},
      });
      expect(s.deviceId, isNull);
      expect(s.name, 'Guider');
      expect(s.connectionState, GuiderConnectionState.unknown);
      expect(s.runtimeState, GuiderRuntimeState.unknown);
      expect(s.currentProfile, isNull);
    });

    test('string-encoded RMS values are coerced to double', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'state': 'connected',
        'runtime': <String, dynamic>{'state': 'guiding', 'rms_total': '0.42', 'rms_ra': 'nope'},
      });
      expect(s.rmsTotal, 0.42);
      expect(s.rmsRa, isNull, reason: 'unparseable string → null');
    });

    test('non-finite RMS values are filtered to null', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'state': 'connected',
        'runtime': <String, dynamic>{
          'state': 'guiding',
          'rms_total': 'NaN',
          'rms_ra': 'Infinity',
          'rms_dec': double.nan,
        },
      });
      expect(s.rmsTotal, isNull);
      expect(s.rmsRa, isNull);
      expect(s.rmsDec, isNull);
    });

    test('value equality — equal-content parses compare equal (no rebuild churn)', () {
      Map<String, dynamic> frame() => <String, dynamic>{
            'device_id': 'phd2',
            'name': 'OpenAstro Guider',
            'state': 'connected',
            'runtime': <String, dynamic>{'state': 'guiding', 'rms_total': 0.4},
          };
      final a = GuiderStatus.fromJson(frame());
      final b = GuiderStatus.fromJson(frame());
      expect(a, equals(b));
      expect(a.hashCode, b.hashCode);

      final c = GuiderStatus.fromJson(frame()..['state'] = 'disconnected');
      expect(a, isNot(equals(c)));
    });
  });
}
