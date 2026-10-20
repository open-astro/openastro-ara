import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_equipment_choices.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/screens/screen_guiding.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/wizard_state.dart';
import 'package:openastroara/widgets/guider/guider_setup_wizard.dart'
    show alpacaDeviceNamesApiProvider;

/// §76 S3 — records the pixel-size lookup and answers with a scripted value.
class _FakeGuiderEquipment implements GuiderEquipmentClient {
  _FakeGuiderEquipment({this.pixelSize});
  final double? pixelSize;
  String? lastHost;
  int? lastPort;
  int? lastDevice;

  @override
  Future<double?> getAlpacaCameraPixelSize(
      {String? host, int? port, int? device}) async {
    lastHost = host;
    lastPort = port;
    lastDevice = device;
    return pixelSize;
  }

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

/// A fresh guider: it offers an Alpaca camera and "None", and has none picked.
class _FreshGuiderEquipment extends _FakeGuiderEquipment {
  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async =>
      const GuiderEquipmentChoicesResponse(
          connected: true,
          choices: GuiderEquipmentChoices(
              cameras: ['Alpaca Camera [localhost:6800/0]', 'None']));
  @override
  Future<List<String>> discoverAlpaca(
          {int? numQueries, int? timeoutSeconds}) async =>
      const [];
}

/// An AlpacaBridge SBC answering discovery on loopback, LAN and hotspot, with
/// two cameras, a mount and a rotator behind it.
const _bridgeAliases = ['127.0.0.1:6800', '192.168.8.118:6800', '172.24.1.1:6800'];

class _BridgeGuiderEquipment extends _FakeGuiderEquipment {
  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async =>
      const GuiderEquipmentChoicesResponse(
          connected: true,
          choices: GuiderEquipmentChoices(
              cameras: ['Alpaca Camera', 'None'],
              mounts: ['Alpaca Mount', 'None'],
              auxMounts: ['Alpaca Mount', 'None'],
              rotators: ['Alpaca Rotator', 'None']));
  @override
  Future<List<String>> discoverAlpaca(
          {int? numQueries, int? timeoutSeconds}) async =>
      _bridgeAliases;
}

class _BridgeDevices extends AlpacaDeviceNamesClient {
  @override
  Future<Map<String, AlpacaDeviceInfo>> fetchDevices(
          String host, int port) async =>
      const {
        'camera/0': AlpacaDeviceInfo(name: 'Guide Cam', uniqueId: 'GUIDE-ID'),
        'camera/1':
            AlpacaDeviceInfo(name: 'Imaging Cam', uniqueId: 'IMAGING-ID'),
        'telescope/0': AlpacaDeviceInfo(name: 'Mount', uniqueId: 'MOUNT-ID'),
        'rotator/0':
            AlpacaDeviceInfo(name: 'Rotator', uniqueId: 'ROTATOR-ID'),
      };
  @override
  void close() {}
}

/// The equipment step's picks: the imaging camera, mount and rotator.
void _seedEquipment(ProviderContainer c) =>
    c.read(wizardControllerProvider).draft.equipment
      ..cameraDeviceId = 'IMAGING-ID'
      ..mountDeviceId = 'MOUNT-ID'
      ..rotatorDeviceId = 'ROTATOR-ID';

DropdownMenu<String> _menu(WidgetTester tester, String label) =>
    tester.widget<DropdownMenu<String>>(find.byWidgetPredicate((w) =>
        w is DropdownMenu<String> && (w.label as Text?)?.data == label));

/// The base profile the wizard clones: [camera] is its guide camera.
class _BaseProfileApi extends ProfileApi {
  _BaseProfileApi(this.camera, {this.mount = ''})
      : super(const AraServer(hostname: 'h', port: 5555));
  final String camera;
  final String mount;
  @override
  Future<Phd2Settings> getPhd2Settings() async =>
      Phd2Settings(guiderCamera: camera, guiderMount: mount);
  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
}

class _NoAlpacaNames extends AlpacaDeviceNamesClient {
  @override
  Future<Map<String, AlpacaDeviceInfo>> fetchDevices(
          String host, int port) async =>
      const {};
  @override
  void close() {}
}

Future<ProviderContainer> _pump(WidgetTester tester,
    {GuiderEquipmentClient? equipment,
    ProfileApi? profile,
    AlpacaDeviceNamesClient? devices,
    void Function(ProviderContainer)? seedDraft}) async {
  final container = ProviderContainer(overrides: [
    guiderEquipmentApiProvider.overrideWithValue(equipment),
    if (profile != null) profileApiProvider.overrideWithValue(profile),
    alpacaDeviceNamesApiProvider
        .overrideWithValue(devices ?? _NoAlpacaNames()),
  ]);
  addTearDown(container.dispose);
  container.listen(wizardStepValidProvider, (_, _) {});
  seedDraft?.call(container);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: ScreenGuider())),
  ));
  await tester.pump();
  return container;
}

