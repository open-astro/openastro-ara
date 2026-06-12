import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';

void main() {
  group('GuiderStatus.fromJson', () {
    test('parses a connected, guiding descriptor with RMS', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'PHD2',
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
    });

    test('star_lost runtime token maps to the camelCase enum', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'PHD2',
        'state': 'connected',
        'runtime': <String, dynamic>{'state': 'star_lost'},
      });
      expect(s.runtimeState, GuiderRuntimeState.starLost);
      expect(s.isGuiding, isFalse);
    });

    test('a disconnected guider has null RMS and stopped/unknown runtime', () {
      final s = GuiderStatus.fromJson(<String, dynamic>{
        'device_id': 'phd2',
        'name': 'PHD2',
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
        'name': 'PHD2',
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

    test('value equality — equal-content parses compare equal (no rebuild churn)', () {
      Map<String, dynamic> frame() => <String, dynamic>{
            'device_id': 'phd2',
            'name': 'PHD2',
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
