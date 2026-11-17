import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/time_sync_state.dart';

// The daemon's GET carries no clock_set flag: a clock the daemon could not set
// stays off by its tracked offset (positive = the daemon clock is slow).

class _SavedServers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [
    AraServer(hostname: 'h', port: 5555),
  ];
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _Clock implements TimeSyncClient {
  _Clock(this.offset);
  final double offset;
  @override
  Future<TimeSyncState> getState() async =>
      TimeSyncState(synced: true, systemTimeOffsetSeconds: offset);
  @override
  Future<void> pushClientTime(DateTime utcNow) async {}
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Seq implements SequenceClient {
  var starts = 0;
  @override
  Future<String> start(String id) async {
    starts++;
    return id;
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async =>
      const SequencePage(items: []);
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Queued extends SimpleRunNotifier {
  @override
  SimpleRun build() => const SimpleRun(sequenceId: 'seq-1');
}

({ProviderContainer c, _Seq seq}) _rig(double offset) {
  final seq = _Seq();
  final c = ProviderContainer(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_SavedServers()),
      timeSyncApiFactoryProvider.overrideWithValue((_) => _Clock(offset)),
      sequenceApiProvider.overrideWithValue(seq),
      simpleRunProvider.overrideWith(_Queued.new),
    ],
  );
  addTearDown(c.dispose);
  return (c: c, seq: seq);
}

Future<List<SimpleMessage>> _questions(ProviderContainer c) async {
  await c.read(savedServersProvider.future);
  await c.read(simpleRunProvider.notifier).startQueue();
  return c
      .read(simpleMessagesProvider)
      .where((m) => m.kind == SimpleMessageKind.question)
      .toList();
}

void main() {
  test('Start on a wrong clock asks; Start anyway starts the run', () async {
    final r = _rig(120);
    final q = await _questions(r.c);
    expect(q, hasLength(1));
    expect(q.single.actions, hasLength(2));
    expect(r.seq.starts, 0, reason: 'nothing started before the answer');
    q.single.actions.last.onPressed();
    await Future<void>.delayed(Duration.zero);
    expect(r.seq.starts, 1);
  });

  test(
    'Start on a wrong clock asks; Not now leaves the run unstarted',
    () async {
      final r = _rig(120);
      final q = await _questions(r.c);
      expect(q, hasLength(1));
      q.single.actions.first.onPressed();
      await Future<void>.delayed(Duration.zero);
      expect(r.seq.starts, 0);
      expect(r.c.read(simpleRunProvider).started, isFalse);
    },
  );

  // GUARDS: pass on current code.
  test(
    'GUARD: a clock off by under 60 s raises no notice and no question',
    () async {
      final r = _rig(30);
      final q = await _questions(r.c);
      expect(q, isEmpty);
      expect(r.seq.starts, 1);
    },
  );

  test('GUARD: a clock set (offset 0) starts without a question', () async {
    final r = _rig(0);
    final q = await _questions(r.c);
    expect(q, isEmpty);
    expect(r.seq.starts, 1);
  });
}
