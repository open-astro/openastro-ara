import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/time_sync_state.dart';

class _FakeSavedServerService implements SavedServerService {
  final List<AraServer> servers;
  _FakeSavedServerService(this.servers);
  @override
  Future<List<AraServer>> loadAll() async => servers;
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeTimeSyncClient implements TimeSyncClient {
  _FakeTimeSyncClient(this.state);
  TimeSyncState state;
  bool throwOnGet = false;
  int pushes = 0;
  DateTime? lastPushed;

  @override
  Future<TimeSyncState> getState() async {
    if (throwOnGet) throw StateError('daemon unreachable');
    return state;
  }

  @override
  Future<void> pushClientTime(DateTime utcNow) async {
    pushes++;
    lastPushed = utcNow;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(_FakeTimeSyncClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [_server])),
    timeSyncApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  test('an unsynced daemon gets the device clock pushed', () async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false));
    final c = _container(api);
    await c.read(savedServersProvider.future);

    final onConnect = c.read(timeSyncOnConnectProvider)
      ..now = () => DateTime.utc(2026, 7, 11, 7, 0, 0);
    await onConnect.syncIfNeeded();

    expect(api.pushes, 1);
    expect(api.lastPushed, DateTime.utc(2026, 7, 11, 7, 0, 0));
  });

  test('a fresh, trustworthy sync is left alone (§31.1 top branch)', () async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: true, source: 'gps-internal', trust: 'high'));
    final c = _container(api);
    await c.read(savedServersProvider.future);

    await c.read(timeSyncOnConnectProvider).syncIfNeeded();

    expect(api.pushes, 0, reason: 'a fresh high-trust sync must not be overwritten by a medium one');
  });

  test('a failed state read is swallowed — the push is best-effort', () async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false))..throwOnGet = true;
    final c = _container(api);
    await c.read(savedServersProvider.future);

    // Must not throw: an opportunistic sync never surfaces an error.
    await c.read(timeSyncOnConnectProvider).syncIfNeeded();
    expect(api.pushes, 0);
  });
}
