import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/dome_settings.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_dome_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/dome_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(this._stored);
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// The daemon's dome settings, kept in memory; it refuses a scope outside the dome.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi(this.settings)
    : super(const AraServer(hostname: 'h', port: 5555));
  DomeSettings settings;
  final puts = <DomeSettings>[];
  @override
  Future<DomeSettings> getDomeSettings() async => settings;
  @override
  Future<DomeSettings> putDomeSettings(DomeSettings value) async {
    puts.add(value);
    final offset = value.scopePositionNorthSouthMm.abs();
    if (offset > value.domeRadiusMm) {
      throw StateError('The scope sits outside the dome');
    }
    return settings = value;
  }
}

class _FakeDomeApi implements EquipmentDeviceClient<DomeStatus> {
  @override
  Future<void> forget() async {
    calls.add('forget');
  }

  _FakeDomeApi(this.status);
  DomeStatus? status;
  final List<String> calls = [];
  @override
  Future<DomeStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add(
        'command:$subpath:${body?['target_azimuth_deg'] ?? body?['enabled']}',
      );
  @override
  void close() {}
}

DomeStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool canSetShutter = true,
  bool canSetAzimuth = true,
  bool canPark = true,
  String runtimeState = 'idle',
  bool following = false,
}) => DomeStatus(
  deviceId: 'dome-0',
  name: 'Observatory',
  connectionState: state,
  capabilities: DomeCapabilities(
    canSetShutter: canSetShutter,
    canSetAzimuth: canSetAzimuth,
    canSyncAzimuth: false,
    canPark: canPark,
    canFindHome: true,
  ),
  runtimeState: runtimeState,
  azimuthDeg: 180,
  shutterOpen: true,
  atHome: false,
  parked: false,
  following: following,
);

