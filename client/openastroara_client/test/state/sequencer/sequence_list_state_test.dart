import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// Fake whose `list()` returns a future the test completes by hand, so the
/// refresh-race ordering is fully controllable. Lifecycle ops are unused here.
class _FakeSeqClient implements SequenceClient {
  @override
  Future<bool> deleteSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  final List<Completer<SequencePage>> calls = [];

  @override
  Future<SequencePage> list({int limit = 50}) {
    final c = Completer<SequencePage>();
    calls.add(c);
    return c.future;
  }

  SequenceRunStateInfo? runState;
  // Per-id override (for cross-sequence races); falls back to [runState].
  Map<String, SequenceRunStateInfo?>? runStateById;
  // When set, getRunState awaits this gate so a test can hold the read in flight
  // (to exercise the "skip WS while loading" / stale-write paths).
  Completer<SequenceRunStateInfo?>? runStateGate;
  // When true, getRunState throws — to exercise the refresh-failure path.
  bool throwOnRead = false;
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async {
    if (throwOnRead) throw Exception('transient');
    // The gate only holds the read in flight; its completion value is unused.
    if (runStateGate != null) await runStateGate!.future;
    return runStateById != null ? runStateById![id] : runState;
  }
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f, {bool treatWarningsAsErrors = false}) async => const SequenceImportResult(createdSequenceId: 'new');
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: id, body: const {});
  @override
  Future<SequenceDetail> updateSequence(String id,
          {String? name, String? description, Map<String, dynamic>? body}) async =>
      SequenceDetail(id: id, name: name ?? id, description: description, body: body ?? const {});
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
  Future<String> skipCurrent(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<SequenceShareExport> exportShare(String id) async => throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description}) async =>
      'new-seq';
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
        'instructions_completed': 12,
        'instructions_total': 60,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.instructionsCompleted, 12);
      expect(info?.instructionsTotal, 60);
      expect(info?.currentInstructionIndex, 3);
    });

    test('a frame naming a different sequence is ignored', () async {
      final (container, controller, _) = await setup('seq-1');
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'other',
        'state': 'running',
        'instructions_completed': 7,
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

    test('a frame arriving while loading is skipped; tracking resumes once loaded',
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
        'instructions_completed': 5,
      }));
      await pumpEventQueue();
      expect(container.read(sequenceRunStateProvider).isLoading, isTrue);

      // Initial read resolves (no active run yet), THEN a fresh frame applies.
      gate.complete(null);
      await container.read(sequenceRunStateProvider.future);
      controller.add(event(SequenceWsEvents.progress, const {
        'sequence_id': 'seq-1',
        'state': 'running',
        'instructions_completed': 9,
        'instructions_total': 30,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.instructionsCompleted, 9);
      expect(info?.instructionsTotal, 30);
    });

    test('a terminal frame re-reads to backfill REST-only fields', () async {
      final (container, controller, fake) = await setup('seq-1');
      // The retained terminal DTO the post-event refresh() will fetch — it
      // carries completedUtc + target name that the WS frame omits.
      fake.runState = SequenceRunStateInfo(
        sequenceId: 'seq-1',
        runId: 'run-9',
        state: SequenceRunState.completed,
        currentTargetName: 'M31',
        completedUtc: DateTime.utc(2026, 6, 18, 10),
        instructionsCompleted: 60,
        instructionsTotal: 60,
      );
      controller.add(event(SequenceWsEvents.complete, const {
        'sequence_id': 'seq-1',
        'state': 'completed',
        'instructions_completed': 60,
        'instructions_total': 60,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.completed);
      // Backfilled by the one-shot refresh, not present on the WS frame.
      expect(info?.completedUtc, DateTime.utc(2026, 6, 18, 10));
      expect(info?.currentTargetName, 'M31');
    });

    test('a sequence.started frame backfills startedUtc via refresh', () async {
      final (container, controller, fake) = await setup('seq-1');
      // An externally-started run: the running DTO the post-event refresh fetches
      // carries startedUtc/target that the WS frame omits.
      fake.runState = SequenceRunStateInfo(
        sequenceId: 'seq-1',
        runId: 'run-9',
        state: SequenceRunState.running,
        currentTargetName: 'M31',
        startedUtc: DateTime.utc(2026, 6, 18, 9),
        instructionsTotal: 60,
      );
      controller.add(event(SequenceWsEvents.started, const {
        'sequence_id': 'seq-1',
        'state': 'running',
        'instructions_completed': 0,
        'instructions_total': 60,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.startedUtc, DateTime.utc(2026, 6, 18, 9)); // backfilled
      expect(info?.currentTargetName, 'M31');
    });

    test('a refresh in flight does not write back after the selection changes',
        () async {
      final (container, controller, fake) = await setup('seq-1');
      // The refresh's read hangs; seq-1 would resolve to a terminal DTO, seq-2 to
      // a fresh (null) state.
      final terminal = SequenceRunStateInfo(
          sequenceId: 'seq-1', state: SequenceRunState.completed);
      fake
        ..runStateById = {'seq-1': terminal, 'seq-2': null}
        ..runStateGate = Completer<SequenceRunStateInfo?>();

      // Start a refresh for seq-1 (read hangs), then switch to seq-2.
      final pending = container.read(sequenceRunStateProvider.notifier).refresh();
      container.read(selectedSequenceIdProvider.notifier).select('seq-2');
      fake.runStateGate!.complete(null); // release both in-flight reads
      await pending;
      await pumpEventQueue();

      // seq-1's terminal state must NOT have landed on seq-2 (the guard dropped
      // the stale write); seq-2 shows its own (null) state.
      expect(container.read(sequenceRunStateProvider).value, isNull);
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
        'instructions_completed': 21,
        'instructions_total': 60,
      }));
      await pumpEventQueue();

      final info = container.read(sequenceRunStateProvider).value;
      expect(info?.state, SequenceRunState.running);
      expect(info?.instructionsCompleted, 21);
    });
  });
}
