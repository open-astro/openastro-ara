import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

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

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
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
}
