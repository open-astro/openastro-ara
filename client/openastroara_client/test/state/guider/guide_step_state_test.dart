import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

void main() {
  final t = DateTime.utc(2026, 10, 4, 22);

  group('GuideStep.fromPayload', () {
    test('parses the full daemon payload', () {
      final s = GuideStep.fromPayload(<String, dynamic>{
        'frame': 7,
        'time_sec': 1.7e9,
        'ra_raw_px': 0.25,
        'dec_raw_px': -0.5,
        'ra_arcsec': 0.5,
        'dec_arcsec': -1.0,
        'ra_duration_ms': -120,
        'dec_duration_ms': 80,
        'pixel_scale_arcsec': 2.0,
        'snr': 31.5,
      }, t)!;
      expect(s.at, t);
      expect(s.raPx, 0.25);
      expect(s.decPx, -0.5);
      expect(s.raArcsec, 0.5);
      expect(s.decArcsec, -1.0);
      expect(s.raPulseMs, -120);
      expect(s.decPulseMs, 80);
      expect(s.pixelScaleArcsec, 2.0);
      expect(s.snr, 31.5);
    });

    test('a lost-star frame with no offsets is dropped', () {
      expect(
          GuideStep.fromPayload(<String, dynamic>{
            'frame': 1,
            'ra_raw_px': null,
            'dec_raw_px': null,
            'ra_duration_ms': 0,
          }, t),
          isNull);
    });

    test('missing arcsec falls back to the client scale, else null', () {
      final s = GuideStep.fromPayload(
          <String, dynamic>{'ra_raw_px': 0.5, 'dec_raw_px': -0.5}, t)!;
      expect(s.raArcsecWith(2.0), 1.0);
      expect(s.decArcsecWith(2.0), -1.0);
      expect(s.raArcsecWith(null), isNull);
      // A daemon figure wins over the fallback.
      final d = GuideStep.fromPayload(<String, dynamic>{
        'ra_raw_px': 0.5,
        'dec_raw_px': 0.5,
        'ra_arcsec': 0.9,
        'dec_arcsec': 0.9,
      }, t)!;
      expect(d.raArcsecWith(2.0), 0.9);
    });

    test('wrong-typed and non-finite values read as null, pulses as zero', () {
      final s = GuideStep.fromPayload(<String, dynamic>{
        'ra_raw_px': 'bad',
        'dec_raw_px': 0.1,
        'ra_duration_ms': 'x',
        'snr': double.nan,
      }, t)!;
      expect(s.raPx, isNull);
      expect(s.decPx, 0.1);
      expect(s.raPulseMs, 0);
      expect(s.snr, isNull);
    });
  });

  group('GuideMarker.fromPayload', () {
    test('maps every wire kind and the dither / status / error details', () {
      final d = GuideMarker.fromPayload(
          <String, dynamic>{'kind': 'dithered', 'dx_px': 2.5, 'dy_px': -1}, t)!;
      expect(d.kind, GuideMarkerKind.dithered);
      expect(d.dxPx, 2.5);
      expect(d.dyPx, -1);
      final done = GuideMarker.fromPayload(
          <String, dynamic>{'kind': 'settle_done', 'status': 0}, t)!;
      expect(done.kind, GuideMarkerKind.settleDone);
      expect(done.status, 0);
      expect(done.error, isNull);
      final lost = GuideMarker.fromPayload(
          <String, dynamic>{'kind': 'star_lost', 'error': 'low SNR', 'status': 2}, t)!;
      expect(lost.kind, GuideMarkerKind.starLost);
      expect(lost.error, 'low SNR');
      for (final k in [
        'settling', 'calibration_started', 'calibration_complete', 'calibration_failed',
        'guiding_started', 'guiding_stopped', 'paused', 'resumed', 'lock_position_lost',
      ]) {
        expect(GuideMarker.fromPayload(<String, dynamic>{'kind': k}, t), isNotNull, reason: k);
      }
      expect(GuideMarker.fromPayload(<String, dynamic>{'kind': 'nope'}, t), isNull);
      expect(GuideMarker.fromPayload(const <String, dynamic>{}, t), isNull);
    });
  });

  group('guideMarkersProvider', () {
    test('appends guider.event markers and caps the history', () async {
      final ws = StreamController<WsEvent>.broadcast();
      final container = ProviderContainer(overrides: [
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ]);
      addTearDown(() => unawaited(ws.close()));
      addTearDown(container.dispose);
      final sub = container.listen(guideMarkersProvider, (_, _) {});
      addTearDown(sub.close);
      ws.add(WsEvent(type: 'guider.event', ts: t, seq: 1, payload: const {'kind': 'dithered'}));
      ws.add(WsEvent(type: 'guider.step', ts: t, seq: 2, payload: const {'ra_raw_px': 0.1}));
      await Future<void>.delayed(Duration.zero);
      expect(container.read(guideMarkersProvider).single.kind, GuideMarkerKind.dithered);
      final n = container.read(guideMarkersProvider.notifier);
      for (var i = 0; i < kGuideMarkerHistory + 5; i++) {
        n.add(GuideMarker(at: t, kind: GuideMarkerKind.starLost));
      }
      expect(container.read(guideMarkersProvider).length, kGuideMarkerHistory);
      n.clear();
      expect(container.read(guideMarkersProvider), isEmpty);
    });
  });

  group('guideStepsProvider', () {
    test('appends guider.step events and ignores everything else', () async {
      // Broadcast: close() then completes with no listener left, so the
      // teardown cannot hang on an undelivered done event.
      final ws = StreamController<WsEvent>.broadcast();
      final container = ProviderContainer(overrides: [
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ]);
      // LIFO: the container (and with it the stream subscription) goes first,
      // then the controller closes with nobody left to deliver done to.
      addTearDown(() => unawaited(ws.close()));
      addTearDown(container.dispose);
      final sub = container.listen(guideStepsProvider, (_, _) {});
      addTearDown(sub.close);

      ws.add(WsEvent(type: 'frame.complete', ts: t, seq: 1, payload: const {}));
      ws.add(WsEvent(type: 'guider.step', ts: t, seq: 2, payload: const {
        'ra_raw_px': 0.1,
        'dec_raw_px': 0.2,
      }));
      await Future<void>.delayed(Duration.zero);
      final steps = container.read(guideStepsProvider);
      expect(steps.length, 1);
      expect(steps.single.raPx, 0.1);
    });

    test('keeps only the newest kGuideStepHistory steps', () {
      final container = ProviderContainer(overrides: [
        wsEventsProvider.overrideWith((ref) => const Stream<WsEvent>.empty()),
      ]);
      addTearDown(container.dispose);
      final n = container.read(guideStepsProvider.notifier);
      for (var i = 0; i < kGuideStepHistory + 25; i++) {
        n.add(GuideStep(at: t.add(Duration(seconds: i)), raPx: i.toDouble()));
      }
      final steps = container.read(guideStepsProvider);
      expect(steps.length, kGuideStepHistory);
      expect(steps.first.raPx, 25);
      expect(steps.last.raPx, (kGuideStepHistory + 24).toDouble());
      n.clear();
      expect(container.read(guideStepsProvider), isEmpty);
    });
  });
}
