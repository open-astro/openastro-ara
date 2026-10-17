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

/// The base profile the wizard clones: [camera] is its guide camera.
class _BaseProfileApi extends ProfileApi {
  _BaseProfileApi(this.camera)
      : super(const AraServer(hostname: 'h', port: 5555));
  final String camera;
  @override
  Future<Phd2Settings> getPhd2Settings() async =>
      Phd2Settings(guiderCamera: camera);
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
    void Function(ProviderContainer)? seedDraft}) async {
  final container = ProviderContainer(overrides: [
    guiderEquipmentApiProvider.overrideWithValue(equipment),
    if (profile != null) profileApiProvider.overrideWithValue(profile),
    alpacaDeviceNamesApiProvider.overrideWithValue(_NoAlpacaNames()),
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
}
