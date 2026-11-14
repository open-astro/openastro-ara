import 'dart:io';

import 'package:flutter/foundation.dart' show precisionErrorTolerance;
import 'package:flutter/material.dart';

import 'package:flutter/services.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart'
    show clockProvider;
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/flat_panel_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/rotator_status.dart';
import 'package:openastroara/models/safety_monitor_status.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/dome_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/equipment/flat_panel_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/equipment/rotator_state.dart';
import 'package:openastroara/state/equipment/safety_monitor_state.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';
import 'package:flutter_riverpod/misc.dart' show Override;

class _Prefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

/// Monitor's clock without its one-second timer, which would outlive the test.
class _Clock extends MonitorClock {
  @override
  DateTime build() => ref.watch(clockProvider)();
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

/// A phone (or, with [phone] false, an iPad-sized desktop) of [size].
Future<ProviderContainer> _pump(
  WidgetTester tester,
  Size size, {
  bool phone = true,
  bool linkUp = true,
  Locale? locale,
  List<Override> gear = const [],
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      ...gear,
      companionPlatformProvider.overrideWithValue(
        phone ? TargetPlatform.iOS : TargetPlatform.linux,
      ),
      simpleModePrefsProvider.overrideWithValue(_Prefs()),
      serverLinkUpProvider.overrideWithValue(linkUp),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      rigSetupHydratedProvider.overrideWith((ref) async => false),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      monitorClockProvider.overrideWith(_Clock.new),
      deviceBatteryProvider.overrideWith(
        (ref) => Stream.value((level: 64, charging: false)),
      ),
    ],
  );
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  return container;
}

const _portrait = Size(390, 844);
const _landscape = Size(844, 390);

/// Text from 1x to 2x, in English and Brazilian Portuguese.
const _textSizes = [
  (Locale('en'), 1.0),
  (Locale('en'), 1.5),
  (Locale('en'), 2.0),
  (Locale('pt', 'BR'), 1.0),
  (Locale('pt', 'BR'), 1.5),
  (Locale('pt', 'BR'), 2.0),
];

// The test font draws every glyph as a square, far wider than a real face,
// so fit is measured with the Roboto the Flutter SDK ships.
final _fonts =
    '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';

Future<void> _loadFonts() async {
  ByteData bytes(String f) => ByteData.view(
    Uint8List.fromList(File('$_fonts/$f').readAsBytesSync()).buffer,
  );
  final roboto = FontLoader('Roboto');
  for (final f in [
    'Roboto-Regular.ttf',
    'Roboto-Medium.ttf',
    'Roboto-Bold.ttf',
  ]) {
    roboto.addFont(Future.value(bytes(f)));
  }
  await roboto.load();
  await (FontLoader(
    'MaterialIcons',
  )..addFont(Future.value(bytes('MaterialIcons-Regular.otf')))).load();
}

Map<String, dynamic> _wire(String name) => {
  'device_id': 'd',
  'name': name,
  'state': 'connected',
};

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async =>
      CameraStatus.fromJson(_wire('ASI2600MM'));
}

class _Mount extends MountNotifier {
  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(_wire('AM5'));
}

class _Focuser extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => FocuserStatus.fromJson({
    ..._wire('EAF'),
    'runtime': {'state': 'idle', 'position': 12000},
  });
}

class _Wheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson({
    ..._wire('EFW'),
    'slots': [
      for (var i = 0; i < 7; i++) {'position': i, 'name': 'Filter $i'},
    ],
    'runtime': {'state': 'idle', 'current_slot': 0},
  });
}

class _Rotator extends RotatorNotifier {
  @override
  Future<RotatorStatus?> build() async => RotatorStatus.fromJson({
    ..._wire('Falcon'),
    'runtime': {
      'state': 'idle',
      'sky_angle_deg': 92,
      'mechanical_angle_deg': 92,
    },
  });
}

class _Dome extends DomeNotifier {
  @override
  Future<DomeStatus?> build() async => DomeStatus.fromJson({
    ..._wire('ScopeDome'),
    'capabilities': {
      'can_set_shutter': true,
      'can_set_azimuth': true,
      'can_park': true,
    },
    'runtime': {'state': 'idle', 'azimuth_deg': 212.0},
  });
}

class _Flat extends FlatPanelNotifier {
  @override
  Future<FlatPanelStatus?> build() async => FlatPanelStatus.fromJson({
    ..._wire('Flip-Flat'),
    'runtime': {
      'state': 'cover_closed',
      'max_brightness': 255,
      'light_warming': false,
      'has_cover': true,
    },
  });
}

