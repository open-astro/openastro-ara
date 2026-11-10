import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/state/guider/guider_choice_state.dart';

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
}
