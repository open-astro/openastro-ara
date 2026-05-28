import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';

/// In-memory fake for SavedServerService that lets tests control whether
/// `add` throws (simulating the Linux-no-keyring case from cleanup-1).
class _FakeSavedServerService implements SavedServerService {
  final List<AraServer> _stored;
  bool throwOnAdd;

  _FakeSavedServerService({
    List<AraServer> initial = const <AraServer>[],
    this.throwOnAdd = false,
  }) : _stored = [...initial];

  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);

  @override
  Future<void> saveAll(List<AraServer> servers) async {
    _stored
      ..clear()
      ..addAll(servers);
  }

  @override
  Future<void> add(AraServer server) async {
    if (throwOnAdd) throw StateError('keyring unavailable');
    if (_stored.contains(server)) return;
    _stored.add(server);
  }

  @override
  // ignore: unused_element
  dynamic noSuchMethod(Invocation invocation) =>
      super.noSuchMethod(invocation);
}

void main() {
  group('SavedServersNotifier', () {
    test('build returns persisted list', () async {
      final fake = _FakeSavedServerService(initial: const [
        AraServer(hostname: 'host-a', port: 5555),
      ]);
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(fake),
      ]);
      addTearDown(container.dispose);
      // Trigger build by reading.
      final servers = await container.read(savedServersProvider.future);
      expect(servers, hasLength(1));
      expect(servers.first.hostname, 'host-a');
    });

    test('add appends to state on success', () async {
      final fake = _FakeSavedServerService();
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(fake),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      await container.read(savedServersProvider.notifier).add(
            const AraServer(hostname: 'host-x', port: 5555),
          );
      final after = await container.read(savedServersProvider.future);
      expect(after, hasLength(1));
      expect(after.first.hostname, 'host-x');
    });

    test('add keeps in-memory state when persistence throws (cleanup-1 contract)',
        () async {
      final fake = _FakeSavedServerService(throwOnAdd: true);
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(fake),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      await container.read(savedServersProvider.notifier).add(
            const AraServer(hostname: 'host-failing', port: 5555),
          );
      // Even though the service throws, the notifier should publish the
      // new state so the router unblocks the FirstRunScreen.
      final state = container.read(savedServersProvider);
      expect(state.hasValue, isTrue);
      expect(state.value, hasLength(1));
      expect(state.value!.first.hostname, 'host-failing');
    });
  });
}
