import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/services/bahtinov_focus_api.dart';
import 'package:openastroara/services/guide_focus_api.dart' show GuideFocusFrame;
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';

/// Serves one status and one frame; the frame's seq can race ahead.
class _FakeClient implements BahtinovFocusClient {
  final BahtinovFocusStatus snapshot;
  final int frameSeq;
  _FakeClient(this.snapshot, this.frameSeq);

  @override
  Future<BahtinovFocusStatus> status() async => snapshot;
  @override
  Future<GuideFocusFrame?> fetchFrame() async => GuideFocusFrame(Uint8List.fromList([1, 2, 3]), frameSeq);
  @override
  Future<void> start({required double exposureSec}) async {}
  @override
  Future<void> stop() async {}
  @override
  void close() {}
}

BahtinovFocusStatus _status(int seq) => BahtinovFocusStatus.fromJson({
      'active': true,
      'state': 'running',
      'seq': seq,
      'has_frame': true,
      'latest': {
        'seq': seq,
        'detected': true,
        'offset_px': 1.0,
        'overlay': {
          'crop_size': 256,
          'intersection_x': 128,
          'intersection_y': 128,
          'lines': [
            {'role': 'outer', 'x1': 0, 'y1': 0, 'x2': 256, 'y2': 256},
            {'role': 'central', 'x1': 0, 'y1': 10, 'x2': 256, 'y2': 246},
            {'role': 'outer', 'x1': 256, 'y1': 0, 'x2': 0, 'y2': 256},
          ],
        },
      },
    });

Future<BahtinovFocusLive> _settle(ProviderContainer c) async {
  c.read(bahtinovFocusProvider);
  await c.read(bahtinovFocusProvider.notifier).refresh();
  return c.read(bahtinovFocusProvider);
}

void main() {
  test('the overlay rides with the frame of its own sample', () async {
    final client = _FakeClient(_status(5), 5);
    final c = ProviderContainer(overrides: [bahtinovFocusApiProvider.overrideWithValue(client)]);
    addTearDown(c.dispose);
    final live = await _settle(c);
    expect(live.frameSeq, 5);
    expect(live.overlay, isNotNull);
  });

  test('a frame that raced ahead of the status shows without lines', () async {
    final client = _FakeClient(_status(5), 6);
    final c = ProviderContainer(overrides: [bahtinovFocusApiProvider.overrideWithValue(client)]);
    addTearDown(c.dispose);
    final live = await _settle(c);
    expect(live.frameSeq, 6);
    expect(live.overlay, isNull);
  });

  test('starting a session keeps the card on Bahtinov', () async {
    final client = _FakeClient(_status(1), 1);
    final c = ProviderContainer(overrides: [bahtinovFocusApiProvider.overrideWithValue(client)]);
    addTearDown(c.dispose);
    expect(c.read(mainFocusMethodProvider), isNull);
    await c.read(bahtinovFocusProvider.notifier).start(exposureSec: 1);
    expect(c.read(mainFocusMethodProvider), MainFocusMethod.bahtinov);
  });
}
