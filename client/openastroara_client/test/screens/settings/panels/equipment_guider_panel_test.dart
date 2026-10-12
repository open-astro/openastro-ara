import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_equipment_choices.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_guider_panel.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/widgets/guider/guider_setup_wizard.dart';

// #1306 — the Settings → Guider pickers used to offer only the daemon's bare
// "Alpaca Camera" / "Alpaca Mount" strings. Saved as-is with no Alpaca
// host/port, the push sent no set_alpaca_server and the guider sat on
// localhost:0 with nothing connected. The panel now offers every real device
// as a `[host:port/N]` choice, follows the camera's server into host/port,
// and refuses Apply on a bare selection, saying why.

const _server = AraServer(hostname: 'h', port: 5555);

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [_server];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi(this.stored) : super(_server);
  Phd2Settings stored;
  Phd2Settings? lastPut;
  @override
  Future<Phd2Settings> getPhd2Settings() async => stored;
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings value) async {
    lastPut = value;
    stored = value;
    return value;
  }

  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
  @override
  Future<EquipmentConnectionSettings> getEquipmentConnection() async =>
      const EquipmentConnectionSettings();
}

class _FakeEquipmentApi implements GuiderEquipmentClient {
  final calls = <String>[];
  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async {
    calls.add('choices');
    // The field case: the guider has no Alpaca config of its own, so it
    // offers only the generic strings.
    return const GuiderEquipmentChoicesResponse(
      connected: true,
      choices: GuiderEquipmentChoices(
        cameras: ['None', 'Alpaca Camera', 'Simulator'],
        mounts: ['None', 'Alpaca Mount', 'On-camera'],
      ),
    );
  }

  @override
  Future<List<String>> discoverAlpaca(
          {int? numQueries, int? timeoutSeconds}) async {
    calls.add('discover');
    return const ['127.0.0.1:6800'];
  }

  @override
  Future<void> pushProfile({bool tuningOnly = false}) async =>
      calls.add(tuningOnly ? 'push-tuning' : 'push');
  @override
  Future<double?> getAlpacaCameraPixelSize(
          {String? host, int? port, int? device}) async =>
      null;
  @override
  void close() {}
}

class _FakeAlpacaNames implements AlpacaDeviceNamesClient {
  final calls = <String>[];
  /// While > 0, every lookup answers empty (an unreachable management API).
  int failuresLeft = 0;
  @override
  Future<Map<String, String>> fetchNames(String host, int port) async {
    calls.add('$host:$port');
    if (failuresLeft > 0) {
      failuresLeft--;
      return const {};
    }
    if (host == '127.0.0.1' && port == 6800) {
      return {
        'camera/0': 'ZWO ASI120MM Mini',
        'telescope/1': 'Sky-Watcher EQM-35 Pro (EQMOD)',
      };
    }
    return const {};
  }

  @override
  void close() {}
}

typedef _Harness = ({
  _FakeProfileApi profile,
  _FakeEquipmentApi equipment,
  _FakeAlpacaNames names,
});

Future<_Harness> _pump(WidgetTester tester, Phd2Settings stored) async {
  await tester.binding.setSurfaceSize(const Size(1600, 2400));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final profile = _FakeProfileApi(stored);
  final equipment = _FakeEquipmentApi();
  final names = _FakeAlpacaNames();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        profileApiProvider.overrideWithValue(profile),
        guiderEquipmentApiFactoryProvider.overrideWithValue((_) => equipment),
        alpacaDeviceNamesApiProvider.overrideWithValue(names),
      ],
      child: const MaterialApp(home: Scaffold(body: EquipmentGuiderPanel())),
    ),
  );
  await tester.pumpAndSettle();
  return (profile: profile, equipment: equipment, names: names);
}

Future<void> _pick(WidgetTester tester, String rowKey, String item) async {
  final dropdown = find.descendant(
      of: find.byKey(ValueKey(rowKey)),
      matching: find.byType(DropdownButton<String>));
  await tester.ensureVisible(dropdown);
  await tester.tap(dropdown);
  await tester.pumpAndSettle();
  await tester.tap(find.text(item).last);
  await tester.pumpAndSettle();
}

