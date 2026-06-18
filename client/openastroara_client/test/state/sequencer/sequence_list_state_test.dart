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
  // When set, getRunState awaits this gate so a test can hold the initial REST
  // read in flight (to exercise the "skip WS while loading" path).
  Completer<SequenceRunStateInfo?>? runStateGate;
  // When true, getRunState throws — to exercise the refresh-failure path.
  bool throwOnRead = false;
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) {
    if (throwOnRead) return Future.error(Exception('transient'));
    return runStateGate?.future ?? Future.value(runState);
  }
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

    Future<(ProviderContainer, StreamController<WsEvent>, _FakeSeqClient)> setup(
        String selectId,
        {bool awaitInitial = true,
        Completer<SequenceRunStateInfo?>? gate}) async {
      final controller = StreamController<WsEvent>.broadcast();
      addTearDown(controller.close);
      final fake = _FakeSeqClient()..runStateGate = gate;
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(fake),
        wsEventsProvider.overrideWith((ref) => controller.stream),
      ]);
      addTearDown(container.dispose);
      container.read(selectedSequenceIdProvider.notifier).select(selectId);
      final sub = container.listen(sequenceRunStateProvider, (_, _) {});
      addTearDown(sub.close);
      if (awaitInitial) {
        await container.read(sequenceRunStateProvider.future); // initial (null)
      }
      return (container, controller, fake);
    }

    test('a sequence.progress frame for the selected sequence updates run state',
        () async {
      final (container, controller, _) = await setup('seq-1');
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'seq-1',
        'run_id': 'run-9',
        'state': 'running',
        'current_instruction_index': 3,
        'frames_completed': 12,
        'frames_total': 60,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.framesCompleted, 12);
      expect(info?.framesTotal, 60);
      expect(info?.currentInstructionIndex, 3);
    });

    test('a frame naming a different sequence is ignored', () async {
      final (container, controller, _) = await setup('seq-1');
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'other',
        'state': 'running',
        'frames_completed': 7,
      }));
      await pumpEventQueue();

      expect(container.read(sequenceRunStateProvider).value, isNull);
    });

    test('a non-sequence event is ignored', () async {
      final (container, controller, _) = await setup('seq-1');
      controller.add(event('diagnostics.issue_detected', const {
        'sequence_id': 'seq-1',
        'state': 'running',
      }));
      await pumpEventQueue();

      expect(container.read(sequenceRunStateProvider).value, isNull);
    });

    test('a frame arriving while the initial read is in flight is not lost',
        () async {
      // Hold the initial getRunState open so the provider stays loading.
      final gate = Completer<SequenceRunStateInfo?>();
      final (container, controller, _) =
          await setup('seq-1', awaitInitial: false, gate: gate);
      // First WS frame lands DURING loading — must be skipped (it would be
      // clobbered by the resolving build() future).
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'seq-1',
        'state': 'running',
        'frames_completed': 5,
      }));
      await pumpEventQueue();
      expect(container.read(sequenceRunStateProvider).isLoading, isTrue);

      // Initial read resolves (no active run yet), THEN a fresh frame applies.
      gate.complete(null);
      await container.read(sequenceRunStateProvider.future);
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'seq-1',
        'state': 'running',
        'frames_completed': 9,
        'frames_total': 30,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.framesCompleted, 9);
      expect(info?.framesTotal, 30);
    });

    test('a refresh() failure does not freeze live WS tracking', () async {
      final (container, controller, fake) = await setup('seq-1');
      // A refresh fails (e.g. a network hiccup after pressing Pause). The last
      // good value is retained (error logged, not promoted to a value-less
      // AsyncError), so hasValue stays true and the WS guard keeps applying.
      fake.throwOnRead = true;
      await container.read(sequenceRunStateProvider.notifier).refresh();
      expect(container.read(sequenceRunStateProvider).hasValue, isTrue);

      // A subsequent WS frame must still apply (not be dropped by the guard).
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'seq-1',
        'state': 'running',
        'frames_completed': 21,
        'frames_total': 60,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.framesCompleted, 21);
    });
  });
}
