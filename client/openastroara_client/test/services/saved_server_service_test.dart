import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';

// The real service against the package's in-memory mock backend — the
// move-to-end contract behind activeServerProvider ("last-confirmed = active")
// lives here, so it gets tested on the real persistence round-trip.

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
}
