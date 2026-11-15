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
    show clockProvider, tonightSkyProvider, tonightsBestProvider;
import 'package:openastroara/state/ws/ws_providers.dart';

import '../../support/storage_fixture.dart';

import 'package:openastroara/screens/simple/simple_plan_panel.dart'
    show SimpleRunSheet;
import 'package:openastroara/screens/simple/simple_rig_storage.dart'
    show SimpleStorageBlock;
import 'package:openastroara/services/dso_catalog_service.dart'
    show PlanningDso;
import 'package:openastroara/services/tonight_sky_api.dart'
    show TonightSkyObject;
import 'package:openastroara/state/album/album_art.dart'
    show AlbumArt, albumManifestProvider;
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart'
    show bundledCatalogProvider;
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart'
    show PlanetariumSelection, planetariumSelectionProvider;
import 'package:openastroara/widgets/inline_choices.dart' show InlineConfirm;

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
  List<Object> overrides = const [],
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      ...overrides.cast(),
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

  // Simple Mode's sky surfaces on an upright phone (the full UI's Sky Atlas
  // never mounts in Companion Mode): Plan's Tonight's best and its search,
  // and the card of an object picked in the Sky Map, all above the deck.
  final catalog = [
    for (final (id, name, m) in const [
      ('NGC0224', 'Andromeda Galaxy', 31),
      ('NGC0598', 'Triangulum Galaxy', 33),
      ('NGC2168', 'Shoe-Buckle Cluster', 35),
      ('NGC1960', 'Pinwheel Cluster', 36),
      ('NGC2099', 'Salt and Pepper Cluster', 37),
      ('NGC1912', 'Starfish Cluster', 38),
      ('NGC1976', 'Orion Nebula', 42),
    ])
      PlanningDso(
        id: id,
        name: name,
        type: 'G',
        magnitude: 4,
        raDeg: 10,
        decDeg: 20,
        messierNum: m,
        aliases: ['M $m'],
      ),
  ];

  for (final (locale, scale) in _textSizes) {
    testWidgets(
      "on a phone upright Plan's Tonight's best and its search fit above the deck and scroll to the end in $locale at text ${scale}x",
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        const best = ['M31', 'NGC 7000', 'M33', 'M45'];
        final tonight = [
          for (final n in best)
            TonightSkyObject(
              id: n,
              name: n,
              type: 'Galaxy',
              magnitude: 5,
              raDeg: 10,
              decDeg: 40,
              altitudeDeg: 55,
              maxAltitudeDeg: 70,
            ),
        ];
        await _pump(
          tester,
          _portrait,
          locale: locale,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => catalog),
            // Plan lists tonightsBestProvider, which ranks from the clock; a
            // pick reads tonightSkyProvider. Both fixed, as in the Plan tests.
            tonightSkyProvider.overrideWith((ref) async => tonight),
            tonightsBestProvider.overrideWith((ref) async => tonight),
          ],
        );
        // The section row swipes: past 1x text Plan starts off screen.
        final plan = find.byKey(const Key('companion-section-plan'));
        await tester.ensureVisible(plan);
        await tester.pump();
        await tester.tap(plan);
        await tester.pump(const Duration(milliseconds: 500));
        final sheet = find.byType(SimpleRunSheet);
        expect(sheet, findsOneWidget);
        await tester.enterText(
          find.byKey(const Key('simple-run-search')),
          'm3',
        );
        await tester.pump();
        await tester.pump();
        final rows = [
          for (final n in best) find.byKey(Key('simple-run-suggestion-$n')),
          // The search's matches as you type: five at most.
          for (var i = 0; i < 5; i++)
            find.byKey(Key('simple-run-suggestion-$i')),
        ];
        final r = tester.getRect(sheet);
        final deck = tester.getRect(find.byKey(const Key('companion-deck')));
        expect(r.left, greaterThanOrEqualTo(0));
        expect(r.right, lessThanOrEqualTo(390));
        expect(r.bottom, lessThanOrEqualTo(deck.top));
        for (final row in rows) {
          expect(row, findsOneWidget);
          final box = tester.getRect(row);
          expect(box.left, greaterThanOrEqualTo(r.left), reason: '$row');
          expect(box.right, lessThanOrEqualTo(r.right), reason: '$row');
        }
        final scroll = find
            .descendant(of: sheet, matching: find.byType(Scrollable))
            .first;
        final position = tester.state<ScrollableState>(scroll).position;
        expect(position.maxScrollExtent, greaterThan(0));
        for (var i = 0; i < 3; i++) {
          await tester.drag(scroll, const Offset(0, -3000));
          await tester.pump(const Duration(milliseconds: 500));
        }
        expect(position.extentAfter, lessThan(0.5));
        expect(tester.getRect(sheet).bottom, lessThanOrEqualTo(deck.top));
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'on a phone upright the Sky Map card with Image this fits above the deck in $locale at text ${scale}x',
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        final c = await _pump(
          tester,
          _portrait,
          locale: locale,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => catalog),
            // With its Art: the card at its widest.
            albumManifestProvider.overrideWith(
              (ref) async => const {
                'M42': AlbumArt(
                  asset: 'assets/album/art/m042.webp',
                  credit: 'ESO',
                  licence: 'CC BY 4.0',
                ),
              },
            ),
          ],
        );
        await tester.tap(find.byKey(const Key('simple-sky')));
        await tester.pump();
        c
            .read(planetariumSelectionProvider.notifier)
            .set(
              const PlanetariumSelection(
                name: 'Orion Nebula',
                kind: 'Nebula',
                raHours: 5.59,
                decDeg: -5.39,
                raJ2000Hours: 5.59,
                decJ2000Deg: -5.39,
                altDeg: 30,
                maxAltDeg: 45,
                designations: ['NAME Orion Nebula', 'M 42', 'NGC 1976'],
              ),
            );
        await tester.pump();
        await tester.pump(); // the manifest and the catalog resolve
        expect(find.byKey(const Key('simple-sky-art')), findsOneWidget);
        final image = find.byKey(const Key('simple-sky-image-this'));
        final card = tester.getRect(
          find.ancestor(of: image, matching: find.byType(SimplePanel)).first,
        );
        final deck = tester.getRect(find.byKey(const Key('companion-deck')));
        expect(card.left, greaterThanOrEqualTo(kSimpleInset));
        expect(card.right, lessThanOrEqualTo(390 - kSimpleInset));
        expect(card.bottom, lessThanOrEqualTo(deck.top));
        // A tap on Image this lands on it, and opens Plan's sheet above the
        // deck.
        final box = tester.renderObject(image);
        expect(
          tester
              .hitTestOnBinding(tester.getCenter(image))
              .path
              .any((e) => e.target == box),
          isTrue,
        );
        await tester.tap(image);
        await tester.pump(const Duration(milliseconds: 500));
        expect(
          tester.getRect(find.byType(SimpleRunSheet)).bottom,
          lessThanOrEqualTo(deck.top),
        );
        expect(tester.takeException(), isNull);
      },
    );
  }

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