class _Weather extends WeatherNotifier {
  @override
  Future<WeatherStatus?> build() async =>
      WeatherStatus.fromJson({..._wire('CloudWatcher'), 'temperature_c': 8.4});
}

class _Safety extends SafetyMonitorNotifier {
  @override
  Future<SafetyMonitorStatus?> build() async =>
      SafetyMonitorStatus.fromJson({..._wire('Solo'), 'safe': true});
}

class _Switches extends SwitchListNotifier {
  @override
  Future<List<SwitchDevice>> build() async => const [
    SwitchDevice(
      deviceId: 'box',
      alpacaDeviceNumber: 0,
      name: 'Pegasus Powerbox',
      connectionState: SwitchConnectionState.connected,
      ports: [
        SwitchPort(
          id: 0,
          name: 'Dew 1',
          value: 0,
          min: 0,
          max: 100,
          canWrite: true,
        ),
      ],
    ),
  ];
  @override
  Future<void> refresh([client]) async {}
}

class _Guider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(const {
    'device_id': 'g',
    'name': 'PHD2',
    'state': 'connected',
    'runtime': {'state': 'idle'},
  });
  @override
  Future<void> refresh([client]) async {}
}

/// Every device connected: the strip shows eleven.
final List<Override> _rig = [
  cameraStatusProvider.overrideWith(_Camera.new),
  mountProvider.overrideWith(_Mount.new),
  focuserProvider.overrideWith(_Focuser.new),
  filterWheelProvider.overrideWith(_Wheel.new),
  rotatorProvider.overrideWith(_Rotator.new),
  domeProvider.overrideWith(_Dome.new),
  flatPanelProvider.overrideWith(_Flat.new),
  weatherProvider.overrideWith(_Weather.new),
  safetyMonitorProvider.overrideWith(_Safety.new),
  switchListProvider.overrideWith(_Switches.new),
  guiderStatusProvider.overrideWith(_Guider.new),
];

