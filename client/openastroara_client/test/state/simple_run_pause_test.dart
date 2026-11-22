import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_run_state.dart';

/// A daemon whose Pause always fails (a long step holds the request).
class _Api implements SequenceClient {
  var pauses = 0;
  @override
  Future<String> pause(String id) async {
    pauses++;
    throw DioException(
      requestOptions: RequestOptions(path: '/pause'),
      type: DioExceptionType.receiveTimeout,
    );
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => _running;

  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async =>
      const SequencePage(items: []);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// A daemon that takes every pause and remembers which kind (#2172).
class _OkApi implements SequenceClient {
  final calls = <String>[];
  @override
  Future<String> pause(String id) async => (calls..add('pause')).last;

  @override
  Future<String> pauseAfterFrame(String id) async =>
      (calls..add('pauseAfterFrame')).last;

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => _running;

  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async =>
      const SequencePage(items: []);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

Future<(ProviderContainer, _OkApi)> _okRun() async {
  final api = _OkApi();
  final c = ProviderContainer(
    overrides: [sequenceApiProvider.overrideWithValue(api)],
  );
  c.read(selectedSequenceIdProvider.notifier).select('s1');
  await c.read(sequenceRunStateProvider.future);
  return (c, api);
}

const _running = SequenceRunStateInfo(
  sequenceId: 's1',
  state: SequenceRunState.running,
);

void main() {
  test(
    'Pause tapped again after a failed pause leaves one error pill',
    () async {
      final api = _Api();
      final c = ProviderContainer(
        overrides: [sequenceApiProvider.overrideWithValue(api)],
      );
      addTearDown(c.dispose);
      c.read(selectedSequenceIdProvider.notifier).select('s1');
      await c.read(sequenceRunStateProvider.future);

      final run = c.read(simpleRunProvider.notifier);
      await run.pause();
      await run.pause();
      await run.pause();

      expect(api.pauses, 3, reason: 'each tap reached the daemon');
      final errors = c
          .read(simpleMessagesProvider)
          .where((m) => m.kind == SimpleMessageKind.error);
      expect(errors, hasLength(1));
    },
  );

  test('the shutter pauses now', () async {
    final (c, api) = await _okRun();
    addTearDown(c.dispose);

    await c.read(simpleRunProvider.notifier).pause();

    expect(api.calls, ['pause'], reason: 'the frame in hand stops (#2172)');
    final run = c.read(simpleRunProvider);
    expect((run.pausing, run.pausingAfterFrame), (true, false));
  });

  test('pause after this frame asks the daemon to keep the frame', () async {
    final (c, api) = await _okRun();
    addTearDown(c.dispose);

    await c.read(simpleRunProvider.notifier).pauseAfterFrame();

    expect(api.calls, ['pauseAfterFrame']);
    final run = c.read(simpleRunProvider);
    expect((run.pausing, run.pausingAfterFrame), (true, true));
  });
}
