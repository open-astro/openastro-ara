import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// Fake whose `list()` returns a future the test completes by hand, so the
/// refresh-race ordering is fully controllable. Lifecycle ops are unused here.
class _FakeSeqClient implements SequenceClient {
  final List<Completer<SequencePage>> calls = [];

  @override
  Future<SequencePage> list({int limit = 50}) {
    final c = Completer<SequencePage>();
    calls.add(c);
    return c.future;
  }

  SequenceRunStateInfo? runState;
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f, {bool treatWarningsAsErrors = false}) async => const SequenceImportResult(createdSequenceId: 'new');
  @override
  Future<SequenceNode> getSequence(String id) async => SequenceNode(
      id: 'root', kind: SequenceNodeKind.root, displayName: 'fake');
  @override
  Future<String> start(String id) async => 'op';
  @override
  Future<String> pause(String id) async => 'op';
  @override
  Future<String> resume(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  void close() {}
}

SequenceListItem _item(String id) => SequenceListItem(id: id, name: id);

void main() {
  test('refresh() is last-issued-wins: a slow older response cannot overwrite a newer one',
      () async {
    final fake = _FakeSeqClient();
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(fake),
    ]);
    addTearDown(container.dispose);

    // Initial build() issues calls[0] synchronously; complete it FIRST, then
    // await the resulting data.
    final sub = container.listen(sequenceListProvider, (_, _) {});
    addTearDown(sub.close);
    expect(fake.calls.length, 1);
    fake.calls[0].complete(SequencePage(items: [_item('initial')]));
    await container.read(sequenceListProvider.future);

    final notifier = container.read(sequenceListProvider.notifier);

    // Two rapid refreshes: calls[1] (older) and calls[2] (newer) both in flight.
    final older = notifier.refresh();
    final newer = notifier.refresh();
    expect(fake.calls.length, 3);

    // Newer completes first, then the older (slower) one resolves afterwards.
    fake.calls[2].complete(SequencePage(items: [_item('newer')]));
    fake.calls[1].complete(SequencePage(items: [_item('older')]));
    await Future.wait([older, newer]);

    // The older response must NOT have clobbered the newer one.
    expect(container.read(sequenceListProvider).value, [_item('newer')]);
  });

  group('sequenceRunStateProvider live WS updates', () {
    WsEvent event(String type, Map<String, dynamic> payload) =>
        WsEvent(type: type, ts: DateTime.utc(2026), seq: 1, payload: payload);

    Future<(ProviderContainer, StreamController<WsEvent>)> setup(
        String selectId) async {
      final controller = StreamController<WsEvent>.broadcast();
      addTearDown(controller.close);
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(_FakeSeqClient()),
        wsEventsProvider.overrideWith((ref) => controller.stream),
      ]);
      addTearDown(container.dispose);
      container.read(selectedSequenceIdProvider.notifier).select(selectId);
      final sub = container.listen(sequenceRunStateProvider, (_, _) {});
      addTearDown(sub.close);
      await container.read(sequenceRunStateProvider.future); // initial (null)
      return (container, controller);
    }

    test('a sequence.progress frame for the selected sequence updates run state',
        () async {
      final (container, controller) = await setup('seq-1');
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'seq-1',
        'run_id': 'run-9',
        'state': 'running',
        'current_instruction_index': 3,
        'frames_completed': 12,
        'frames_total': 60,
      }));
      await Future<void>.delayed(Duration.zero); // let the stream deliver

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.framesCompleted, 12);
      expect(info?.framesTotal, 60);
      expect(info?.currentInstructionIndex, 3);
    });

    test('a frame naming a different sequence is ignored', () async {
      final (container, controller) = await setup('seq-1');
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'other',
        'state': 'running',
        'frames_completed': 7,
      }));
      await Future<void>.delayed(Duration.zero);

      expect(container.read(sequenceRunStateProvider).value, isNull);
    });

    test('a non-sequence event is ignored', () async {
      final (container, controller) = await setup('seq-1');
      controller.add(event('diagnostics.issue_detected', const {
        'sequence_id': 'seq-1',
        'state': 'running',
      }));
      await Future<void>.delayed(Duration.zero);

      expect(container.read(sequenceRunStateProvider).value, isNull);
    });
  });
}
