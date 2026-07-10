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
    // Mirrors the real service's move-to-end (re-confirmed = active).
    _stored
      ..removeWhere((s) => s == server)
      ..add(server);
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

    test('the in-memory fallback also moves a re-added server to the end',
        () async {
      // Persistence down + re-confirming an already-known server: the
      // fallback must apply the same move-to-end semantics as the service,
      // or the active pick would differ by whether the keyring works.
      final fake = _FakeSavedServerService(
        initial: const [
          AraServer(hostname: 'observatory', port: 8080),
          AraServer(hostname: 'travel-rig', port: 8080),
        ],
        throwOnAdd: true,
      );
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(fake),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      await container.read(savedServersProvider.notifier).add(
            const AraServer(hostname: 'observatory', port: 8080),
          );
      expect(
        container.read(savedServersProvider).value!.map((s) => s.hostname),
        ['travel-rig', 'observatory'],
      );
    });
  });

  group('activeServerProvider', () {
    test('null while empty, the last (most-recently-confirmed) once saved',
        () async {
      final fake = _FakeSavedServerService();
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(fake),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);
      expect(container.read(activeServerProvider), isNull);

      final notifier = container.read(savedServersProvider.notifier);
      await notifier.add(const AraServer(hostname: 'observatory', port: 8080));
      await notifier.add(const AraServer(hostname: 'travel-rig', port: 8080));
      expect(container.read(activeServerProvider)!.hostname, 'travel-rig');

      // Reconnecting the observatory flips the active pick back to it.
      await notifier.add(const AraServer(hostname: 'observatory', port: 8080));
      expect(container.read(activeServerProvider)!.hostname, 'observatory');
    });

    test('the awaitable variant resolves after the initial load', () async {
      final fake = _FakeSavedServerService(initial: const [
        AraServer(hostname: 'observatory', port: 8080),
      ]);
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(fake),
      ]);
      addTearDown(container.dispose);
      // No prior read of savedServersProvider — the future variant must kick
      // off the load itself and resolve to the saved server, not null.
      final server =
          await container.read(activeServerFutureProvider.future);
      expect(server!.hostname, 'observatory');
    });
  });
}
