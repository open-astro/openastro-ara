import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/sequencer/run_event_ticker.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// A run-state notifier the test can push new values through.
class _PushableRun extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => null;
  void push(SequenceRunStateInfo v) => state = AsyncData(v);
}

void main() {
  late StreamController<WsEvent> ws;
  late ProviderContainer container;
  late _PushableRun run;

  setUp(() async {
    ws = StreamController<WsEvent>.broadcast();
    run = _PushableRun();
    container = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
      sequenceRunStateProvider.overrideWith(() => run),
    ]);
    container.listen(runEventTickerProvider, (_, _) {});
    await container.read(sequenceRunStateProvider.future);
  });

  tearDown(() async {
    container.dispose();
    await ws.close();
  });

  SequenceRunStateInfo info(SequenceRunState s,
          {String runId = 'r1', String? instruction}) =>
      SequenceRunStateInfo(
          runId: runId, state: s, currentInstructionDescription: instruction);

  test('state transitions + instruction changes land, newest first', () {
    run.push(info(SequenceRunState.running, instruction: 'Slew'));
    run.push(info(SequenceRunState.running, instruction: 'Take Exposure'));
    final events = container.read(runEventTickerProvider);
    expect(events.map((e) => e.label).toList(),
        ['▶ Take Exposure', '▶ Slew', 'Run started']);
  });

  test('pause then running reads Resumed; a new run clears the buffer', () {
    run.push(info(SequenceRunState.running));
    run.push(info(SequenceRunState.paused));
    run.push(info(SequenceRunState.running));
    expect(container.read(runEventTickerProvider).first.label, 'Resumed');

    run.push(info(SequenceRunState.running, runId: 'r2'));
    final events = container.read(runEventTickerProvider);
    expect(events, hasLength(1));
    expect(events.single.label, 'Run started');
  });

  test('frame.complete during a run appends with its filter', () async {
    run.push(info(SequenceRunState.running));
    ws.add(WsEvent(
        type: 'frame.complete',
        ts: DateTime.now().toUtc(),
        seq: 1,
        payload: const {'frame_id': 'f1', 'filter_name': 'OIII'}));
    await Future<void>.delayed(Duration.zero);
    expect(container.read(runEventTickerProvider).first.label,
        'Frame captured · OIII');
  });

  test('ring buffer caps at 50', () {
    run.push(info(SequenceRunState.running));
    for (var i = 0; i < 80; i++) {
      run.push(info(SequenceRunState.running, instruction: 'step $i'));
    }
    expect(container.read(runEventTickerProvider).length,
        RunEventTickerNotifier.cap);
  });
}
