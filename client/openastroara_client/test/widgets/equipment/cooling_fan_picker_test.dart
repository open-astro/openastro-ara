import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/cooling_fan_prompt_listener.dart';
import 'package:openastroara/widgets/equipment/cooling_fan_picker.dart';

SwitchDevice _box({
  String id = 'box',
  String name = 'Pegasus Powerbox',
  SwitchConnectionState state = SwitchConnectionState.connected,
}) => SwitchDevice(
  deviceId: id,
  alpacaDeviceNumber: 0,
  name: name,
  connectionState: state,
  ports: [
    SwitchPort(id: 0, name: 'Fan 1', value: 0, min: 0, max: 1, canWrite: true),
    SwitchPort(id: 1, name: 'Dew B', value: 0, min: 0, max: 1, canWrite: true),
    SwitchPort(
      id: 2,
      name: 'Voltage',
      value: 12,
      min: 0,
      max: 20,
      canWrite: false,
    ),
  ],
);

class _Switches extends SwitchListNotifier {
  _Switches(this.list);
  final List<SwitchDevice> list;
  @override
  Future<List<SwitchDevice>> build() async => list;
}

class _Api extends ProfileApi {
  _Api() : super(const AraServer(hostname: 'test', port: 1));
  Object? refuse;
  CoolingFan current = CoolingFan.none;
  final puts = <CoolingFan>[];
  @override
  Future<CoolingFan> getCoolingFan() async => current;
  @override
  Future<CoolingFan> putCoolingFan(CoolingFan value) async {
    final e = refuse;
    if (e != null) throw e;
    puts.add(value);
    return current = value;
  }
}

class _Profiles extends ProfileManagementNotifier {
  _Profiles(this.activeId);
  final String? activeId;
  @override
  Future<ProfileList> build() async =>
      ProfileList(activeId: activeId, profiles: const []);
}

class _Camera extends CameraStatusNotifier {
  _Camera({this.cooler = true, this.wireState = 'connected'});
  final bool cooler;
  final String wireState;
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson({
    'device_id': 'c',
    'name': 'ATR2600M',
    'state': wireState,
    'capabilities': {'has_cooler': cooler, 'can_set_temperature': cooler},
    'runtime': {'cooler_on': false},
  });
}

class _Mem extends SimpleModePrefsService {
  bool value = false;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

Future<void> _pumpPicker(
  WidgetTester tester, {
  CoolingFan value = CoolingFan.none,
  List<SwitchDevice> switches = const [],
  ValueChanged<CoolingFan>? onChanged,
}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [switchListProvider.overrideWith(() => _Switches(switches))],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: CoolingFanPicker(value: value, onChanged: onChanged ?? (_) {}),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _open(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('cooling-fan-picker')));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('None comes first, then each writable port as Device › Port', (
    tester,
  ) async {
    await _pumpPicker(tester, switches: [_box()]);
    await _open(tester);
    final items = tester
        .widgetList<DropdownMenuItem<CoolingFan>>(
          find.byType(DropdownMenuItem<CoolingFan>),
        )
        .map((i) => (i.child as Text).data)
        .toList();
    expect(items.toSet(), {
      'None',
      'Pegasus Powerbox › Fan 1',
      'Pegasus Powerbox › Dew B',
    });
    expect(items.first, 'None');
  });

  testWidgets(
    'a disconnected switch offers no ports; a chosen one stays and says so',
    (tester) async {
      const chosen = CoolingFan(deviceId: 'box', portId: 0);
      await _pumpPicker(
        tester,
        value: chosen,
        switches: [_box(state: SwitchConnectionState.disconnected)],
      );
      expect(find.text('Chosen fan (not connected)'), findsOneWidget);
      expect(find.textContaining('cannot be turned off'), findsOneWidget);
    },
  );

  testWidgets('the caption says what the choice does', (tester) async {
    await _pumpPicker(tester);
    expect(
      find.text('No fan is chosen, so Ara does not run a fan with the cooler.'),
      findsOneWidget,
    );
    await _pumpPicker(
      tester,
      value: const CoolingFan(deviceId: 'box', portId: 0),
      switches: [_box()],
    );
    expect(
      find.text(
        'This fan starts with the cooler and cannot be turned off while the camera is cooling.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('choosing a port hands back its designation', (tester) async {
    CoolingFan? picked;
    await _pumpPicker(tester, switches: [_box()], onChanged: (v) => picked = v);
    await _open(tester);
    await tester.tap(find.text('Pegasus Powerbox › Dew B').last);
    await tester.pumpAndSettle();
    expect(picked, const CoolingFan(deviceId: 'box', portId: 1));
  });

  testWidgets('the control is at least 44 px tall', (tester) async {
    await _pumpPicker(tester, switches: [_box()]);
    expect(
      tester.getSize(find.byKey(const Key('cooling-fan-picker'))).height,
      greaterThanOrEqualTo(44),
    );
  });

  testWidgets('a refused save keeps the old choice and says so', (
    tester,
  ) async {
    final api = _Api()..refuse = StateError('down');
    final c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWithValue(api),
        profileManagementProvider.overrideWith(() => _Profiles('p1')),
        switchListProvider.overrideWith(() => _Switches([_box()])),
        simpleModeActiveProvider.overrideWithValue(true),
      ],
    );
    addTearDown(c.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: ProfileCoolingFanPicker()),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await _open(tester);
    await tester.tap(find.text('Pegasus Powerbox › Fan 1').last);
    await tester.pumpAndSettle();
    expect(c.read(coolingFanProvider).value, CoolingFan.none);
    expect(
      c.read(simpleMessagesProvider).single.title,
      "Couldn't save the cooling fan",
    );
  });

