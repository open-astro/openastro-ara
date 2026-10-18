import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/legacy.dart';
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
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/ws_event.dart';

class _History implements GuiderLiveClient {
  final List<Map<String, dynamic>> history;
  final bool fail;

  /// When set, getHistory waits for it — a fetch still in flight.
  final Completer<void>? gate;
  int calls = 0;
  _History(this.history, {this.fail = false, this.gate});
  @override
  Future<List<Map<String, dynamic>>> getHistory() async {
    calls++;
    if (gate != null) await gate!.future;
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

  test('a guider that connects later seeds the graph then', () async {
    // The on-connect listener, not the start-up seed: the guider is
    // disconnected at start-up, so only the transition can fill the graph.
    final fake = _History([_p(4, 0.2), _p(2, 0.3)]);
    final status = _Switchable();
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(() => unawaited(ws.close()));
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'h', port: 1),
        ),
        guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
        guiderStatusProvider.overrideWith(() => status),
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ],
    );
    addTearDown(c.dispose);
    c.read(guideStepsProvider);
    await c.read(guiderStatusProvider.future);
    await Future<void>.delayed(Duration.zero);
    expect(fake.calls, 0, reason: 'no history from a disconnected guider');
    expect(c.read(guideStepsProvider), isEmpty);
    status.goConnected();
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(fake.calls, 1);
    expect(c.read(guideStepsProvider), hasLength(2));
  });

  test('a live step that lands while the history is in flight wins', () async {
    final gate = Completer<void>();
    final fake = _History([_p(4, 0.2), _p(2, 0.3)], gate: gate);
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(() => unawaited(ws.close()));
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'h', port: 1),
        ),
        guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
        guiderStatusProvider.overrideWith(() => _Connected()),
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ],
    );
    addTearDown(c.dispose);
    final sub = c.listen(guideStepsProvider, (_, _) {});
    addTearDown(sub.close);
    await c.read(guiderStatusProvider.future);
    await c
        .read(guideStepsProvider.notifier)
        .seedFromHistory()
        .timeout(Duration.zero, onTimeout: () {});
    expect(fake.calls, 1, reason: 'the fetch is in flight');
    ws.add(
      WsEvent(
        type: 'guider.step',
        ts: DateTime.utc(2026, 10, 16),
        seq: 1,
        payload: const {'ra_raw_px': 0.9, 'dec_raw_px': -0.9},
      ),
    );
    await Future<void>.delayed(Duration.zero);
    expect(c.read(guideStepsProvider), hasLength(1));
    gate.complete();
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    final steps = c.read(guideStepsProvider);
    expect(
      steps,
      hasLength(1),
      reason: 'the late history never replaces live steps',
    );
    expect(steps.single.raPx, 0.9);
  });

  test(
    'a clear while the history is in flight drops the late history',
    () async {
      // A log replay clears the graph and then draws its own frames; a seed
      // started before the clear must not land in the gap and mix with them.
      final gate = Completer<void>();
      final fake = _History([_p(4, 0.2), _p(2, 0.3)], gate: gate);
      final ws = StreamController<WsEvent>.broadcast();
      addTearDown(() => unawaited(ws.close()));
      final c = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'h', port: 1),
          ),
          guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
          guiderStatusProvider.overrideWith(() => _Connected()),
          wsEventsProvider.overrideWith((ref) => ws.stream),
        ],
      );
      addTearDown(c.dispose);
      final sub = c.listen(guideStepsProvider, (_, _) {});
      addTearDown(sub.close);
      await c.read(guiderStatusProvider.future);
      await Future<void>.delayed(Duration.zero);
      expect(fake.calls, 1, reason: 'the start-up fetch is in flight');
      c.read(guideStepsProvider.notifier).clear();
      gate.complete();
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      expect(c.read(guideStepsProvider), isEmpty);
    },
  );

  test('without a server nothing is fetched', () async {
    final c = ProviderContainer(
      overrides: [activeServerProvider.overrideWithValue(null)],
    );
    addTearDown(c.dispose);
    c.read(guideStepsProvider);
    await Future<void>.delayed(Duration.zero);
    expect(c.read(guideStepsProvider), isEmpty);
  });

  test('switching servers drops the graph and seeds from the new one', () async {
    // Review of #1387: the root-scoped graph kept the old server's steps and,
    // being non-empty, never asked the new server for its history.
    final old = _History([_p(4, 0.2), _p(2, 0.3)]);
    final fresh = _History([_p(3, 0.5), _p(2, 0.6), _p(1, 0.7)]);
    final server = StateProvider<AraServer?>(
      (_) => const AraServer(hostname: 'a', port: 1),
    );
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(() => unawaited(ws.close()));
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWith((ref) => ref.watch(server)),
        guiderLiveApiFactoryProvider.overrideWithValue(
          (s) => s.hostname == 'a' ? old : fresh,
        ),
        guiderStatusProvider.overrideWith(_Connected.new),
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ],
    );
    addTearDown(c.dispose);
    // The app shell always watches the active server; without a watcher the
    // derived provider is lazy and its listeners never hear the change.
    c.listen(activeServerProvider, (_, _) {});
    c.read(guideStepsProvider);
    await c.read(guiderStatusProvider.future);
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(c.read(guideStepsProvider), hasLength(2));

    c.read(server.notifier).state = const AraServer(hostname: 'b', port: 1);
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(old.calls, 1, reason: 'the old server is not asked again');
    expect(fresh.calls, 1);
    expect(c.read(guideStepsProvider), hasLength(3));
    expect(c.read(guideStepsProvider).last.raPx, 0.7);
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

/// Disconnected until [goConnected] — drives the on-connect seed.
class _Switchable extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => const GuiderStatus(
    name: 'Guider',
    connectionState: GuiderConnectionState.disconnected,
    runtimeState: GuiderRuntimeState.unknown,
  );

  void goConnected() => state = const AsyncData(
    GuiderStatus(
      name: 'Guider',
      connectionState: GuiderConnectionState.connected,
      runtimeState: GuiderRuntimeState.guiding,
    ),
  );
}
