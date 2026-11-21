import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/guider/guider_choice_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

import '../../support/phd2_section.dart';
import '../../support/settings_values.dart';

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
  Completer<void>? gate;

  @override
  Future<Map<String, AlpacaDeviceInfo>> fetchDevices(String host, int port) async {
    asked.add('$host:$port');
    await gate?.future;
    return byServer['$host:$port'] ?? const {};
  }

  @override
  void close() {}
}

class _FakeEquipment implements GuiderEquipmentClient {
  final servers = <String>['rig.lan:6800'];
  Completer<List<String>>? gate;
  @override
  Future<List<String>> discoverAlpaca({
    int? numQueries,
    int? timeoutSeconds,
  }) async => gate != null ? await gate!.future : servers;
  @override
  void close() {}
  @override
  dynamic noSuchMethod(Invocation i) => super.noSuchMethod(i);
}

void main() {
  ProviderContainer container(_FakeNames names) {
    final c = ProviderContainer(overrides: [
      alpacaDeviceNamesApiProvider.overrideWithValue(names),
      activeServerProvider.overrideWith((ref) => ref.watch(_rig)),
      // A pick edits the rig's copy of the guider settings (ADR 0015).
      phd2FromRig(),
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

  group('pick (#1861)', () {
    Future<ProviderContainer> named() async {
      final c = container(
        _FakeNames({
          'rig.lan:6800': {
            'camera/1': const AlpacaDeviceInfo(
              name: 'ASI290MM',
              uniqueId: 'cam-1',
            ),
          },
        }),
      );
      await c.read(guiderAlpacaDevicesProvider.notifier).resolve([
        'rig.lan:6800',
      ]);
      return c;
    }

    const stored = TestPhd2Settings(
      guiderCamera: 'Alpaca Camera [old.lan:11111/0]',
      guiderCameraAlpacaUniqueId: 'old-cam',
      guiderAlpacaHost: 'old.lan',
      guiderAlpacaPort: 11111,
      guiderCameraId: '3',
    );

    test('a pick from a second Alpaca server carries camera, UniqueID, host and port together', () async {
      final c = await named();
      final pick = c
          .read(guiderAlpacaDevicesProvider.notifier)
          .pick('Alpaca Camera [rig.lan:6800/1]');
      expect(pick.camera, 'Alpaca Camera [rig.lan:6800/1]');
      expect(pick.uniqueId, 'cam-1');
      expect((pick.host, pick.port), ('rig.lan', 6800));

      final out = applyGuiderPick(stored, pick);
      expect(out.guiderCamera, 'Alpaca Camera [rig.lan:6800/1]');
      expect(out.guiderCameraAlpacaUniqueId, 'cam-1');
      expect((out.guiderAlpacaHost, out.guiderAlpacaPort), ('rig.lan', 6800));
      expect(
        out.guiderCameraId,
        '3',
        reason: 'guiderCameraId is not part of a pick',
      );
    });

    test('a non-Alpaca choice has no UniqueID and no endpoint, and leaves the stored server', () async {
      final c = await named();
      final pick = c
          .read(guiderAlpacaDevicesProvider.notifier)
          .pick('ZWO ASI120MM');
      expect(pick.uniqueId, '');
      expect(pick.host, isNull);
      final out = applyGuiderPick(stored, pick);
      expect(out.guiderCamera, 'ZWO ASI120MM');
      expect(out.guiderCameraAlpacaUniqueId, '');
      expect((out.guiderAlpacaHost, out.guiderAlpacaPort), ('old.lan', 11111));
    });

    test(
      'an Alpaca camera whose id is not known yet picks with no UniqueID',
      () async {
        final c = await named();
        final pick = c
            .read(guiderAlpacaDevicesProvider.notifier)
            .pick('Alpaca Camera [rig.lan:6800/9]');
        expect(pick.uniqueId, '');
        expect(pick.host, isNotNull);
      },
    );

    test('None clears camera and UniqueID and leaves host and port', () async {
      final c = await named();
      final out = applyGuiderPick(
        stored,
        c.read(guiderAlpacaDevicesProvider.notifier).pick(''),
      );
      expect(out.guiderCamera, '');
      expect(out.guiderCameraAlpacaUniqueId, '');
      expect((out.guiderAlpacaHost, out.guiderAlpacaPort), ('old.lan', 11111));
    });

    test('the notifier and a draft apply the same pick the same way', () async {
      final c = await named();
      final pick = c
          .read(guiderAlpacaDevicesProvider.notifier)
          .pick('Alpaca Camera [rig.lan:6800/1]');
      final n = c.read(phd2SettingsProvider.notifier);
      n.applyPick(pick);
      expect(
        c.read(phd2SettingsProvider).value!.guiderCameraAlpacaUniqueId,
        applyGuiderPick(const TestPhd2Settings(), pick).guiderCameraAlpacaUniqueId,
      );
      expect(c.read(phd2SettingsProvider).value!.guiderAlpacaPort, 6800);
    });

    test('an untouched first-run camera step has no pick; a bare camera name derives its endpoint', () {
      final g = GuiderSettings();
      expect(g.guiderPick, isNull);
      g.guiderCamera = 'Alpaca Camera [rig.lan:6800/1]';
      expect(
        (g.guiderPick!.host, g.guiderPick!.port, g.guiderPick!.uniqueId),
        ('rig.lan', 6800, ''),
      );
      g.guiderCamera = null;
      expect(g.guiderPick, isNull);
    });
  });

  group('discovery state (#1861)', () {
    ProviderContainer withEquipment(_FakeNames names, _FakeEquipment eq) {
      final c = ProviderContainer(
        overrides: [
          alpacaDeviceNamesApiProvider.overrideWithValue(names),
          guiderEquipmentApiProvider.overrideWithValue(eq),
          activeServerProvider.overrideWith((ref) => ref.watch(_rig)),
        ],
      );
      addTearDown(c.dispose);
      return c;
    }

    test('two readers see a sweep running and the servers it found; a server switch resets them', () async {
      final c = withEquipment(_FakeNames({}), _FakeEquipment());
      final a = c.listen(guiderDiscoveryProvider, (_, _) {});
      final b = c.listen(guiderDiscoveryProvider, (_, _) {});
      c.listen(guiderAlpacaDevicesProvider, (_, _) {});
      expect(a.read().running, isFalse);

      final f = c.read(guiderAlpacaDevicesProvider.notifier).discover();
      expect(b.read().running, isTrue);
      await f;
      expect(a.read().running, isFalse);
      expect(a.read().servers, ['rig.lan:6800']);

      c.read(_rig.notifier).set(const AraServer(hostname: 'rig-b', port: 1));
      expect(c.read(guiderDiscoveryProvider).servers, isNull);
    });
    test('a sweep started on one rig does not write its servers into the next rig', () async {
      final gate = Completer<List<String>>();
      final eq = _FakeEquipment()..gate = gate;
      final c = withEquipment(
        _FakeNames({
          'rig-a-alpaca.lan:11111': {'camera/0': const AlpacaDeviceInfo(name: 'X', uniqueId: 'rig-a-cam')},
        }),
        eq,
      );
      c.listen(guiderDiscoveryProvider, (_, _) {});
      c.listen(guiderAlpacaDevicesProvider, (_, _) {});
      final f = c.read(guiderAlpacaDevicesProvider.notifier).discover();

      c.read(_rig.notifier).set(const AraServer(hostname: 'rig-b', port: 1));
      expect(c.read(guiderDiscoveryProvider).servers, isNull);
      gate.complete(['rig-a-alpaca.lan:11111']);
      await f;
      expect(c.read(guiderDiscoveryProvider).servers, isNull, reason: 'rig A\'s servers stay out of rig B');
      expect(c.read(guiderAlpacaDevicesProvider).uniqueIds, isEmpty, reason: 'rig A\'s UniqueIDs stay out of rig B');
    });

    test('a name lookup started on one rig does not write its UniqueIDs into the next rig', () async {
      final gate = Completer<void>();
      final names = _FakeNames({
        'localhost:11111': {'camera/0': const AlpacaDeviceInfo(name: 'ASI120', uniqueId: 'rig-a-cam')},
      })..gate = gate;
      final c = container(names);
      c.listen(guiderAlpacaDevicesProvider, (_, _) {});
      final f = c.read(guiderAlpacaDevicesProvider.notifier).resolve(['localhost:11111']);

      c.read(_rig.notifier).set(const AraServer(hostname: 'rig-b', port: 1));
      gate.complete();
      await f;
      expect(c.read(guiderAlpacaDevicesProvider).uniqueIds, isEmpty);
    });
  });
}
