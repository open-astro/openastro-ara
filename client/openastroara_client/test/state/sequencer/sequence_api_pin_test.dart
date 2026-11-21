import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

class _Client implements SequenceClient {
  bool closed = false;
  @override
  void close() => closed = true;
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError();
}

/// The hazard behind the "offline draft while connected" failure seen on the
/// rig (2026-10-04): [sequenceApiProvider] is autoDispose, so a plain read
/// with no listener lets Riverpod dispose it — closing its Dio — at the next
/// turn, and a create that captured the client before awaiting settings then
/// fails with "adapter was closed". createImagingRun pins the provider with a
/// listen for the whole create; this pins the behaviour that fix relies on.
void main() {
  ProviderContainer build(_Client client) => ProviderContainer(overrides: [
        activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'rig.local', port: 5555)),
        sequenceApiFactoryProvider.overrideWithValue((_) => client),
      ]);

  test('a bare read is disposed (and closed) once the event loop turns',
      () async {
    final client = _Client();
    final container = build(client);
    addTearDown(container.dispose);
    expect(container.read(sequenceApiProvider), same(client));
    await Future<void>.delayed(Duration.zero);
    expect(client.closed, isTrue);
  });

  test('a listen keeps the client open across awaits until it is closed',
      () async {
    final client = _Client();
    final container = build(client);
    addTearDown(container.dispose);
    final keep = container.listen(sequenceApiProvider, (_, _) {});
    expect(container.read(sequenceApiProvider), same(client));
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(client.closed, isFalse);
    keep.close();
    await Future<void>.delayed(Duration.zero);
    expect(client.closed, isTrue);
  });
}
