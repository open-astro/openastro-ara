import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/sequencer/run_latest_frame_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _FakeRun extends SequenceRunStateNotifier {
  _FakeRun(this._v);
  final SequenceRunStateInfo? _v;
  @override
  Future<SequenceRunStateInfo?> build() async => _v;
}

WsEvent _frame(String id) => WsEvent(
    type: 'frame.complete',
    ts: DateTime.now().toUtc(),
    seq: 1,
    payload: {'frame_id': id, 'filter_name': 'Ha'});

void main() {
  test('frame.complete during an active run updates; idle frames ignored',
      () async {
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    final container = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
      sequenceRunStateProvider.overrideWith(() => _FakeRun(
          const SequenceRunStateInfo(
              runId: 'r1', state: SequenceRunState.running))),
    ]);
    addTearDown(container.dispose);

    // Materialize + let the fake run state land.
    container.listen(runLatestFrameProvider, (_, _) {});
    await container.read(sequenceRunStateProvider.future);
    ws.add(_frame('f-1'));
    await Future<void>.delayed(Duration.zero);
    expect(container.read(runLatestFrameProvider)?.frameId, 'f-1');
    expect(container.read(runLatestFrameProvider)?.filterName, 'Ha');
  });

  test('no active run → frames ignored', () async {
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    final container = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
      sequenceRunStateProvider.overrideWith(() => _FakeRun(null)),
    ]);
    addTearDown(container.dispose);
    container.listen(runLatestFrameProvider, (_, _) {});
    await container.read(sequenceRunStateProvider.future);
    ws.add(_frame('f-1'));
    await Future<void>.delayed(Duration.zero);
    expect(container.read(runLatestFrameProvider), isNull);
  });
}
