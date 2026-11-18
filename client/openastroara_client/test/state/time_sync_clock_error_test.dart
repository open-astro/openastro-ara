import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/time_sync_state.dart';

// The daemon's GET carries no clock_set flag: a clock the daemon could not set
// stays off by its tracked offset (positive = the daemon clock is slow).
// Interface the Implement child provides:
//   timeSyncClockErrorProvider: FutureProvider<TimeSyncClockError?>
//   TimeSyncClockError { double seconds (absolute); bool slow }

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

({ProviderContainer c}) _rig(double offset) {
  final c = ProviderContainer(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_SavedServers()),
      timeSyncApiFactoryProvider.overrideWithValue((_) => _Clock(offset)),
    ],
  );
  addTearDown(c.dispose);
  return (c: c);
}

void main() {
  test('a clock 120 s slow that could not be set reads as wrong, by how much, which way', () async {
    final r = _rig(120);
    await r.c.read(savedServersProvider.future);
    final err = await r.c.read(timeSyncClockErrorProvider.future);
    expect(err, isNotNull);
    expect(err!.seconds, 120);
    expect(err.slow, isTrue);
  });

  test('a clock 90 s fast reads as fast', () async {
    final r = _rig(-90);
    await r.c.read(savedServersProvider.future);
    final err = await r.c.read(timeSyncClockErrorProvider.future);
    expect(err!.seconds, 90);
    expect(err.slow, isFalse);
  });
}