void main() {
  group('guideCameraPickNeeded', () {
    const cameras = ['Alpaca Camera [localhost:6800/0]', 'None'];
    test('a fresh profile on a reachable guider must pick a camera', () {
      expect(
          guideCameraPickNeeded(
              baseLoaded: true,
              baseCamera: null,
              draftCamera: null,
              cameraChoices: cameras),
          isTrue);
    });
    test('any pick, None included, clears the gate', () {
      for (final pick in ['Alpaca Camera [localhost:6800/0]', 'None']) {
        expect(
            guideCameraPickNeeded(
                baseLoaded: true,
                baseCamera: null,
                draftCamera: pick,
                cameraChoices: cameras),
            isFalse,
            reason: pick);
      }
    });
    test('a base profile camera is kept, so no pick is needed', () {
      expect(
          guideCameraPickNeeded(
              baseLoaded: true,
              baseCamera: 'Alpaca Camera [localhost:6800/0]',
              draftCamera: null,
              cameraChoices: cameras),
          isFalse);
    });
    test('offline never blocks: base unread or no choices', () {
      expect(
          guideCameraPickNeeded(
              baseLoaded: false,
              baseCamera: null,
              draftCamera: null,
              cameraChoices: cameras),
          isFalse);
      expect(
          guideCameraPickNeeded(
              baseLoaded: true,
              baseCamera: null,
              draftCamera: null,
              cameraChoices: null),
          isFalse);
    });
  });

  testWidgets('a fresh profile holds Next until a guide camera is picked',
      (tester) async {
    final container = await _pump(tester,
        equipment: _FreshGuiderEquipment(), profile: _BaseProfileApi(''));
    await tester.pumpAndSettle();
    expect(container.read(wizardStepValidProvider), isFalse);
    expect(find.textContaining('No guide camera picked'), findsOneWidget);

    tester
        .widget<DropdownMenu<String>>(find.byType(DropdownMenu<String>).first)
        .onSelected!('Alpaca Camera [localhost:6800/0]');
    await tester.pumpAndSettle();
    expect(container.read(wizardStepValidProvider), isTrue);
    expect(find.textContaining('No guide camera picked'), findsNothing);
  });

  testWidgets('a base profile with a guide camera keeps Next open',
      (tester) async {
    final container = await _pump(tester,
        equipment: _FreshGuiderEquipment(),
        profile: _BaseProfileApi('Alpaca Camera [localhost:6800/0]'));
    await tester.pumpAndSettle();
    expect(container.read(wizardStepValidProvider), isTrue);
    expect(find.textContaining('No guide camera picked'), findsNothing);
  });

  testWidgets('the exposure range defaults to the guider\'s own 1.0–6.0 s '
      'and darks-on-finish defaults ON', (tester) async {
    final container = await _pump(tester);
    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.guider.darkMinExposureMs, 1000);
    expect(draft.guider.darkMaxExposureMs, 6000);
    expect(draft.guider.buildDarksOnFinish, isTrue);
    expect(find.text('Build dark library now'), findsOneWidget);
    expect(container.read(wizardStepValidProvider), isTrue);
  });

  testWidgets('the Shortest menu opens at 0.01 s, not scrolled to the default',
      (tester) async {
    await _pump(tester);
    final shortest = find.byWidgetPredicate((w) =>
        w is DropdownMenu<int> && (w.label as Text?)?.data == 'Shortest');
    await tester.ensureVisible(shortest);
    await tester.tap(shortest);
    await tester.pumpAndSettle();
    expect(find.text('0.01 s').hitTestable(), findsOneWidget);
    await tester.tapAt(Offset.zero);
    await tester.pumpAndSettle();
  });

  testWidgets('an inverted range blocks Next and shows the error hint; '
      'fixing it re-validates', (tester) async {
    final container = await _pump(tester);

    // Shortest 2.5 s > Longest 1.0 s. Drive the DropdownMenu selection
    // callbacks directly — the exposure items sit inside the menu overlay's
    // own scrollable, where a coordinate tap silently misses; the range-gate
    // logic is what's under test, not Material's menu scrolling. (Two int
    // dropdowns on screen: index 0 = Shortest, 1 = Longest — the Advanced
    // frame-count dropdown only exists once the disclosure is expanded.)
    final shortest = find.byType(DropdownMenu<int>).at(0);
    final longest = find.byType(DropdownMenu<int>).at(1);
    tester.widget<DropdownMenu<int>>(shortest).onSelected!(2500);
    tester.widget<DropdownMenu<int>>(longest).onSelected!(1000);
    await tester.pumpAndSettle();

    expect(container.read(wizardStepValidProvider), isFalse);
    expect(find.text('Shortest exposure must not exceed the longest.'),
        findsOneWidget);

    tester.widget<DropdownMenu<int>>(longest).onSelected!(4000);
    await tester.pumpAndSettle();
    expect(container.read(wizardStepValidProvider), isTrue);

    final g = container.read(wizardControllerProvider).draft.guider;
    expect(g.darkMinExposureMs, 2500);
    expect(g.darkMaxExposureMs, 4000);
  });

  testWidgets('remounting with an inverted range persisted in the draft '
      're-locks Next (Back → Next cannot bypass the gate)', (tester) async {
    final container = await _pump(tester, seedDraft: (c) {
      c.read(wizardControllerProvider).draft.guider
        ..darkMinExposureMs = 2500
        ..darkMaxExposureMs = 1000;
      // Simulate the controller's unconditional reset on navigation.
      c.read(wizardStepValidProvider.notifier).setValid(true);
    });
    await tester.pump();
    expect(container.read(wizardStepValidProvider), isFalse,
        reason: 'mount re-derives the gate from the persisted draft');
    expect(find.text('Shortest exposure must not exceed the longest.'),
        findsOneWidget);
    // Tear the tree down and elapse any straggler one-shot timers (the
    // screen's providers arm short-lived ones) so the binding's
    // pending-timer teardown assert stays quiet.
    await tester.pumpWidget(const SizedBox());
    await tester.pump(const Duration(minutes: 1));
  });

  testWidgets('a chosen Alpaca guide camera fills the pixel size from the '
      'driver and shows it as a read fact', (tester) async {
    final fake = _FakeGuiderEquipment(pixelSize: 2.9);
    final container = await _pump(tester, equipment: fake, seedDraft: (c) {
      c.read(wizardControllerProvider).draft.guider.guiderCamera =
          'Alpaca Camera [10.0.0.9:11111/1]';
    });
    await tester.pumpAndSettle();

    expect(fake.lastHost, '10.0.0.9');
    expect(fake.lastPort, 11111);
    expect(fake.lastDevice, 1);
    final g = container.read(wizardControllerProvider).draft.guider;
    expect(g.guidePixelSizeUm, 2.9);
    expect(find.textContaining('read from the camera driver'), findsOneWidget);
  });

  testWidgets('a non-Alpaca camera choice leaves the manual pixel-size field '
      'in Advanced', (tester) async {
    final container = await _pump(tester, seedDraft: (c) {
      c.read(wizardControllerProvider).draft.guider.guiderCamera = 'QHY5-II';
    });
    await tester.pumpAndSettle();
    expect(find.textContaining('read from the camera driver'), findsNothing);

    await tester.ensureVisible(find.text('Advanced'));
    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();
    expect(find.text('Guide pixel size (µm)'), findsOneWidget);
    expect(container.read(wizardControllerProvider).draft.guider.guidePixelSizeUm,
        isNull);
  });

  group('pre-picks from the equipment step', () {
    testWidgets('a fresh profile gets the other camera and the imaging mount, '
        'once each, on the guider\'s own machine', (tester) async {
      final container = await _pump(tester,
          equipment: _BridgeGuiderEquipment(),
          profile: _BaseProfileApi(''),
          devices: _BridgeDevices(),
          seedDraft: _seedEquipment);
      await tester.pumpAndSettle();

      final g = container.read(wizardControllerProvider).draft.guider;
      expect(g.guiderCamera, 'Alpaca Camera [127.0.0.1:6800/0]');
      expect(g.guiderCameraAlpacaUniqueId, 'GUIDE-ID');
      expect(g.guiderMount, 'Alpaca Mount [127.0.0.1:6800/0]');
      // A guide scope never turns with the imaging rotator.
      expect(g.guiderRotator, isNull);
      expect(container.read(wizardStepValidProvider), isTrue);
      expect(find.textContaining('Matched to the equipment'), findsNWidgets(2));

      final cameraRows = _menu(tester, 'Guide camera')
          .dropdownMenuEntries
          .map((e) => e.value)
          .toList();
      expect(cameraRows, [
        '',
        'None',
        'Alpaca Camera [127.0.0.1:6800/0]',
        'Alpaca Camera [127.0.0.1:6800/1]',
      ]);
    });

    testWidgets('an OAG brings the imaging rotator; a guide scope withdraws it',
        (tester) async {
      final container = await _pump(tester,
          equipment: _BridgeGuiderEquipment(),
          profile: _BaseProfileApi(''),
          devices: _BridgeDevices(),
          seedDraft: _seedEquipment);
      await tester.pumpAndSettle();
      final g = container.read(wizardControllerProvider).draft.guider;

      _menu(tester, 'Guide setup').onSelected!('oag');
      await tester.pumpAndSettle();
      expect(g.guiderRotator, 'Alpaca Rotator [127.0.0.1:6800/0]');

      _menu(tester, 'Guide setup').onSelected!('guide_scope');
      await tester.pumpAndSettle();
      expect(g.guiderRotator, isNull);
    });

    testWidgets('a user pick is never overwritten', (tester) async {
      final container = await _pump(tester,
          equipment: _BridgeGuiderEquipment(),
          profile: _BaseProfileApi(''),
          devices: _BridgeDevices(),
          seedDraft: _seedEquipment);
      await tester.pumpAndSettle();
      final g = container.read(wizardControllerProvider).draft.guider;

      _menu(tester, 'Guide mount (pulse target)').onSelected!('None');
      _menu(tester, 'Guide camera').onSelected!('None');
      await tester.pumpAndSettle();
      expect(g.guiderCamera, 'None');
      expect(g.guiderCameraAlpacaUniqueId, '');
      expect(g.guiderMount, 'None');
      expect(find.textContaining('Matched to the equipment'), findsNothing);
    });

    testWidgets('a base profile that fills the slots keeps them',
        (tester) async {
      final container = await _pump(tester,
          equipment: _BridgeGuiderEquipment(),
          profile: _BaseProfileApi('Alpaca Camera [192.168.8.118:6800/0]',
              mount: 'Alpaca Mount [192.168.8.118:6800/0]'),
          devices: _BridgeDevices(),
          seedDraft: _seedEquipment);
      await tester.pumpAndSettle();
      final g = container.read(wizardControllerProvider).draft.guider;
      expect(g.guiderCamera, isNull);
      expect(g.guiderMount, isNull);
      // The base camera's own address is the row kept for that device.
      expect(
          _menu(tester, 'Guide camera').dropdownMenuEntries.map((e) => e.value),
          contains('Alpaca Camera [192.168.8.118:6800/0]'));
    });

    testWidgets('no equipment-step camera, no guess', (tester) async {
      final container = await _pump(tester,
          equipment: _BridgeGuiderEquipment(),
          profile: _BaseProfileApi(''),
          devices: _BridgeDevices());
      await tester.pumpAndSettle();
      final g = container.read(wizardControllerProvider).draft.guider;
      expect(g.guiderCamera, isNull);
      expect(g.guiderMount, isNull);
      expect(container.read(wizardStepValidProvider), isFalse);
    });
  });
}
