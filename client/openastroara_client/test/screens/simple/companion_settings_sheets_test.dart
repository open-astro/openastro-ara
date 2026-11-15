import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/services/storage_devices_api.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/profile_management_state.dart'
    show profileApiProvider;
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/saved_server_state.dart'
    show activeServerProvider;
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart'
    show clockProvider, tonightSkyProvider;
import 'package:openastroara/state/ws/ws_providers.dart';

import '../../support/storage_fixture.dart';

import 'package:openastroara/screens/simple/simple_rig_storage.dart'
    show SimpleStorageBlock;
import 'package:openastroara/services/tonight_sky_api.dart'
    show TonightSkyObject;
import 'package:openastroara/widgets/inline_choices.dart' show InlineConfirm;
import 'package:openastroara/widgets/sky_atlas/stellarium_view.dart'
    show SkyPanelsLayout;
import 'package:openastroara/widgets/sky_atlas/tonight_sky_panel.dart';

const _store = StorageDevice(
  path: '/dev/sda1',
  uuid: 'AAAA',
  label: 'STORE',
  fileSystem: 'ext4',
  removable: true,
  isSystemDisk: false,
  isAraStore: true,
);

const _rig = AraServer(hostname: '192.0.2.10', port: 5555);

/// The drive list and its contents, with no network.
class _StorageApi extends StorageDevicesApi {
  _StorageApi() : super(_rig);
  @override
  Future<StorageContents> contents(String uuid) async =>
      StorageContents.unknown;
  @override
  void close() {}
}

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
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
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

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });

  // Each surface: how it opens on an upright phone, and a key that is on it.
  final surfaces = <String, (Future<void> Function(WidgetTester), String)>{
    'Settings': (
      (t) async => t.tap(find.byIcon(Icons.settings_outlined)),
      'simple-menu',
    ),
    'Settings, Devices': (
      (t) async {
        await t.tap(find.byIcon(Icons.settings_outlined));
        await t.pump(const Duration(milliseconds: 300));
        await t.tap(find.byKey(const Key('simple-menu-nav-Devices')));
      },
      'simple-menu',
    ),
    'Ara sheet': (
      (t) async => t.tap(find.byKey(const Key('simple-ara-mark'))),
      'simple-ara-sheet',
    ),
    'Search': (
      (t) async => t.tap(find.byKey(const Key('simple-search-button'))),
      'simple-search',
    ),
  };

  for (final e in surfaces.entries) {
    for (final (locale, scale) in _textSizes) {
      testWidgets(
        'on a phone upright (390x844) ${e.key} fits between the strip and the deck in $locale at text ${scale}x, no overflow',
        skip: !hasFonts,
        (tester) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          await _pump(tester, _portrait, locale: locale);
          await e.value.$1(tester);
          await tester.pump(const Duration(milliseconds: 500));
          expect(tester.takeException(), isNull);
          final r = tester.getRect(find.byKey(Key(e.value.$2)));
          final deck = tester.getRect(find.byKey(const Key('companion-deck')));
          expect(r.left, greaterThanOrEqualTo(0));
          expect(r.right, lessThanOrEqualTo(390));
          expect(r.bottom, lessThanOrEqualTo(deck.top));
          expect(r.width, greaterThanOrEqualTo(390 - 2 * 16));
        },
      );
    }
  }

  // Settings > Rig holds Storage and Logs & bug report below the fold: the
  // page scrolls to its end without an overflow.
  for (final (locale, scale) in _textSizes) {
    testWidgets(
      'on a phone upright Settings Rig scrolls to its end in $locale at text ${scale}x',
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        await _pump(tester, _portrait, locale: locale);
        await tester.tap(find.byIcon(Icons.settings_outlined));
        await tester.pump(const Duration(milliseconds: 300));
        final menu = find.byKey(const Key('simple-menu'));
        final page = find.descendant(
          of: menu,
          matching: find.byType(SingleChildScrollView),
        );
        for (var i = 0; i < 3; i++) {
          await tester.drag(page.first, const Offset(0, -3000));
          await tester.pump(const Duration(milliseconds: 500));
        }
        expect(tester.takeException(), isNull);
        final position = tester
            .state<ScrollableState>(
              find
                  .descendant(of: page.first, matching: find.byType(Scrollable))
                  .first,
            )
            .position;
        expect(position.maxScrollExtent, greaterThan(0));
        expect(position.extentAfter, lessThan(0.5));
        final deck = tester.getRect(find.byKey(const Key('companion-deck')));
        expect(tester.getRect(menu).bottom, lessThanOrEqualTo(deck.top));
      },
    );
  }

  // Tonight's Sky in the Sky Atlas host: full width on an upright phone,
  // docked at 360 px beside the sky in landscape.
  Future<void> pumpHost(
    WidgetTester tester,
    Size size,
    bool upright,
    Locale locale,
  ) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          tonightSkyProvider.overrideWith(
            (ref) async => [
              for (var i = 0; i < 12; i++)
                TonightSkyObject(
                  id: 'M$i',
                  name: 'Target $i',
                  type: 'galaxy',
                  magnitude: 3.4,
                  raDeg: 10.6847,
                  decDeg: 41.269,
                  altitudeDeg: 55,
                  maxAltitudeDeg: 60,
                ),
            ],
          ),
        ],
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          // The host gives the layout the page's tight size, as the Expanded
          // in StellariumView does.
          home: Scaffold(
            body: SizedBox.expand(
              child: SkyPanelsLayout(
                sky: const Expanded(child: SizedBox(key: Key('sky'))),
                tonightOpen: true,
                rotateOpen: false,
                upright: upright,
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  for (final (locale, scale) in _textSizes) {
    testWidgets(
      "Tonight's Sky fills a 390 px phone in $locale at text ${scale}x without overflow",
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        await pumpHost(tester, _portrait, true, locale);
        expect(tester.takeException(), isNull);
        expect(tester.getSize(find.byType(TonightSkyPanel)).width, 390);
        // Full height and scrolls to its end without overflow.
        expect(tester.getSize(find.byType(TonightSkyPanel)).height, 844);
        final list = find.descendant(
          of: find.byType(TonightSkyPanel),
          matching: find.byType(Scrollable),
        );
        final position = tester.state<ScrollableState>(list.first).position;
        expect(position.maxScrollExtent, greaterThan(0));
        // The list's length is an estimate until its end is built: drag twice.
        for (var i = 0; i < 2; i++) {
          await tester.drag(list.first, const Offset(0, -5000));
          await tester.pump(const Duration(milliseconds: 500));
        }
        expect(position.extentAfter, 0);
        expect(tester.takeException(), isNull);
      },
    );
  }

  testWidgets(
    "Tonight's Sky in landscape still docks at 360 px beside the sky",
    (tester) async {
      await pumpHost(tester, const Size(844, 390), false, const Locale('en'));
      expect(tester.takeException(), isNull);
      expect(tester.getSize(find.byType(TonightSkyPanel)).width, 360);
      expect(tester.getSize(find.byKey(const Key('sky'))).width, 844 - 360);
    },
  );

  Finder anyOverlay() => find.byWidgetPredicate(
    (w) => w is Dialog || w is BottomSheet || w is SnackBar || w is AlertDialog,
  );

  for (final scale in [1.0, 2.0]) {
    testWidgets(
      'on a phone upright Rig Storage opens its confirmation and drive list in place at text ${scale}x',
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        tester.view.physicalSize = _portrait;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        await tester.pumpWidget(
          ProviderScope(
            overrides: [
              activeServerProvider.overrideWithValue(_rig),
              profileApiProvider.overrideWithValue(FakeStorageApi()),
              storageDevicesApiFactoryProvider.overrideWithValue(
                (_) => _StorageApi(),
              ),
              storageDevicesProvider.overrideWith(
                (ref) async => const [_store],
              ),
              storageSpaceProvider.overrideWith(
                (ref) async => const StorageSpace(
                  saveDirectory: '/mnt/store',
                  isFallback: false,
                  freeBytes: 100,
                  totalBytes: 200,
                ),
              ),
            ],
            child: MaterialApp(
              localizationsDelegates: AppLocalizations.localizationsDelegates,
              supportedLocales: AppLocalizations.supportedLocales,
              home: const Scaffold(
                body: SingleChildScrollView(child: SimpleStorageBlock()),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
        for (final k in ['simple-storage-choose', 'simple-storage-check']) {
          await tester.ensureVisible(find.byKey(Key(k)));
          await tester.pumpAndSettle();
          await tester.tap(find.byKey(Key(k)));
          await tester.pumpAndSettle();
        }
        expect(find.byKey(const Key('simple-storage-chooser')), findsOneWidget);
        expect(find.byType(InlineConfirm), findsOneWidget);
        // Both opened in the page: no route, dialog, sheet or snackbar.
        expect(
          tester.state<NavigatorState>(find.byType(Navigator)).canPop(),
          isFalse,
        );
        expect(anyOverlay(), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'on a phone upright the Ara sheet shows Change rig and Change profile within the screen at text ${scale}x',
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        await _pump(tester, _portrait);
        await tester.tap(find.byKey(const Key('simple-ara-mark')));
        await tester.pump(const Duration(milliseconds: 500));
        for (final k in [
          'simple-ara-change-rig',
          'simple-ara-change-profile',
        ]) {
          final r = tester.getRect(find.byKey(Key(k)));
          expect(r.left, greaterThanOrEqualTo(0), reason: k);
          expect(r.right, lessThanOrEqualTo(390), reason: k);
        }
        expect(anyOverlay(), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );
  }
}