/// The windows, by the Settings panel id the strip's icon selects.
const _windows = {
  'eq.camera': 'simple-device-camera',
  'eq.mount': 'simple-device-mount',
  'eq.focuser': 'simple-device-focuser',
  'eq.filterwheel': 'simple-device-filterwheel',
  'eq.rotator': 'simple-device-rotator',
  'eq.guider': 'simple-device-guider',
  'eq.dome': 'simple-device-dome',
  'eq.flat': 'simple-device-flat',
  'eq.switch': 'simple-device-switches',
  'eq.weather': 'simple-device-weather',
  'eq.safety': 'simple-device-safety',
};

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });
  final stop = find.byType(EmergencyStopButton);
  Rect panelOf(WidgetTester tester, String key) => tester.getRect(
    find
        .ancestor(of: find.byKey(Key(key)), matching: find.byType(SimplePanel))
        .first,
  );

  Future<void> expectHoldable(
    WidgetTester tester,
    String why,
    Locale locale,
  ) async {
    final rect = tester.getRect(stop);
    expect(rect.width, greaterThanOrEqualTo(60), reason: why);
    expect(rect.height, greaterThanOrEqualTo(60), reason: why);
    final screen = tester.view.physicalSize;
    expect(
      rect.right <= screen.width && rect.bottom <= screen.height,
      isTrue,
      reason: '$why: STOP is on the screen',
    );
    final g = await tester.startGesture(rect.center);
    await tester.pump(const Duration(milliseconds: 300));
    expect(
      find.text(lookupAppLocalizations(locale).estopCompactHold),
      findsOneWidget,
      reason: '$why: the hold started',
    );
    await g.up();
    await tester.pump();
  }

  for (final e in _windows.entries) {
    for (final (locale, scale) in _textSizes) {
      testWidgets(
        'on a phone upright (390x844) the ${e.value} window opens between the strip and the deck in $locale at text ${scale}x, STOP held',
        skip: !hasFonts,
        (tester) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          final c = await _pump(tester, _portrait, locale: locale, gear: _rig);
          c.read(selectedSettingsPanelProvider.notifier).select(e.key);
          c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
          await tester.pump(const Duration(milliseconds: 500));
          // No takeException() to hide one: an overflow fails the case.
          expect(find.byKey(Key(e.value)), findsOneWidget);
          final win = panelOf(tester, e.value);
          final strip = panelOf(tester, 'simple-ara-mark');
          final deck = tester.getRect(find.byKey(const Key('companion-deck')));
          expect(win.top, greaterThanOrEqualTo(strip.bottom));
          expect(win.bottom, lessThanOrEqualTo(deck.top));
          expect(win.left, greaterThanOrEqualTo(0));
          expect(win.right, lessThanOrEqualTo(390));
          expect(
            win.width,
            greaterThanOrEqualTo(390 - 2 * 16),
            reason: 'full width, as a section panel',
          );
          await expectHoldable(tester, e.value, locale);
        },
      );
    }
  }

  // The strip: with no devices, the note shows whole at the narrowest phones.
  for (final size in [const Size(360, 780), _portrait]) {
    for (final (locale, scale) in _textSizes) {
      testWidgets(
        'on a phone upright at $size in $locale at text ${scale}x the strip says there are no devices whole',
        skip: !hasFonts,
        (tester) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          await _pump(tester, size, locale: locale);
          final note = find.descendant(
            of: find.byKey(const Key('simple-no-devices')),
            matching: find.byType(RichText),
          );
          final p = tester.renderObject<RenderParagraph>(note);
          expect(
            p.text.toPlainText(),
            lookupAppLocalizations(locale).shellNoDevices,
          );
          expect(p.didExceedMaxLines, isFalse, reason: 'the note is cut');
          expect(
            p.size.height,
            greaterThanOrEqualTo(p.getMinIntrinsicHeight(p.size.width)),
            reason: 'the note is cut short',
          );
          expect(tester.takeException(), isNull);
          final r = tester.getRect(find.byKey(const Key('simple-no-devices')));
          expect(r.height, greaterThanOrEqualTo(44));
          expect(r.right, lessThanOrEqualTo(size.width));
        },
      );
    }
  }

  // The strip with a rig: eleven device icons in a row that swipes sideways.
  for (final size in [const Size(360, 780), _portrait]) {
    for (final (locale, scale) in _textSizes) {
      testWidgets(
        'on a phone upright at $size in $locale at text ${scale}x the strip\'s device icons swipe, each a 44 px target with 8 px between',
        skip: !hasFonts,
        (tester) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          await _pump(tester, size, locale: locale, gear: _rig);
          await tester.pump(const Duration(milliseconds: 100));
          expect(tester.takeException(), isNull);
          final row = find.byKey(const Key('simple-strip-devices'));
          expect(row, findsOneWidget);
          final chips = find.descendant(
            of: row,
            matching: find.byType(InkWell),
          );
          expect(chips, findsNWidgets(11));
          final rects = [
            for (var i = 0; i < 11; i++) tester.getRect(chips.at(i)),
          ];
          for (var i = 0; i < rects.length; i++) {
            expect(rects[i].width, greaterThanOrEqualTo(44), reason: '$i');
            expect(rects[i].height, greaterThanOrEqualTo(44), reason: '$i');
            if (i > 0) {
              expect(
                rects[i].left - rects[i - 1].right,
                greaterThanOrEqualTo(8 - precisionErrorTolerance),
                reason: 'chip $i touches the one before',
              );
            }
          }
          expect(rects.last.right, greaterThan(size.width), reason: 'swipes');
          final end = rects.last.right;
          await tester.drag(row, Offset(-end, 0));
          await tester.pump(const Duration(milliseconds: 500));
          expect(
            tester.getRect(chips.last).right,
            lessThanOrEqualTo(size.width),
            reason: 'the last one swipes into view',
          );
          await tester.tap(chips.last);
          await tester.pump(const Duration(milliseconds: 500));
          expect(find.byKey(const Key('simple-device-dome')), findsOneWidget);
        },
      );
    }
  }

  testWidgets(
    'a phone on its side (844x390) keeps the left-hand window beside the rail',
    (tester) async {
      final c = await _pump(tester, _landscape, gear: _rig);
      tester.takeException(); // the 2 px the right rail is short, #1775
      c.read(selectedSettingsPanelProvider.notifier).select('eq.focuser');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pump(const Duration(milliseconds: 500));
      final win = panelOf(tester, 'simple-device-focuser');
      expect(find.byKey(const Key('companion-deck')), findsNothing);
      expect(win.left, 104, reason: 'one inset right of the rail');
      expect(win.width, 360);
    },
  );

  testWidgets(
    'with Companion Mode off (an iPad-sized desktop) the window stays 360 wide beside the rail',
    (tester) async {
      final c = await _pump(
        tester,
        const Size(768, 1024),
        phone: false,
        gear: _rig,
      );
      c.read(selectedSettingsPanelProvider.notifier).select('eq.focuser');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pump(const Duration(milliseconds: 500));
      final win = panelOf(tester, 'simple-device-focuser');
      expect(win.left, 104);
      expect(win.width, 360);
      expect(find.byKey(const Key('simple-strip-devices')), findsNothing);
    },
  );
}
