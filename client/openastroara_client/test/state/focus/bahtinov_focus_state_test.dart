import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/services/bahtinov_focus_api.dart';
import 'package:openastroara/services/guide_focus_api.dart' show GuideFocusFrame;
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';

/// Serves one status and one frame; the frame's seq can race ahead.
class _FakeClient implements BahtinovFocusClient {
  final BahtinovFocusStatus snapshot;
  final int frameSeq;
  int fetches = 0;
  _FakeClient(this.snapshot, this.frameSeq);

  @override
  Future<BahtinovFocusStatus> status() async => snapshot;
  @override
  Future<GuideFocusFrame?> fetchFrame() async {
    fetches++;
    return GuideFocusFrame(Uint8List.fromList([1, 2, 3]), frameSeq);
  }
  @override
  Future<void> start({required double exposureSec}) async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> pick({required int x, required int y}) async {}
  @override
  Future<void> clearPick() async {}
  @override
  void close() {}
}

BahtinovFocusStatus _status(int seq, {int? frameSeq}) => BahtinovFocusStatus.fromJson({
      'active': true,
      'state': 'running',
      'seq': seq,
      'has_frame': true,
      'frame_seq': frameSeq ?? seq,
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

class _Focuser extends FocuserNotifier {
  _Focuser(this.connected);
  final bool connected;
  @override
  Future<FocuserStatus?> build() async => connected
      ? FocuserStatus(
          deviceId: 'f',
          name: 'Focuser',
          connectionState: EquipmentConnectionState.connected,
          capabilities: const FocuserCapabilities(
              minPosition: 0, maxPosition: 1000, stepSizeUm: 1, canTempComp: false, absoluteFocuser: true),
          runtimeState: 'idle',
          position: 500,
          temperature: null,
          tempCompEnabled: false,
        )
      : null;
}

class _Pick extends MainFocusMethodNotifier {
  _Pick(this.method);
  final MainFocusMethod method;
  @override
  MainFocusMethod? build() => method;
}

/// The focus method both presentations show, with the readout [running] or
/// not, a focuser [connected] or not, and the user's [pick] if any.
Future<MainFocusMethod> _effective({required bool running, required bool connected, MainFocusMethod? pick}) async {
  final c = ProviderContainer(overrides: [
    if (running) bahtinovFocusApiProvider.overrideWithValue(_FakeClient(_status(1), 1)),
    focuserProvider.overrideWith(() => _Focuser(connected)),
    if (pick != null) mainFocusMethodProvider.overrideWith(() => _Pick(pick)),
  ]);
  addTearDown(c.dispose);
  await c.read(focuserProvider.future);
  if (running) await _settle(c);
  return c.read(effectiveFocusMethodProvider);
}

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

  test('a sample whose frame failed to render does not re-fetch the old frame', () async {
    // The daemon is on sample 7 but its picture is still sample 5's.
    final client = _FakeClient(_status(7, frameSeq: 5), 5);
    final c = ProviderContainer(overrides: [bahtinovFocusApiProvider.overrideWithValue(client)]);
    addTearDown(c.dispose);
    await _settle(c);
    final fetched = client.fetches;
    expect(fetched, greaterThan(0));
    expect(c.read(bahtinovFocusProvider).frameSeq, 5);
    await c.read(bahtinovFocusProvider.notifier).refresh();
    await c.read(bahtinovFocusProvider.notifier).refresh();
    expect(client.fetches, fetched, reason: 'the same frame is not fetched again');
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

  group('effectiveFocusMethodProvider', () {
    test('a readout already running wins over a connected focuser and no pick', () async {
      expect(await _effective(running: true, connected: true), MainFocusMethod.bahtinov);
    });
    test('a readout already running wins over a pick of autofocus', () async {
      expect(await _effective(running: true, connected: true, pick: MainFocusMethod.autofocus),
          MainFocusMethod.bahtinov);
    });
    test('the pick wins over the focuser', () async {
      expect(await _effective(running: false, connected: true, pick: MainFocusMethod.bahtinov),
          MainFocusMethod.bahtinov);
      expect(await _effective(running: false, connected: false, pick: MainFocusMethod.autofocus),
          MainFocusMethod.autofocus);
    });
    test('no pick: autofocus with a focuser connected, the mask without', () async {
      expect(await _effective(running: false, connected: true), MainFocusMethod.autofocus);
      expect(await _effective(running: false, connected: false), MainFocusMethod.bahtinov);
    });
  });
}
