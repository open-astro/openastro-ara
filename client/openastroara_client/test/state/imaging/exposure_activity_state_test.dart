import 'dart:async';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/imaging/exposure_activity_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

WsEvent _ev(String type, Map<String, dynamic> payload, [int seq = 1]) => WsEvent(
    type: type, ts: DateTime.utc(2026, 10, 4), seq: seq, payload: payload);

Map<String, dynamic> _started(String id, double secs, {String kind = 'light', String? filter}) =>
    <String, dynamic>{
      'frame_id': id,
      'exposure_sec': secs,
      'started_utc': '2026-10-04T21:00:00.000Z',
      'kind': kind,
      'filter_name': filter,
    };

class _Rig {
  final ws = StreamController<WsEvent>();
  final link = StreamController<WsConnectionState>();
  late final ProviderContainer container;
  late final ProviderSubscription<ExposureActivity?> sub;
  _Rig() {
    container = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
      wsConnectionStateProvider.overrideWith((ref) => link.stream),
    ]);
    sub = container.listen(exposureActivityProvider, (_, _) {});
  }
  ExposureActivity? get state => container.read(exposureActivityProvider);
  void dispose() {
    sub.close();
    container.dispose();
    ws.close();
    link.close();
  }
}

void main() {
  test('exposure_started opens an activity stamped on the client clock, '
      'exposure_complete for it closes it', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    final before = DateTime.now();
    rig.ws.add(_ev('camera.exposure_started', _started('f1', 120, filter: 'Ha')));
    await Future<void>.delayed(Duration.zero);
    final a = rig.state!;
    expect(a.frameId, 'f1');
    expect(a.exposure, const Duration(seconds: 120));
    expect(a.kind, 'light');
    expect(a.kindLabel, 'Light');
    expect(a.filterName, 'Ha');
    expect(a.startedAt.isBefore(before), isFalse);

    rig.ws.add(_ev('camera.exposure_complete', {'frame_id': 'f1'}, 2));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNull);
  });

  test('exposure_failed and a matching frame.complete both close it; a '
      'foreign frame.complete does not', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    rig.ws.add(_ev('camera.exposure_started', _started('f2', 5, kind: 'analysis')));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state!.kindLabel, 'Focus probe');
    rig.ws.add(_ev('frame.complete', {'frame_id': 'other'}, 2));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNotNull, reason: 'a late orphan registering is not ours');
    rig.ws.add(_ev('frame.complete', {'frame_id': 'f2'}, 3));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNull);

    rig.ws.add(_ev('camera.exposure_started', _started('f3', 5, kind: 'flat'), 4));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state!.kindLabel, 'Flat');
    rig.ws.add(_ev('camera.exposure_failed', {'frame_id': 'f3', 'reason': 'cancelled'}, 5));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNull);
  });

  test('a late failed or complete for an earlier exposure leaves the live '
      'timer running', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    rig.ws.add(_ev('camera.exposure_started', _started('f6', 30)));
    await Future<void>.delayed(Duration.zero);
    rig.ws.add(_ev('camera.exposure_failed', {'frame_id': 'f5', 'reason': 'aborted'}, 2));
    rig.ws.add(_ev('camera.exposure_complete', {'frame_id': 'f5'}, 3));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state?.frameId, 'f6', reason: 'events for another frame are not ours');
    rig.ws.add(_ev('camera.exposure_complete', {'frame_id': 'f6'}, 4));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNull);
  });

  test('a newer started replaces the current one', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    rig.ws.add(_ev('camera.exposure_started', _started('f4', 10)));
    rig.ws.add(_ev('camera.exposure_started', _started('f5', 20), 2));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state!.frameId, 'f5');
    expect(rig.state!.exposure, const Duration(seconds: 20));
  });

  test('endLocally drops the activity and its watchdog', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    rig.ws.add(_ev('camera.exposure_started', _started('f8', 600)));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNotNull);
    rig.container.read(exposureActivityProvider.notifier).endLocally();
    expect(rig.state, isNull);
  });

  test('malformed started payloads are ignored', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    rig.ws.add(_ev('camera.exposure_started', {'frame_id': '', 'exposure_sec': 3}));
    rig.ws.add(_ev('camera.exposure_started', {'frame_id': 'x', 'exposure_sec': 'nope'}, 2));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNull);
  });

  test('losing the WS link clears the activity', () async {
    final rig = _Rig();
    addTearDown(rig.dispose);
    rig.ws.add(_ev('camera.exposure_started', _started('f6', 300)));
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNotNull);
    rig.link.add(WsConnectionState.disconnected);
    await Future<void>.delayed(Duration.zero);
    expect(rig.state, isNull);
  });

  test('the watchdog drops an activity whose complete never arrives', () {
    fakeAsync((async) {
      final rig = _Rig();
      rig.ws.add(_ev('camera.exposure_started', _started('f7', 10)));
      async.flushMicrotasks();
      expect(rig.state, isNotNull);
      async.elapse(const Duration(seconds: 10) + kExposureActivityGrace -
          const Duration(seconds: 1));
      expect(rig.state, isNotNull, reason: 'still inside the grace window');
      async.elapse(const Duration(seconds: 2));
      expect(rig.state, isNull);
      rig.dispose();
      async.flushMicrotasks();
    });
  });

  test('ExposureActivity clock helpers', () {
    final start = DateTime(2026, 10, 4, 21);
    final a = ExposureActivity(
        frameId: 'f', exposure: const Duration(seconds: 10), startedAt: start, kind: 'light');
    expect(a.elapsed(start.subtract(const Duration(seconds: 1))), Duration.zero);
    expect(a.elapsed(start.add(const Duration(seconds: 4))), const Duration(seconds: 4));
    expect(a.remaining(start.add(const Duration(seconds: 4))), const Duration(seconds: 6));
    expect(a.remaining(start.add(const Duration(seconds: 40))), Duration.zero);
    expect(a.progress(start.add(const Duration(seconds: 5))), closeTo(0.5, 1e-9));
    expect(a.isDownloading(start.add(const Duration(seconds: 9))), isFalse);
    expect(a.isDownloading(start.add(const Duration(seconds: 10))), isTrue);
    final zero = ExposureActivity(
        frameId: 'z', exposure: Duration.zero, startedAt: start, kind: 'bias');
    expect(zero.kindLabel, 'Bias');
  });
}
