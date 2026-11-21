import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';

// The move-to-end contract behind activeServerProvider ("last-confirmed =
// active") lives here. Since #1129 the list is session-only: nothing is
// written to the device, and the list older versions stored is wiped.

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  const rigA = AraServer(hostname: 'observatory', port: 8080);
  const rigB = AraServer(hostname: 'travel-rig', port: 8080);

  test('add persists and loadAll round-trips', () async {
    final svc = SavedServerService();
    await svc.add(rigA);
    await svc.add(rigB);
    final loaded = await svc.loadAll();
    expect(loaded.map((s) => s.hostname), ['observatory', 'travel-rig']);
  });

  test('re-confirming a saved server moves it to the end (= active)', () async {
    final svc = SavedServerService();
    await svc.add(rigA);
    await svc.add(rigB);
    // The user reconnects to the observatory — it must become the active
    // (last) entry, not stay shadowed by the travel rig.
    await svc.add(rigA);
    final loaded = await svc.loadAll();
    expect(loaded.map((s) => s.hostname), ['travel-rig', 'observatory']);
    expect(loaded, hasLength(2), reason: 'a re-add must never duplicate');
  });

  test('re-adding refreshes stored metadata (version/mDNS can change)', () async {
    final svc = SavedServerService();
    await svc.add(const AraServer(
        hostname: 'observatory', port: 8080, serverVersion: '0.0.1'));
    // Same identity (host:port), newer handshake metadata.
    await svc.add(const AraServer(
        hostname: 'observatory',
        port: 8080,
        mdnsName: 'ara-obs',
        serverVersion: '0.0.2'));
    final loaded = await svc.loadAll();
    expect(loaded, hasLength(1));
    expect(loaded.single.serverVersion, '0.0.2');
    expect(loaded.single.mdnsName, 'ara-obs');
  });

  test('a bare manual re-entry keeps earlier-recorded metadata', () async {
    // A manual add types only host:port; that re-confirmation must not blank
    // the mDNS name / version a richer earlier confirmation stored.
    final svc = SavedServerService();
    await svc.add(const AraServer(
        hostname: 'observatory',
        port: 8080,
        mdnsName: 'ara-obs',
        serverVersion: '0.0.2'));
    await svc.add(const AraServer(hostname: 'observatory', port: 8080));
    final loaded = await svc.loadAll();
    expect(loaded, hasLength(1));
    expect(loaded.single.mdnsName, 'ara-obs');
    expect(loaded.single.serverVersion, '0.0.2');
  });

  // #1129: a rig's address (and name) can change every night, so nothing is
  // remembered between launches — every launch scans.
  test('confirming a rig writes nothing to the device', () async {
    final svc = SavedServerService();
    await svc.add(rigA);
    expect(await const FlutterSecureStorage().readAll(), isEmpty);
  });

  test('a new launch starts with no rigs', () async {
    await SavedServerService().add(rigA);
    expect(await SavedServerService().loadAll(), isEmpty);
  });

  test('the address list older versions stored is wiped on first load', () async {
    // What an older version of the app left on the device (test data only).
    FlutterSecureStorage.setMockInitialValues({
      SavedServerService.legacyStorageKey: '[{"hostname":"old-rig.test","port":5555}]',
      'unrelated.key': 'kept',
    });
    final loaded = await SavedServerService().loadAll();
    expect(loaded, isEmpty, reason: 'an old saved address is never used');
    final left = await const FlutterSecureStorage().readAll();
    expect(left.containsKey(SavedServerService.legacyStorageKey), isFalse);
    expect(left['unrelated.key'], 'kept');
  });
}