  test('a saved choice takes the daemon echo', () async {
    final api = _Api();
    final c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWithValue(api),
        profileManagementProvider.overrideWith(() => _Profiles('p1')),
      ],
    );
    addTearDown(c.dispose);
    c.listen(
      coolingFanProvider,
      (_, _) {},
    ); // nothing but a listener keeps a rebuilt provider running
    await c.read(coolingFanProvider.future);
    await c
        .read(coolingFanProvider.notifier)
        .choose(const CoolingFan(deviceId: 'box', portId: 1));
    expect(api.puts.single, const CoolingFan(deviceId: 'box', portId: 1));
    expect(
      c.read(coolingFanProvider).value,
      const CoolingFan(deviceId: 'box', portId: 1),
    );
  });

  testWidgets('the full UI asks once, with a snackbar', (tester) async {
    final asked = _Mem();
    final c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWithValue(_Api()),
        profileManagementProvider.overrideWith(() => _Profiles('p1')),
        cameraStatusProvider.overrideWith(_Camera.new),
        switchListProvider.overrideWith(() => _Switches([_box()])),
        coolingFanAskedPrefsProvider.overrideWith((ref, id) => asked),
      ],
    );
    addTearDown(c.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: CoolingFanPromptListener(child: SizedBox())),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.textContaining('Settings › Camera'), findsOneWidget);
    expect(asked.value, isTrue);
  });

  group('asking', () {
    Future<ProviderContainer> rig({
      bool cooler = true,
      String cameraState = 'connected',
      List<SwitchDevice>? switches,
      CoolingFan fan = CoolingFan.none,
      Completer<CoolingFan>? slowFan,
    }) async {
      final api = _Api()..current = fan;
      final c = ProviderContainer(
        overrides: [
          profileApiProvider.overrideWithValue(api),
          profileManagementProvider.overrideWith(() => _Profiles('p1')),
          cameraStatusProvider.overrideWith(
            () => _Camera(cooler: cooler, wireState: cameraState),
          ),
          switchListProvider.overrideWith(
            () => _Switches(switches ?? [_box()]),
          ),
          if (slowFan != null)
            coolingFanProvider.overrideWith(() => _SlowFan(slowFan)),
        ],
      );
      addTearDown(c.dispose);
      await c.read(cameraStatusProvider.future);
      await c.read(switchListProvider.future);
      // The ask watches the fan only once the rig qualifies; let it subscribe and land.
      c.listen(coolingFanAskDueProvider, (_, _) {});
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      return c;
    }

    test('due with a connected cooling camera, a switch and no fan', () async {
      expect((await rig()).read(coolingFanAskDueProvider), isTrue);
    });

    test('not due without a cooler, a connected camera or a switch', () async {
      expect(
        (await rig(cooler: false)).read(coolingFanAskDueProvider),
        isFalse,
      );
      expect(
        (await rig(cameraState: 'disconnected')).read(coolingFanAskDueProvider),
        isFalse,
      );
      expect(
        (await rig(switches: const [])).read(coolingFanAskDueProvider),
        isFalse,
      );
    });

    test('not due once a fan is chosen', () async {
      expect(
        (await rig(fan: const CoolingFan(deviceId: 'box', portId: 0)))
            .read(coolingFanAskDueProvider),
        isFalse,
      );
    });

    test('not due while the profile has not been read', () async {
      final slow = Completer<CoolingFan>();
      expect(
        (await rig(slowFan: slow)).read(coolingFanAskDueProvider),
        isFalse,
        reason: 'a rig that has a fan must not be asked on the way up',
      );
    });

    test('the ask is claimed once per profile', () async {
      final mem = _Mem();
      final c = ProviderContainer(
        overrides: [
          profileManagementProvider.overrideWith(() => _Profiles('p1')),
          coolingFanAskedPrefsProvider.overrideWith((ref, id) => mem),
        ],
      );
      addTearDown(c.dispose);
      expect(await claimCoolingFanAsk(c.read), isTrue);
      expect(await claimCoolingFanAsk(c.read), isFalse);
    });
  });
}

class _SlowFan extends CoolingFanNotifier {
  _SlowFan(this.gate);
  final Completer<CoolingFan> gate;
  @override
  Future<CoolingFan> build() => gate.future;
}