/// The text field of the settings row labelled [label].
Finder _field(String label) => find.descendant(
  of: find
      .ancestor(of: find.text(label), matching: find.byType(Row))
      .at(1), // the label's own Row is the nearest
  matching: find.byType(TextField),
);

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeDomeApi> _pump(
  WidgetTester tester,
  DomeStatus? status, {
  _FakeProfileApi? profile,
  Locale? locale,
}) async {
  // A working device's status blinks (DESIGN.md status rule) and never settles: Reduce Motion holds it.
  tester.platformDispatcher.accessibilityFeaturesTestValue =
      const FakeAccessibilityFeatures(disableAnimations: true);
  addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
  await _wideSurface(tester);
  final api = _FakeDomeApi(status);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        serverLinkUpProvider.overrideWith((ref) => true),
        savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)]),
        ),
        domeApiFactoryProvider.overrideWithValue((_) => api),
        if (profile != null) profileApiProvider.overrideWithValue(profile),
      ],
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(body: EquipmentDomePanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live state + shutter/slew/park controls', (
    tester,
  ) async {
    await _pump(tester, _status());
    expect(find.text('Observatory'), findsOneWidget);
    expect(find.text('Shutter'), findsOneWidget);
    expect(find.text('180°'), findsWidgets);
    expect(find.widgetWithText(OutlinedButton, 'Open shutter'), findsOneWidget);
    expect(
      find.widgetWithText(OutlinedButton, 'Close shutter'),
      findsOneWidget,
    );
    expect(find.widgetWithText(OutlinedButton, 'Park'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Slew'), findsOneWidget);
  });

  testWidgets('Open shutter sends the command', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.widgetWithText(OutlinedButton, 'Open shutter'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:shutter/open:null'));
  });

  testWidgets('Slew sends the target azimuth', (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.widgetWithText(TextField, 'Azimuth (°)'), '90');
    await tester.tap(find.widgetWithText(FilledButton, 'Slew'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:slew:90.0'));
  });

  testWidgets('controls hidden / disabled by capability + busy', (
    tester,
  ) async {
    // No shutter capability → no shutter buttons; slewing → Slew disabled.
    await _pump(tester, _status(canSetShutter: false, runtimeState: 'slewing'));
    expect(find.widgetWithText(OutlinedButton, 'Open shutter'), findsNothing);
    expect(find.text('Slewing…'), findsOneWidget);
    final slew = tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Slew'),
    );
    expect(slew.onPressed, isNull);
  });

  testWidgets('the slaving note is gone; Follow the mount sends the command', (
    tester,
  ) async {
    final profile = _FakeProfileApi(const DomeSettings());
    final api = await _pump(tester, _status(), profile: profile);
    expect(
      find.textContaining('there is no separate dome setting'),
      findsNothing,
    );
    expect(
      find.textContaining('Ara turns the dome so its slit stays'),
      findsOneWidget,
    );
    await tester.tap(find.byType(Switch).at(1)); // after Auto-connect on boot
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:follow:true'));
  });

  testWidgets('the Follow switch shows the dome status', (tester) async {
    // The daemon turned following off (a park, the dial, a safety reaction):
    // the status says so, and the switch follows it.
    await _pump(
      tester,
      _status(),
      profile: _FakeProfileApi(const DomeSettings()),
    );
    expect(tester.widget<Switch>(find.byType(Switch).at(1)).value, isFalse);

    await _pump(
      tester,
      _status(following: true),
      profile: _FakeProfileApi(const DomeSettings()),
    );
    expect(tester.widget<Switch>(find.byType(Switch).at(1)).value, isTrue);
  });

  testWidgets('a dome that cannot turn offers no Follow the mount', (
    tester,
  ) async {
    final api = await _pump(
      tester,
      _status(canSetAzimuth: false),
      profile: _FakeProfileApi(const DomeSettings()),
    );
    await tester.tap(find.byType(Switch).at(1));
    await tester.pumpAndSettle();
    expect(
      find.text('Connect a dome that can turn to an azimuth first'),
      findsOneWidget,
    );
    expect(api.calls.where((c) => c.startsWith('command:follow')), isEmpty);
  });

  testWidgets('the geometry is saved, and a refusal says why', (tester) async {
    final profile = _FakeProfileApi(const DomeSettings(domeRadiusMm: 2500));
    await _pump(tester, _status(), profile: profile);
    final radius = _field('Dome radius (mm)');
    await tester.scrollUntilVisible(
      radius,
      200,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.enterText(radius, '1000');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(profile.puts.single.domeRadiusMm, 1000);

    final north = _field('Scope north of dome center (mm)');
    await tester.scrollUntilVisible(
      north,
      200,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.enterText(north, '1500');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(
      find.textContaining('The scope sits outside the dome'),
      findsOneWidget,
    );
    expect(profile.settings.scopePositionNorthSouthMm, 0, reason: 'kept');
  });

  testWidgets('a fork mount has no German-mount axis offsets', (tester) async {
    await _pump(
      tester,
      _status(),
      profile: _FakeProfileApi(const DomeSettings(mountType: 'fork_on_wedge')),
    );
    await tester.scrollUntilVisible(
      find.text('Azimuth tolerance (°)'),
      200,
      scrollable: find.byType(Scrollable).first,
    );
    expect(find.text('Mount axis length (mm)'), findsNothing);
    await _pump(
      tester,
      _status(),
      profile: _FakeProfileApi(const DomeSettings()),
    );
    await tester.scrollUntilVisible(
      find.text('Azimuth tolerance (°)'),
      200,
      scrollable: find.byType(Scrollable).first,
    );
    expect(find.text('Mount axis length (mm)'), findsOneWidget);
  });

  testWidgets('the follow section and its geometry read in pt-BR', (
    tester,
  ) async {
    await _pump(
      tester,
      _status(runtimeState: 'slewing', following: true),
      profile: _FakeProfileApi(const DomeSettings()),
      locale: const Locale('pt', 'BR'),
    );
    expect(find.text('Seguindo a montagem'), findsOneWidget);
    expect(find.text('Seguindo a montagem…'), findsOneWidget);
    expect(find.text('Seguir a montagem'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('Raio da cúpula (mm)'),
      200,
      scrollable: find.byType(Scrollable).first,
    );
    expect(find.text('Geometria'), findsOneWidget);
  });

  testWidgets('no device connected shows the empty state + Connect…', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.text('No dome connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
