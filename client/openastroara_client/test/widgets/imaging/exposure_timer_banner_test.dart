import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/imaging/exposure_timer_banner.dart';

WsEvent _ev(String type, Map<String, dynamic> payload, [int seq = 1]) => WsEvent(
    type: type, ts: DateTime.utc(2026, 10, 4), seq: seq, payload: payload);

Future<(ProviderContainer, StreamController<WsEvent>)> _pump(WidgetTester tester) async {
  // Broadcast: close() then completes with no listener left, so the
      // teardown cannot hang on an undelivered done event.
      final ws = StreamController<WsEvent>.broadcast();
  final container = ProviderContainer(overrides: [
    wsEventsProvider.overrideWith((ref) => ws.stream),
    wsConnectionStateProvider
        .overrideWith((ref) => Stream.value(WsConnectionState.connected)),
  ]);
  addTearDown(ws.close);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: ExposureTimerBanner())),
  ));
  return (container, ws);
}

/// Unmount first so the banner's 250 ms ticker is cancelled before the
/// pending-timer check, then drop the container.
Future<void> _teardown(WidgetTester tester, ProviderContainer c) async {
  await tester.pumpWidget(const SizedBox());
  c.dispose();
  await tester.pump();
}

void main() {
  testWidgets('hidden while nothing is exposing', (tester) async {
    final (c, _) = await _pump(tester);
    expect(find.textContaining('Exposing'), findsNothing);
    await _teardown(tester, c);
  });

  testWidgets('shows kind, filter and the elapsed / total clock while '
      'exposing, then Downloading once the exposure time is up, then hides',
      (tester) async {
    final (c, ws) = await _pump(tester);
    ws.add(_ev('camera.exposure_started', {
      'frame_id': 'f1',
      'exposure_sec': 120,
      'kind': 'light',
      'filter_name': 'OIII',
    }));
    await tester.pump();
    await tester.pump();
    expect(find.text('Exposing · Light · OIII'), findsOneWidget);
    expect(find.textContaining('/ 2:00'), findsOneWidget);
    final bar = tester.widget<LinearProgressIndicator>(
        find.byType(LinearProgressIndicator));
    expect(bar.value, isNotNull);

    // A zero-length exposure is "downloading" the moment it starts.
    ws.add(_ev('camera.exposure_started', {
      'frame_id': 'f2',
      'exposure_sec': 0,
      'kind': 'plate-solve',
    }, 2));
    await tester.pump();
    await tester.pump();
    expect(find.text('Downloading · Plate solve'), findsOneWidget);
    expect(
        tester
            .widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator))
            .value,
        isNull,
        reason: 'indeterminate while the daemon reads out');

    ws.add(_ev('camera.exposure_complete', {'frame_id': 'f2'}, 3));
    await tester.pump();
    await tester.pump();
    expect(find.textContaining('Downloading'), findsNothing);
    expect(find.textContaining('Exposing'), findsNothing);
    await _teardown(tester, c);
  });

  test('formatExposureClock', () {
    expect(formatExposureClock(const Duration(seconds: 47), const Duration(minutes: 2)),
        '0:47 / 2:00');
    expect(formatExposureClock(const Duration(seconds: 125), const Duration(seconds: 300)),
        '2:05 / 5:00');
    expect(formatExposureClock(const Duration(milliseconds: 4200), const Duration(seconds: 10)),
        '4.2 / 10 s');
    expect(formatExposureClock(const Duration(milliseconds: 500), const Duration(milliseconds: 2500)),
        '0.5 / 2.5 s');
    // Elapsed never overshoots the total (the download phase).
    expect(formatExposureClock(const Duration(seconds: 12), const Duration(seconds: 10)),
        '10.0 / 10 s');
    expect(formatExposureClock(const Duration(minutes: 3), const Duration(minutes: 2)),
        '2:00 / 2:00');
  });
}
