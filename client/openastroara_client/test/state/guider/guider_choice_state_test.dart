import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/guider/guider_choice_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

/// The rig the app talks to, switchable from a test.
class _Rig extends Notifier<AraServer?> {
  @override
  AraServer? build() => const AraServer(hostname: 'rig-a', port: 1);
  void set(AraServer s) => state = s;
}

final _rig = NotifierProvider<_Rig, AraServer?>(_Rig.new);

class _FakeNames extends AlpacaDeviceNamesClient {
  _FakeNames(this.byServer);

  final Map<String, Map<String, AlpacaDeviceInfo>> byServer;
  final asked = <String>[];

  @override
  Future<Map<String, AlpacaDeviceInfo>> fetchDevices(String host, int port) async {
    asked.add('$host:$port');
    return byServer['$host:$port'] ?? const {};
  }

  @override
  void close() {}
}

void main() {
  ProviderContainer container(_FakeNames names) {
    final c = ProviderContainer(overrides: [
      alpacaDeviceNamesApiProvider.overrideWithValue(names),
      activeServerProvider.overrideWith((ref) => ref.watch(_rig)),
    ]);
    addTearDown(c.dispose);
    return c;
  }

  test("a camera pick is pinned by the UniqueID its server reports", () async {
    final names = _FakeNames({
      'rig.lan:6800': {
        'camera/1': const AlpacaDeviceInfo(name: 'ASI290MM Mini', uniqueId: 'cam-uid'),
      },
    });
    final c = container(names);
    await c.read(guiderAlpacaDevicesProvider.notifier).resolve(['rig.lan:6800']);

    final devices = c.read(guiderAlpacaDevicesProvider);
    expect(devices.names['rig.lan:6800|camera/1'], 'ASI290MM Mini');
    expect(c.read(guiderAlpacaDevicesProvider.notifier).uniqueIdFor('Alpaca Camera [rig.lan:6800/1]'),
        'cam-uid');
    expect(c.read(guiderAlpacaDevicesProvider.notifier).uniqueIdFor('Alpaca Camera [rig.lan:6800/2]'), '');
  });

  test('a named server is not asked again; one that answered nothing is', () async {
    final names = _FakeNames({
      'rig.lan:6800': {'camera/1': const AlpacaDeviceInfo(name: 'Cam')},
    });
    final c = container(names);
    final devices = c.read(guiderAlpacaDevicesProvider.notifier);
    await devices.resolve(['rig.lan:6800', 'down.lan:6800']);
    await devices.resolve(['rig.lan:6800', 'down.lan:6800']);

    expect(names.asked.where((e) => e == 'rig.lan:6800'), hasLength(1));
    expect(names.asked.where((e) => e == 'down.lan:6800'), hasLength(2));
  });

  test('a pick on another Alpaca server than the camera is a cross-server pick', () {
    expect(isCrossServerPick('rig.lan:6800', 'Alpaca Telescope [other.lan:11111/0]'), isTrue);
    expect(isCrossServerPick('rig.lan:6800', 'Alpaca Telescope [rig.lan:6800/0]'), isFalse);
    expect(isCrossServerPick(null, 'Alpaca Telescope [other.lan:11111/0]'), isFalse);
    expect(isCrossServerPick('rig.lan:6800', 'EQMOD ASCOM'), isFalse);
  });

  test('a server switch forgets the last rig\'s names and UniqueIDs', () async {
    final names = _FakeNames({
      'localhost:11111': {'camera/0': const AlpacaDeviceInfo(name: 'ASI120', uniqueId: 'rig-a-cam')},
    });
    final c = container(names);
    c.listen(guiderAlpacaDevicesProvider, (_, _) {});
    await c.read(guiderAlpacaDevicesProvider.notifier).resolve(['localhost:11111']);
    expect(c.read(guiderAlpacaDevicesProvider).uniqueIds, isNotEmpty);

    c.read(_rig.notifier).set(const AraServer(hostname: 'rig-b', port: 1));
    expect(c.read(guiderAlpacaDevicesProvider).uniqueIds, isEmpty, reason: 'rig B has its own localhost');
    names.byServer['localhost:11111'] = {'camera/0': const AlpacaDeviceInfo(name: 'ASI290', uniqueId: 'rig-b-cam')};
    await c.read(guiderAlpacaDevicesProvider.notifier).resolve(['localhost:11111']);
    expect(c.read(guiderAlpacaDevicesProvider.notifier).uniqueIdFor('Alpaca Camera [localhost:11111/0]'), 'rig-b-cam');
  });

  test('an explicit refresh asks a named server again, so a swapped camera is pinned by its own UniqueID', () async {
    final names = _FakeNames({
      'rig.lan:6800': {'camera/0': const AlpacaDeviceInfo(name: 'Old', uniqueId: 'old-cam')},
    });
    final c = container(names);
    final devices = c.read(guiderAlpacaDevicesProvider.notifier);
    await devices.resolve(['rig.lan:6800']);
    names.byServer['rig.lan:6800'] = {'camera/0': const AlpacaDeviceInfo(name: 'New', uniqueId: 'new-cam')};
    await devices.resolve(['rig.lan:6800'], fresh: true);
    expect(devices.uniqueIdFor('Alpaca Camera [rig.lan:6800/0]'), 'new-cam');
  });
}
