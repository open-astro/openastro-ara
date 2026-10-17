import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_calibration_view.dart';
import 'package:openastroara/models/guider_live.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_live_api.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/guider/guider_live_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _History implements GuiderLiveClient {
  final List<Map<String, dynamic>> history;
  final bool fail;
  int calls = 0;
  _History(this.history, {this.fail = false});
  @override
  Future<List<Map<String, dynamic>>> getHistory() async {
    calls++;
    if (fail) throw StateError('daemon without the history route');
    return history;
  }

  @override
  Future<GuiderLive> getLive() async => GuiderLive.disconnected;
  @override
  Future<Uint8List?> getFrame() async => null;
  @override
  Future<void> loop() async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> selectStar(double x, double y) async {}
  @override
  Future<void> deselectStar() async {}
  @override
  Future<GuiderCalibrationView> getCalibration() async =>
      GuiderCalibrationView.disconnected;
  @override
  Future<void> clearCalibration() async {}
  @override
  void close() {}
}

Map<String, dynamic> _p(double age, double ra) => {
  'age_sec': age,
  'ra_raw_px': ra,
  'dec_raw_px': -ra,
  'ra_arcsec': ra * 5,
  'dec_arcsec': -ra * 5,
  'ra_duration_ms': -120,
  'dec_duration_ms': 0,
  'pixel_scale_arcsec': 5.0,
  'snr': 40,
};

void main() {
  test('history payloads become steps stamped by their age, oldest first', () {
    final at = DateTime.utc(2026, 10, 16, 4, 0, 0);
    final steps = GuideStepNotifier.historySteps([
      _p(2, 0.3),
      _p(6, 0.1),
      _p(4, 0.2),
    ], at);
    expect(steps.map((s) => s.raPx), [0.1, 0.2, 0.3]);
    expect(steps.first.at, at.subtract(const Duration(seconds: 6)));
    expect(steps.last.at, at.subtract(const Duration(seconds: 2)));
    expect(
      steps.first.raPulseMs,
      -120,
      reason: 'the guider.step sign survives',
    );
    final many = GuideStepNotifier.historySteps([
      for (var i = 500; i > 0; i--) _p(i.toDouble(), 0),
    ], at);
    expect(
      many,
      hasLength(kGuideStepHistory),
      reason: 'capped like the live window',
    );
  });

  test(
    'an empty graph is seeded once from the guider, never over live steps',
    () async {
      final fake = _History([_p(4, 0.2), _p(2, 0.3)]);
      final c = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'h', port: 1),
          ),
          guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
          guiderStatusProvider.overrideWith(() => _Connected()),
        ],
      );
      addTearDown(c.dispose);
      c.read(guideStepsProvider);
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      expect(c.read(guideStepsProvider), hasLength(2));
      // A second seed is a no-op once the graph has steps.
      await c.read(guideStepsProvider.notifier).seedFromHistory();
      expect(c.read(guideStepsProvider), hasLength(2));
      expect(fake.calls, 1);
    },
  );

  test('a failed history fetch leaves the graph empty and quiet', () async {
    final fake = _History(const [], fail: true);
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'h', port: 1),
        ),
        guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
        guiderStatusProvider.overrideWith(() => _Connected()),
      ],
    );
    addTearDown(c.dispose);
    c.read(guideStepsProvider);
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(fake.calls, 1);
    expect(c.read(guideStepsProvider), isEmpty);
    // The graph still fills live afterwards, and a later seed may try again.
    await c.read(guideStepsProvider.notifier).seedFromHistory();
    expect(fake.calls, 2, reason: 'a failure is not remembered as "seeded"');
  });

  test('without a server nothing is fetched', () async {
    final c = ProviderContainer(
      overrides: [activeServerProvider.overrideWithValue(null)],
    );
    addTearDown(c.dispose);
    c.read(guideStepsProvider);
    await Future<void>.delayed(Duration.zero);
    expect(c.read(guideStepsProvider), isEmpty);
  });
}

class _Connected extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => const GuiderStatus(
    name: 'Guider',
    connectionState: GuiderConnectionState.connected,
    runtimeState: GuiderRuntimeState.guiding,
  );
}