void main() {
  group('describeUnservedAlpacaSelection', () {
    test('flags a bare Alpaca choice with no host/port, naming the slot', () {
      const s = Phd2Settings(guiderCamera: 'Alpaca Camera', guiderMount: 'Alpaca Mount');
      final reason = describeUnservedAlpacaSelection(s)!;
      expect(reason, contains("guide camera 'Alpaca Camera'"));
      expect(reason, contains('Alpaca host and port'));
    });

    test('names the one missing half', () {
      const s = Phd2Settings(guiderMount: 'Alpaca Mount', guiderAlpacaHost: '127.0.0.1');
      expect(describeUnservedAlpacaSelection(s), endsWith('Alpaca port.'));
    });

    test('flags a suffixed mount beside a non-Alpaca camera with no stored server', () {
      const s = Phd2Settings(
          guiderCamera: 'Simulator', guiderMount: 'Alpaca Mount [10.0.0.5:6800/1]');
      final reason = describeUnservedAlpacaSelection(s)!;
      expect(reason, contains("guide mount 'Alpaca Mount [10.0.0.5:6800/1]'"));
      expect(reason, contains('10.0.0.5:6800'));
      expect(reason, contains('set the Alpaca host and port to it'));
    });

    test('passes served, suffixed and non-Alpaca selections', () {
      expect(
          describeUnservedAlpacaSelection(const Phd2Settings(
              guiderCamera: 'Alpaca Camera',
              guiderAlpacaHost: '127.0.0.1',
              guiderAlpacaPort: 6800)),
          isNull);
      expect(
          describeUnservedAlpacaSelection(const Phd2Settings(
              guiderCamera: 'Alpaca Camera [127.0.0.1:6800/0]',
              guiderMount: 'Alpaca Mount')),
          isNull,
          reason: 'the camera endpoint serves the bare mount');
      expect(
          describeUnservedAlpacaSelection(
              const Phd2Settings(guiderCamera: 'Simulator', guiderMount: 'On-camera')),
          isNull);
      expect(describeUnservedAlpacaSelection(const Phd2Settings()), isNull);
    });
  });

  testWidgets('a mount on another server than the camera gets a cross-server warning',
      (tester) async {
    await _pump(
        tester,
        const Phd2Settings(
            guiderCamera: 'Alpaca Camera [127.0.0.1:6800/0]',
            guiderMount: 'Alpaca Mount [10.0.0.5:6800/1]'));
    expect(find.byKey(const ValueKey('guider_cross_server')), findsOneWidget);
    expect(find.textContaining('different Alpaca server'), findsOneWidget);
  });

  testWidgets('a bare Alpaca selection shows the reason and Apply refuses to push',
      (tester) async {
    final h = await _pump(
        tester,
        const Phd2Settings(
            guiderCamera: 'Alpaca Camera', guiderMount: 'Alpaca Mount'));

    expect(find.byKey(const ValueKey('guider_unserved_alpaca')), findsOneWidget);
    await tester.ensureVisible(find.text('Apply to guider'));
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();

    expect(h.equipment.calls, isNot(contains('push')),
        reason: 'nothing is pushed while the selection cannot work');
    expect(h.profile.lastPut, isNull, reason: 'nothing is saved either');
    expect(find.textContaining("names an Alpaca driver but no server"),
        findsWidgets);
  });

  testWidgets(
      'Discover Alpaca puts real [host:port/N] devices in the pickers and a pick fills host/port',
      (tester) async {
    final h = await _pump(tester, const Phd2Settings());

    await tester.ensureVisible(find.text('Discover Alpaca'));
    await tester.tap(find.text('Discover Alpaca'));
    await tester.pumpAndSettle();
    expect(h.names.calls, contains('127.0.0.1:6800'));

    await _pick(tester, 'guider_pick_camera', 'ZWO ASI120MM Mini (127.0.0.1:6800/0)');
    await _pick(tester, 'guider_pick_mount',
        'Sky-Watcher EQM-35 Pro (EQMOD) (127.0.0.1:6800/1)');

    final element = tester.element(find.byType(EquipmentGuiderPanel));
    final phd2 = ProviderScope.containerOf(element).read(phd2SettingsProvider);
    expect(phd2.guiderCamera, 'Alpaca Camera [127.0.0.1:6800/0]');
    expect(phd2.guiderMount, 'Alpaca Mount [127.0.0.1:6800/1]');
    expect(phd2.guiderAlpacaHost, '127.0.0.1');
    expect(phd2.guiderAlpacaPort, 6800);
    expect(find.byKey(const ValueKey('guider_unserved_alpaca')), findsNothing);

    await tester.ensureVisible(find.text('Apply to guider'));
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    expect(h.profile.lastPut?.guiderCamera, 'Alpaca Camera [127.0.0.1:6800/0]');
    expect(h.equipment.calls, contains('push'));
  });

  testWidgets('an endpoint that answered nothing is asked again on the next trigger',
      (tester) async {
    final h = await _pump(tester, const Phd2Settings());
    h.names.failuresLeft = 1; // the management API is briefly unreachable
    await tester.ensureVisible(find.text('Discover Alpaca'));
    await tester.tap(find.text('Discover Alpaca'));
    await tester.pumpAndSettle();
    expect(h.names.calls, ['127.0.0.1:6800']);
    await tester.tap(find.text('Discover Alpaca'));
    await tester.pumpAndSettle();
    expect(h.names.calls, ['127.0.0.1:6800', '127.0.0.1:6800'],
        reason: 'an empty answer is not remembered as resolved');
    await _pick(tester, 'guider_pick_camera', 'ZWO ASI120MM Mini (127.0.0.1:6800/0)');
    await tester.tap(find.text('Discover Alpaca'));
    await tester.pumpAndSettle();
    expect(h.names.calls.length, 2, reason: 'a resolved endpoint is not refetched');
  });

  testWidgets('a stored Alpaca host/port is resolved on load so its devices are pickable',
      (tester) async {
    final h = await _pump(
        tester,
        const Phd2Settings(
            guiderCamera: 'Alpaca Camera',
            guiderAlpacaHost: '127.0.0.1',
            guiderAlpacaPort: 6800));
    expect(h.names.calls, contains('127.0.0.1:6800'));
    expect(find.byKey(const ValueKey('guider_unserved_alpaca')), findsNothing,
        reason: 'host and port serve the bare choice');
    await _pick(tester, 'guider_pick_camera', 'ZWO ASI120MM Mini (127.0.0.1:6800/0)');
    final element = tester.element(find.byType(EquipmentGuiderPanel));
    expect(
        ProviderScope.containerOf(element).read(phd2SettingsProvider).guiderCamera,
        'Alpaca Camera [127.0.0.1:6800/0]');
  });
}
