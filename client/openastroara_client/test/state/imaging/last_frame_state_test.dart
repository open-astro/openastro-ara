import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

WsEvent _event(String type, Map<String, dynamic> payload) =>
    WsEvent(type: type, ts: DateTime.now().toUtc(), seq: 1, payload: payload);

void main() {
  test('every frame.complete moves the Imaging viewer to that frame', () async {
    // The viewer only learned about Take One shots; a running sequence left it
    // blank (2026-10-03).
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    final container = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
    ]);
    addTearDown(container.dispose);
    container.listen(lastCapturedFrameIdProvider, (_, _) {});

    expect(container.read(lastCapturedFrameIdProvider), isNull);
    ws.add(_event('frame.complete', {'frame_id': 'seq-1', 'frame_type': 'light'}));
    await Future<void>.delayed(Duration.zero);
    expect(container.read(lastCapturedFrameIdProvider), 'seq-1');

    ws.add(_event('frame.analyzed', {'frame_id': 'other'}));
    ws.add(_event('frame.complete', {'frame_id': ''}));
    await Future<void>.delayed(Duration.zero);
    expect(container.read(lastCapturedFrameIdProvider), 'seq-1', reason: 'other events and empty ids are ignored');

    container.read(lastCapturedFrameIdProvider.notifier).set('manual-1');
    expect(container.read(lastCapturedFrameIdProvider), 'manual-1', reason: 'Take One still sets it directly');
  });
}
