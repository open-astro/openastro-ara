// url_launcher_platform_interface / plugin_platform_interface are transitive deps of
// url_launcher, imported here only to mock the launch platform channel in tests.
// ignore_for_file: depend_on_referenced_packages
import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/foundation.dart' show debugDefaultTargetPlatformOverride;
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/album/sky_album_screen.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/album/album_art.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_matching.dart';
import 'package:openastroara/state/album/album_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';
import 'package:url_launcher_platform_interface/url_launcher_platform_interface.dart';
import 'package:openastroara/widgets/album/album_card.dart';

/// Sky Album slices 5 and 8 (#1520, #1595, docs/specs/sky-album-v1.md
/// "Layout" and "Tonight's Sky tie-in"): the screen with a fake callback and
/// neither shell, the card front on Art or the Placeholder, the baseball-card
/// back.

PlanningDso _dso(String id, String name, {int? m, int? c, double ra = 10}) =>
    PlanningDso(id: id, name: name, type: 'G', magnitude: 5, raDeg: ra, decDeg: 20, messierNum: m, caldwellNum: c,
        aliases: [if (m != null) 'M $m', if (c != null) 'C $c']);

final _m1 = _dso('NGC1952', 'Crab Nebula', m: 1, ra: 83.6);
final _m31 = _dso('NGC0224', 'Andromeda Galaxy', m: 31, ra: 10.68);
final _m42 = _dso('NGC1976', 'Orion Nebula', m: 42, ra: 83.8);
final _c14 = _dso('NGC0869', 'Double Cluster', c: 14, ra: 34.7);

TonightSkyObject _tonight(PlanningDso d) => TonightSkyObject(
    id: d.id, name: d.name, type: d.type, magnitude: d.magnitude, raDeg: d.raDeg, decDeg: d.decDeg,
    altitudeDeg: 50, maxAltitudeDeg: 70);

/// The collection cache in memory: no file in widget tests.
class _MemStore extends AlbumCollectionStore {
  AlbumCollection held = const AlbumCollection();
  @override
  Future<AlbumCollection> load() async => held;
  @override
  Future<void> save(AlbumCollection c) async => held = c;
}

Future<ProviderContainer> _pump(
  WidgetTester tester, {
  required List<AlbumFrame> frames,
  List<PlanningDso> tonight = const [],
  void Function(PlanningDso)? onImageTarget,
  AlbumFetcher? fetcher,
  List<TonightSkyObject>? tonightRows,
  bool bookPending = false,
  Map<String, AlbumArt> manifest = const {},
  Size size = const Size(1200, 1600),
  double textScale = 1,
  double bottomInset = 0,
  Locale? locale,
  // The rows were already held from an earlier open, so no card is new; the new-cards
  // shelf would otherwise repeat every collected card.
  bool seen = true,
}) async {
  tester.view.physicalSize = size;
  tester.view.padding = FakeViewPadding(bottom: bottomInset);
  tester.view.viewPadding = FakeViewPadding(bottom: bottomInset);
  tester.platformDispatcher.textScaleFactorTestValue = textScale;
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(const AraServer(hostname: 'rig', port: 1888)),
      albumCollectionStoreProvider.overrideWithValue(
          _MemStore()..held = AlbumCollection(frames: {if (seen) for (final f in frames) f.frameId: f})),
      albumManifestProvider.overrideWith((ref) async => manifest),
      bundledCatalogProvider.overrideWith((ref) async => [_m42, _c14, _m31, _m1]),
      albumFetcherProvider.overrideWithValue(fetcher ??
          (baseUrl, {since}) async => AlbumFramesPage(totalLights: frames.length, frames: frames)),
      tonightSkyProvider.overrideWith((ref) async => tonightRows ?? [for (final d in tonight) _tonight(d)]),
      if (bookPending) albumBookProvider.overrideWith((ref, key) => Completer<AlbumBookView>().future),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(onImageTarget, locale: locale)));
  await tester.pumpAndSettle();
  return container;
}

Widget _app(void Function(PlanningDso)? onImageTarget, {bool open = true, Locale? locale}) => MaterialApp(
      locale: locale,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: open ? SkyAlbumScreen(onImageTarget: onImageTarget ?? (_) {}) : const SizedBox(),
    );

Finder _onShelf(String text) =>
    find.descendant(of: find.byKey(const Key('album-shelf-tonight')), matching: find.text(text));
Finder _onNewShelf(String text) =>
    find.descendant(of: find.byKey(const Key('album-shelf-new')), matching: find.text(text));

// Two hours on M31 (silver), by target name: no pointing stored. One light a night, at local 22:00.
final _m31Frames = [
  for (var i = 0; i < 4; i++)
    AlbumFrame(frameId: 'f$i', targetName: 'M 31', exposureSeconds: 1800, filterName: 'L',
        capturedUtc: DateTime(2026, 3, 2 + i, 22).toUtc()),
];

Finder albumTierNameFinder(AlbumTier tier) => find.text(switch (tier) {
      AlbumTier.bronze => 'Bronze',
      AlbumTier.silver => 'Silver',
      AlbumTier.gold => 'Gold',
    });

void main() {
  testWidgets('the reveal fades the tier word in with the border, not in its final colour', (tester) async {
    final card = AlbumCardData(
      dso: _m31,
      label: 'M31',
      number: 31,
      bookSize: 110,
      stats: CardStats(frameCount: 5000, integrationHours: 100, firstCaptureUtc: DateTime.utc(2026), filters: const ['L']),
    );
    final tier = card.tier!;
    final word = albumTierNameFinder(tier);
    await tester.pumpWidget(MaterialApp(home: Scaffold(body: SizedBox(width: 200, height: 280, child: AlbumCardFront(card, reveal: true, revealFrom: AlbumTier.values.first == tier ? null : AlbumTier.values.first)))));
    Color colour() => tester.widget<Text>(word).style!.color!;
    expect(colour(), isNot(albumTierColor(tier)), reason: 'first frame');
    await tester.pumpAndSettle();
    expect(colour(), albumTierColor(tier));
  });

  testWidgets('a missing card\'s action calls back with that object', (tester) async {
    PlanningDso? picked;
    await _pump(tester, frames: _m31Frames, onImageTarget: (d) => picked = d);

    await tester.tap(find.text('Crab Nebula'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Image this target'));
    await tester.pumpAndSettle();

    expect(picked, same(_m1));
    expect(find.text('Image this target'), findsNothing, reason: 'the card flips back and the album hands over');
  });

  testWidgets('a collected card shows its Nights and totals and offers more time', (tester) async {
    PlanningDso? picked;
    await _pump(tester, frames: _m31Frames, onImageTarget: (d) => picked = d);

    expect(find.text('1 / 4 collected'), findsOneWidget);
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(find.text('ANDROMEDA GALAXY'), findsOneWidget, reason: 'the banner');
    expect(find.text('31 / 3'), findsOneWidget, reason: 'catalog number over the Book size');
    expect(find.text('Total · 4 nights'), findsOneWidget);
    expect(find.text('2.0 h'), findsOneWidget, reason: 'total hours, with its unit');
    expect(find.text('1800 s'), findsNWidgets(4), reason: 'one row per Night with its sub');

    await tester.tap(find.text('Add more time'));
    await tester.pumpAndSettle();
    expect(picked, same(_m31));
  });

  testWidgets('the back lists at most four Nights above a totals row with the full count', (tester) async {
    await _pump(tester, frames: [
      for (var i = 0; i < 9; i++)
        AlbumFrame(frameId: 'n$i', targetName: 'M 31', exposureSeconds: 120, capturedUtc: DateTime(2026, 3, 1 + i, 23).toUtc()),
    ]);
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(find.text('120 s'), findsNWidgets(AlbumCardBack.nightRows));
    expect(find.text('Total · 9 nights'), findsOneWidget);
    final latest = MaterialLocalizations.of(tester.element(find.byType(AlbumCardBack)))
        .formatShortMonthDay(DateTime(2026, 3, 9));
    expect(find.text(latest), findsOneWidget, reason: 'the latest Night first');
  });

  testWidgets('the back carries the bio line, the trivia and the Art credit', (tester) async {
    await _pump(tester, frames: _m31Frames, manifest: const {
      'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'ESO', licence: 'CC BY 4.0',
          distance: '2.5 million ly', trivia: 'It is approaching us.'),
    });
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(find.text('Galaxy · V 5.0 · 2.5 million ly'), findsOneWidget);
    expect(find.text('Did you know?'), findsOneWidget);
    expect(find.text('It is approaching us.'), findsOneWidget);
    expect(find.text('Art (cropped): ESO · CC BY 4.0', findRichText: true), findsOneWidget);
  });

  testWidgets('tapping the Art credit opens its licence deed', (tester) async {
    final original = UrlLauncherPlatform.instance;
    addTearDown(() => UrlLauncherPlatform.instance = original);
    final launcher = UrlLauncherPlatform.instance = _FakeUrlLauncher();
    await _pump(tester, frames: _m31Frames, manifest: const {
      'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'ESO', licence: 'CC BY 2.0'),
    });
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('album-art-credit')));
    await tester.pump();
    expect(launcher.launched, 'https://creativecommons.org/licenses/by/2.0/');
    // A 44 px row, not an inline line of text: a cold hand at the telescope still hits it.
    expect(tester.getSize(find.byKey(const Key('album-art-credit'))).height, greaterThanOrEqualTo(44));
  });

  testWidgets('pulling the collection down runs the same fetch the open runs', (tester) async {
    var fetches = 0;
    await _pump(tester, frames: _m31Frames, fetcher: (baseUrl, {since}) async {
      fetches++;
      return const AlbumFramesPage(totalLights: 0, frames: []);
    });
    expect(fetches, 1, reason: 'the open');
    await tester.fling(find.byType(CustomScrollView).first, const Offset(0, 400), 1000);
    await tester.pumpAndSettle();
    expect(fetches, 2);
  });

  testWidgets('the Art credit is announced as a link', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, frames: _m31Frames, manifest: const {
      'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'ESO', licence: 'CC BY 2.0'),
    });
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(
        tester.getSemantics(find.byKey(const Key('album-art-credit'))),
        matchesSemantics(
            isLink: true, isFocusable: true, hasTapAction: true, hasFocusAction: true,
            label: 'Art (cropped): ESO · CC BY 2.0'));
    handle.dispose();
  });

  for (final (name, launcher) in [
    ('refuses', () => _FakeUrlLauncher(result: false)),
    ('throws', () => _FakeUrlLauncher(throws: true)),
  ]) {
    testWidgets('a licence link the browser $name says so', (tester) async {
      final original = UrlLauncherPlatform.instance;
      addTearDown(() => UrlLauncherPlatform.instance = original);
      UrlLauncherPlatform.instance = launcher();
      await _pump(tester, frames: _m31Frames, manifest: const {
        'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'ESO', licence: 'CC BY 2.0'),
      });
      await tester.tap(find.text('Andromeda Galaxy'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('album-art-credit')));
      await tester.pump();
      await tester.pump();
      expect(find.text("Couldn't open the licence page"), findsOneWidget);
    });
  }

  testWidgets('a card front: the Placeholder without Art, dimmed Art while missing, colour when collected',
      (tester) async {
    const art = AlbumArt(asset: 'assets/album/art/test.webp', credit: 'ESO', licence: 'CC BY 4.0');
    await _pump(tester, frames: _m31Frames, manifest: const {'M31': art, 'M1': art});
    Finder on(String name, String key) => find.descendant(
        of: find.ancestor(of: find.text(name), matching: find.byType(AlbumCardFront)), matching: find.byKey(Key(key)));
    expect(on('Andromeda Galaxy', 'album-art'), findsOneWidget, reason: 'collected: in colour');
    expect(on('Crab Nebula', 'album-art-dimmed'), findsOneWidget, reason: 'missing: dimmed');
    expect(find.ancestor(of: on('Crab Nebula', 'album-art-dimmed'), matching: find.byType(ColorFiltered)), findsOneWidget,
        reason: 'greyed');
    // One compositing layer per missing card: the dimming is in the colour matrix, not an Opacity on top.
    expect(find.ancestor(of: on('Crab Nebula', 'album-art-dimmed'), matching: find.byType(Opacity)), findsNothing);
    expect(on('Orion Nebula', 'album-placeholder'), findsOneWidget, reason: 'no Art: the Placeholder');
  });

  testWidgets('in Portuguese the back reads the type, distance and trivia in Portuguese', (tester) async {
    await _pump(tester, frames: _m31Frames, locale: const Locale('pt', 'BR'), manifest: const {
      'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'ESO', licence: 'CC BY 4.0',
          distance: '2.5 million ly', trivia: 'It is approaching us.',
          pt: AlbumArtText(distance: '2,5 milhões de anos-luz', trivia: 'Está se aproximando de nós.')),
    });
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(find.text('Galáxia · V 5,0 · 2,5 milhões de anos-luz'), findsOneWidget, reason: 'one decimal separator');
    expect(find.text('2,0 h'), findsOneWidget, reason: 'the total hours, in the locale');
    expect(find.text('0,5'), findsNWidgets(4), reason: 'each Night\'s hours, in the locale');
    expect(find.text('Está se aproximando de nós.'), findsOneWidget);
  });

  testWidgets('the Album uses Simple Mode sizes: 44 px controls, 14 px header and back text', (tester) async {
    await _pump(tester, frames: _m31Frames, manifest: const {
      'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'ESO', licence: 'CC BY 4.0', trivia: 'Close.'),
    });
    for (final chip in find.byType(FilterChip).evaluate()) {
      expect(tester.getSize(find.byWidget(chip.widget)).height, greaterThanOrEqualTo(44));
    }
    expect(tester.getSize(find.byType(SegmentedButton<AlbumFilter>)).height, greaterThanOrEqualTo(44));
    TextStyle styleOf(Finder f) => DefaultTextStyle.of(tester.element(f)).style.merge(tester.widget<Text>(f).style);
    expect(styleOf(find.text('1 / 4 collected')).fontSize, 14);
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(styleOf(find.text('Close.')).fontSize, 14);
  });

  testWidgets('the back\'s action draws at 44 px or more on desktop', (tester) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.macOS;
    try {
      await _pump(tester, frames: _m31Frames);
      await tester.tap(find.text('Andromeda Galaxy'));
      await tester.pumpAndSettle();
      expect(tester.getSize(find.byType(FilledButton)).height, greaterThanOrEqualTo(44));
    } finally {
      debugDefaultTargetPlatformOverride = null;
    }
  });

  testWidgets('both faces write the Tier, not only colour it', (tester) async {
    await _pump(tester, frames: _m31Frames);
    Finder inFront(String t) => find.descendant(of: find.byType(AlbumCardFront), matching: find.text(t));
    expect(inFront('Silver'), findsOneWidget, reason: 'the collected card\'s front');
    expect(inFront('Bronze'), findsNothing);
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(find.descendant(of: find.byType(AlbumCardBack), matching: find.text('Silver')), findsOneWidget);
  });

  testWidgets('the flip keeps one image provider for the front art', (tester) async {
    await _pump(tester, frames: _m31Frames, manifest: const {'M31': AlbumArt(asset: 'assets/album/art/test.webp')});
    Set<int?> widths() => {
          for (final img in tester.widgetList<Image>(find.byType(Image))) (img.image as ResizeImage).width,
        };
    final grid = widths();
    await tester.tap(find.text('Andromeda Galaxy'));
    final seen = <int?>{};
    for (var i = 0; i < 10; i++) {
      await tester.pump(const Duration(milliseconds: 16));
      seen.addAll(widths());
    }
    expect(seen.difference(grid), hasLength(1), reason: 'decode widths seen during the flip: $seen');
    await tester.pumpAndSettle();
  });

  testWidgets('a missing card\'s Placeholder icon reads at 3:1 on its panel', (tester) async {
    await _pump(tester, frames: const []);
    double ratio(Color a, Color b) {
      final (x, y) = (a.computeLuminance(), b.computeLuminance());
      return (math.max(x, y) + 0.05) / (math.min(x, y) + 0.05);
    }

    final icon = tester.widget<Icon>(find.descendant(of: find.byKey(const Key('album-placeholder')), matching: find.byType(Icon)).first);
    expect(ratio(icon.color!, AraColors.bgPanelAlt), greaterThanOrEqualTo(3));
  });

  testWidgets('tapping outside a flipped card flips it back', (tester) async {
    await _pump(tester, frames: _m31Frames);
    await tester.tap(find.text('Crab Nebula'));
    await tester.pumpAndSettle();
    expect(find.text('Image this target'), findsOneWidget);
    await tester.tapAt(const Offset(5, 1590));
    await tester.pumpAndSettle();
    expect(find.text('Image this target'), findsNothing);
  });

  testWidgets('with no lights the grid is grey under one caption, and the shelf still shows', (tester) async {
    await _pump(tester, frames: const [], tonight: [_m42, _c14, _m1]);
    expect(find.text('Image a target to collect your first card'), findsOneWidget);
    expect(find.text('0 / 4 collected'), findsOneWidget);
    // The shelf keeps the ranker's order, only missing cards of the catalogs shown.
    expect(find.text('3 missing cards well placed tonight'), findsOneWidget);
  });

  testWidgets('the shelf leaves out collected cards and hides when nothing is left', (tester) async {
    await _pump(tester, frames: _m31Frames, tonight: [_m31]);
    expect(find.textContaining('well placed tonight'), findsNothing);
  });

  testWidgets('each catalog chip shows its collected count, so a card in an unselected catalog shows', (tester) async {
    await _pump(tester, frames: _m31Frames);
    expect(find.text('Messier · 1'), findsOneWidget);
    expect(find.text('Caldwell · 0'), findsOneWidget);
  });

  testWidgets('with no catalog selected every catalog shows, Messier first', (tester) async {
    await _pump(tester, frames: _m31Frames);
    Finder inGrid(String t) => find.descendant(of: find.byType(SliverGrid), matching: find.text(t));
    expect(inGrid('Andromeda Galaxy'), findsOneWidget);
    expect(inGrid('Double Cluster'), findsOneWidget);
    expect(find.text('1 / 4 collected'), findsOneWidget);
    final m = tester.getTopLeft(inGrid('Orion Nebula'));
    final c = tester.getTopLeft(inGrid('Double Cluster'));
    expect(c.dy > m.dy || (c.dy == m.dy && c.dx > m.dx), isTrue, reason: 'Caldwell after Messier');
  });

  testWidgets('selecting a catalog shows only its cards; selecting both shows all again', (tester) async {
    await _pump(tester, frames: _m31Frames);
    Finder inGrid(String t) => find.descendant(of: find.byType(SliverGrid), matching: find.text(t));
    await tester.tap(find.text('Caldwell · 0'));
    await tester.pumpAndSettle();
    expect(inGrid('Double Cluster'), findsOneWidget);
    expect(inGrid('Andromeda Galaxy'), findsNothing);
    expect(find.text('0 / 1 collected'), findsOneWidget);

    await tester.tap(find.text('Messier · 1'));
    await tester.pumpAndSettle();
    expect(inGrid('Andromeda Galaxy'), findsOneWidget);
    expect(find.text('1 / 4 collected'), findsOneWidget);

    await tester.tap(find.text('Caldwell · 0'));
    await tester.pumpAndSettle();
    expect(inGrid('Double Cluster'), findsNothing);
    expect(find.text('1 / 3 collected'), findsOneWidget);
  });

  testWidgets('a shelf card fires the same callback, in ranker order', (tester) async {
    PlanningDso? picked;
    await _pump(tester, frames: _m31Frames, tonight: [_m42, _c14, _m1], onImageTarget: (d) => picked = d);
    expect(tester.getTopLeft(_onShelf('Orion Nebula')).dx, lessThan(tester.getTopLeft(_onShelf('Crab Nebula')).dx));

    await tester.tap(_onShelf('Orion Nebula'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Image this target'));
    await tester.pumpAndSettle();
    expect(picked, same(_m42));
  });

  testWidgets('a failed first fetch shows grey cards and an error, hides the shelf, and retries on reopen',
      (tester) async {
    var fail = true;
    Future<AlbumFramesPage> fetcher(String baseUrl, {DateTime? since}) async {
      if (fail) throw StateError('rig away');
      return AlbumFramesPage(totalLights: _m31Frames.length, frames: _m31Frames);
    }

    final container = await _pump(tester, frames: const [], tonight: [_m42], fetcher: fetcher);
    expect(find.textContaining("Couldn't load the album"), findsOneWidget);
    expect(find.text('0 / 4 collected'), findsOneWidget);
    expect(find.textContaining('well placed tonight'), findsNothing,
        reason: 'without rows every card looks missing, so the shelf would mislead');

    fail = false;
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null, open: false)));
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null)));
    await tester.pumpAndSettle();
    expect(find.text('1 / 4 collected'), findsOneWidget);
    expect(find.textContaining("Couldn't load the album"), findsNothing);
  });

  testWidgets('the filter shows only collected or only missing cards', (tester) async {
    await _pump(tester, frames: _m31Frames, tonight: [_m42]);
    Finder inGrid(String t) => find.descendant(of: find.byType(SliverGrid), matching: find.text(t));

    await tester.tap(find.text('Collected'));
    await tester.pumpAndSettle();
    expect(inGrid('Andromeda Galaxy'), findsOneWidget);
    expect(inGrid('Crab Nebula'), findsNothing);
    expect(find.textContaining('well placed tonight'), findsNothing, reason: 'the shelf is missing cards');
    expect(find.text('1 / 4 collected'), findsOneWidget, reason: 'the count is the catalogs shown, not the filter');

    await tester.tap(find.text('Missing'));
    await tester.pumpAndSettle();
    expect(inGrid('Andromeda Galaxy'), findsNothing);
    expect(inGrid('Crab Nebula'), findsOneWidget);
    expect(inGrid('Orion Nebula'), findsOneWidget);

    await tester.tap(find.text('All'));
    await tester.pumpAndSettle();
    expect(inGrid('Andromeda Galaxy'), findsOneWidget);
  });

  testWidgets('the title carries the album icon', (tester) async {
    await _pump(tester, frames: const []);
    expect(find.descendant(of: find.byType(AppBar), matching: find.byIcon(Icons.style_outlined)), findsOneWidget);
  });

  testWidgets('tapping the flipped card again flips it back', (tester) async {
    await _pump(tester, frames: _m31Frames);
    await tester.tap(find.text('Crab Nebula'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Not collected yet'));
    await tester.pumpAndSettle();
    expect(find.text('Image this target'), findsNothing);
  });

  testWidgets('the tier counts sit on the right of the header', (tester) async {
    await _pump(tester, frames: _m31Frames);
    final width = tester.view.physicalSize.width;
    expect(tester.getTopLeft(find.text('1 / 4 collected')).dx, lessThan(width / 2));
    expect(tester.getTopRight(find.text('Bronze 0')).dx, greaterThan(width - 40));
  });

  testWidgets('the catalogs sit left of the filter on one row, the count below', (tester) async {
    await _pump(tester, frames: _m31Frames);
    final filter = find.byType(SegmentedButton<AlbumFilter>);
    expect(tester.getCenter(find.text('Messier · 1')).dy, closeTo(tester.getCenter(filter).dy, 4));
    expect(tester.getCenter(find.text('Caldwell · 0')).dx, lessThan(tester.getTopLeft(filter).dx));
    expect(tester.getTopLeft(find.text('1 / 4 collected')).dy, greaterThan(tester.getBottomLeft(filter).dy));
    expect(tester.getCenter(find.text('Gold 0')).dy, closeTo(tester.getCenter(find.text('1 / 4 collected')).dy, 4));
  });

  testWidgets('a phone keeps the catalogs and the filter above the count, out of the bar', (tester) async {
    await _pump(tester, frames: _m31Frames, size: const Size(400, 900));
    final countTop = tester.getTopLeft(find.text('1 / 4 collected')).dy;
    expect(tester.getBottomLeft(find.byType(SegmentedButton<AlbumFilter>)).dy, lessThan(countTop));
    expect(tester.getBottomLeft(find.text('Messier · 1')).dy, lessThan(countTop));
    expect(find.descendant(of: find.byType(AppBar), matching: find.text('Messier · 1')), findsNothing);
  });

  testWidgets('the shelf finds a card the ranker lists under another designation', (tester) async {
    // The ranker's row is M42 and the Book's object NGC1976 (alias "M 42"); another row names C 14 only as an alias.
    await _pump(tester, frames: _m31Frames, tonightRows: [
      const TonightSkyObject(id: 'M42', name: 'Orion Nebula', type: 'HII', magnitude: 4, raDeg: 83.8, decDeg: -5,
          altitudeDeg: 50, maxAltitudeDeg: 70),
      const TonightSkyObject(id: 'Double', name: 'h and chi Persei', type: 'OCl', magnitude: 4, raDeg: 34.7, decDeg: 57,
          altitudeDeg: 40, maxAltitudeDeg: 60, aliases: ['C 14']),
    ]);
    expect(_onShelf('Orion Nebula'), findsOneWidget);
    await tester.tap(find.text('Caldwell · 0'));
    await tester.pumpAndSettle();
    expect(_onShelf('Double Cluster'), findsOneWidget);
  });

  testWidgets('a failed refresh keeps the cached cards under the error', (tester) async {
    var fail = false;
    Future<AlbumFramesPage> fetcher(String baseUrl, {DateTime? since}) async {
      if (fail) throw StateError('rig away');
      return AlbumFramesPage(totalLights: _m31Frames.length, frames: _m31Frames);
    }

    final container = await _pump(tester, frames: const [], fetcher: fetcher);
    expect(find.text('1 / 4 collected'), findsOneWidget);

    fail = true;
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null, open: false)));
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null)));
    await tester.pumpAndSettle();
    expect(find.text('1 / 4 collected'), findsOneWidget);
    expect(find.textContaining("Couldn't load the album"), findsOneWidget);
  });

  testWidgets('cards new or up a tier since the last open turn to colour once, and the header says so',
      (tester) async {
    var served = [_m31Frames.first];
    Future<AlbumFramesPage> fetcher(String baseUrl, {DateTime? since}) async =>
        AlbumFramesPage(totalLights: served.length, frames: served);
    late final ProviderContainer container;
    Future<void> reopen() async {
      await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null, open: false)));
      await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null)));
      await tester.pump();
      await tester.pump();
    }

    container = await _pump(tester, frames: const [], fetcher: fetcher);
    // First open: the cache was empty, so the whole collection is new to the album. It shows
    // on a shelf at the top, so a card far down the grid (C 63 under 110 Messier cards) is seen.
    expect(find.text('1 new card'), findsOneWidget);
    expect(_onNewShelf('Andromeda Galaxy'), findsOneWidget);

    // A night later: M31 reached silver and M1 is new.
    served = [
      ..._m31Frames,
      AlbumFrame(frameId: 'm1', targetName: 'M 1', exposureSeconds: 60, capturedUtc: DateTime(2026, 3, 9, 22).toUtc()),
    ];
    await reopen();
    expect(find.byKey(const Key('album-card-reveal')), findsNWidgets(2), reason: 'M31 and M1 animate');
    await tester.pumpAndSettle();
    expect(find.text('1 new card · 1 tier up since your last visit'), findsOneWidget);
    expect(_onNewShelf('Andromeda Galaxy'), findsOneWidget);
    expect(_onNewShelf('Crab Nebula'), findsOneWidget);
    expect(find.byKey(const Key('album-card-reveal')), findsNothing, reason: 'played once, then a plain card');

    // Nothing changed since: no line, no reveal.
    await reopen();
    expect(find.byKey(const Key('album-card-reveal')), findsNothing);
    await tester.pumpAndSettle();
    expect(find.textContaining('since your last visit'), findsNothing);
    expect(find.byKey(const Key('album-shelf-new')), findsNothing);
  });

  testWidgets('a new card holds grey while the album opens, then turns to colour', (tester) async {
    // The rig answers in about 60 ms, so the reveal must wait out the page coming in.
    const art = AlbumArt(asset: 'assets/album/art/test.webp', credit: 'ESO', licence: 'CC BY 4.0');
    var served = const <AlbumFrame>[];
    final container = await _pump(tester,
        frames: const [],
        manifest: const {'M31': art},
        fetcher: (baseUrl, {since}) async => AlbumFramesPage(totalLights: served.length, frames: served));
    served = _m31Frames;
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null, open: false)));
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null)));
    await tester.pump();
    await tester.pump();
    final colourArt = find.descendant(of: find.byKey(const Key('album-shelf-new')), matching: find.byKey(const Key('album-art')));
    // The fade is the Image's own opacity: an Opacity widget would add a layer per fading card.
    double colour() => tester.widget<Image>(colourArt).opacity!.value;
    await tester.pump(const Duration(milliseconds: 400));
    expect(colour(), lessThan(0.05), reason: 'still grey while the page settles');
    expect(find.ancestor(of: colourArt, matching: find.byType(Opacity)), findsNothing);
    await tester.pump(const Duration(milliseconds: 800));
    expect(colour(), allOf(greaterThan(0.2), lessThan(0.95)), reason: 'fading in where the eye can follow it');
    await tester.pumpAndSettle();
  });

  testWidgets('the reveal decodes the Art once: its decode width holds through the fade', (tester) async {
    // A width that changes mid-fade re-decodes the Art, and the card shows black until it lands.
    const art = AlbumArt(asset: 'assets/album/art/test.webp', credit: 'ESO', licence: 'CC BY 4.0');
    var served = const <AlbumFrame>[];
    final container = await _pump(tester,
        frames: const [],
        manifest: const {'M31': art},
        fetcher: (baseUrl, {since}) async => AlbumFramesPage(totalLights: served.length, frames: served));
    served = _m31Frames;
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null, open: false)));
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null)));
    final widths = <int?>{};
    for (var i = 0; i < 25; i++) {
      await tester.pump(const Duration(milliseconds: 100));
      for (final img in tester.widgetList<Image>(
          find.descendant(of: find.byKey(const Key('album-shelf-new')), matching: find.byType(Image)))) {
        widths.add((img.image as ResizeImage).width);
      }
    }
    expect(widths, hasLength(1));
  });

  testWidgets('tapping a card mid-reveal finishes it, so the flip shows the card as it ends', (tester) async {
    // The flip draws a plain front; a reveal still running underneath would jump to colour.
    var served = const <AlbumFrame>[];
    final container = await _pump(tester,
        frames: const [],
        fetcher: (baseUrl, {since}) async => AlbumFramesPage(totalLights: served.length, frames: served));
    served = _m31Frames;
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null, open: false)));
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: _app(null)));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.byKey(const Key('album-card-reveal')), findsOneWidget);
    await tester.tap(_onNewShelf('Andromeda Galaxy'));
    await tester.pump();
    expect(find.byKey(const Key('album-card-reveal')), findsNothing);
    await tester.pumpAndSettle();
  });

  testWidgets('a screen reader hears that a card on the new shelf is new or up a tier', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, frames: _m31Frames, seen: false);
    final onShelf = find.descendant(of: find.byKey(const Key('album-shelf-new')), matching: find.byType(AlbumCard));
    expect(tester.getSemantics(onShelf).label, 'M 31 Andromeda Galaxy, Silver, new');
    handle.dispose();
  });

  testWidgets('a mouse drags the shelves sideways on desktop', (tester) async {
    // A plain wheel scrolls the page, so dragging is the mouse's way along a shelf.
    await _pump(tester, frames: const [], tonight: [_m42, _c14, _m31, _m1], size: const Size(400, 900));
    final shelf = find.byKey(const Key('album-shelf-tonight'));
    await tester.dragFrom(tester.getCenter(shelf), const Offset(-200, 0), kind: PointerDeviceKind.mouse);
    await tester.pumpAndSettle();
    final position = tester.state<ScrollableState>(find.descendant(of: shelf, matching: find.byType(Scrollable))).position;
    expect(position.pixels, greaterThan(0));
  });

  testWidgets('with reduced motion a new card shows in colour at once', (tester) async {
    tester.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(reduceMotion: true);
    addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
    await _pump(tester, frames: _m31Frames, seen: false);
    expect(find.text('1 new card'), findsOneWidget);
    expect(find.byKey(const Key('album-card-reveal')), findsNothing);
  });

  testWidgets('the shelf waits until the Book is matched against the rows', (tester) async {
    // Rows loaded but the Book not yet matched: every card would look missing, M31 included.
    await _pump(tester, frames: _m31Frames, tonight: [_m31], bookPending: true);
    expect(_onShelf('Andromeda Galaxy'), findsNothing);
  });

  // Audit of #1528: a 320 px phone and large system text must not overflow the bar or the header.
  for (final (size, scale) in [
    (const Size(320, 700), 1.0),
    (const Size(400, 900), 1.3),
    (const Size(400, 900), 2.0),
    (const Size(800, 400), 2.0),
  ]) {
    testWidgets('nothing overflows at ${size.width.toInt()} px and text ${scale}x', (tester) async {
      await _pump(tester, frames: _m31Frames, size: size, textScale: scale);
      expect(tester.takeException(), isNull);
      expect(find.text('Messier · 1'), findsOneWidget, reason: 'the catalog chips stay reachable');
    });
  }

  for (final scale in [1.0, 1.3, 2.0]) {
    testWidgets('a flipped back with every part fits a landscape phone at text ${scale}x', (tester) async {
      PlanningDso? picked;
      await _pump(tester, frames: _m31Frames, size: const Size(800, 400), textScale: scale, onImageTarget: (d) => picked = d, manifest: const {
        'M31': AlbumArt(asset: 'assets/album/art/m031.webp', credit: 'NASA, ESA, and the Hubble Heritage Team',
            licence: 'Public domain', distance: '2.5 million ly',
            trivia: 'The nearest large galaxy to the Milky Way, it is approaching us at about 110 km/s.'),
      });
      // Large text stacks the header, pushing the grid (built lazily) below the fold.
      await tester.scrollUntilVisible(find.text('Andromeda Galaxy'), 100);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Andromeda Galaxy'));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull, reason: 'no overflow');
      // A back taller than the card scrolls to its action (the test font is wider than any real one).
      await tester.ensureVisible(find.text('Add more time'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Add more time'));
      await tester.pumpAndSettle();
      expect(picked, same(_m31), reason: 'and reachable');
    });
  }

  testWidgets('a flipped card stays inside a landscape phone', (tester) async {
    await _pump(tester, frames: _m31Frames, size: const Size(800, 400));
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    final back = tester.getRect(find.byType(AlbumCardBack));
    expect(back.top, greaterThanOrEqualTo(16));
    expect(back.bottom, lessThanOrEqualTo(400 - 16 + 0.01));
  });

  testWidgets('the last grid row clears the home indicator', (tester) async {
    await _pump(tester, frames: _m31Frames, size: const Size(400, 600), bottomInset: 34);
    await tester.drag(find.byType(CustomScrollView), const Offset(0, -2000));
    await tester.pumpAndSettle();
    final last = tester.getRect(find.byType(AlbumCardFront).last);
    expect(last.bottom, lessThanOrEqualTo(600 - 34));
  });

  testWidgets('a missing card\'s label and name read at 4.5:1 on the card', (tester) async {
    // A missing card is tappable (its back offers "Image this target"), so WCAG 1.4.3 applies.
    await _pump(tester, frames: _m31Frames);
    double ratio(Color a, Color b) {
      final (x, y) = (a.computeLuminance(), b.computeLuminance());
      return (math.max(x, y) + 0.05) / (math.min(x, y) + 0.05);
    }

    for (final text in ['M 1', 'Crab Nebula']) {
      final style = tester.widget<Text>(find.descendant(of: find.byType(SliverGrid), matching: find.text(text))).style!;
      expect(ratio(style.color!, AraColors.bgPanel), greaterThanOrEqualTo(4.5), reason: text);
    }
  });

  testWidgets('a card is announced as one button with its label said once', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, frames: _m31Frames);
    final node = tester.getSemantics(find.byType(AlbumCard).first);
    final data = node.getSemanticsData();
    expect(data.flagsCollection.isButton, isTrue);
    expect(data.hasAction(SemanticsAction.tap), isTrue);
    expect(data.label, 'M 1 Crab Nebula, Not collected yet');
    // The flipped back closes through the dialog's labelled barrier; the card itself is no
    // second, unlabelled close target.
    await tester.tap(find.text('Crab Nebula'));
    await tester.pumpAndSettle();
    final back = tester.getSemantics(find.byType(AlbumCardBack)).getSemanticsData();
    expect(back.hasAction(SemanticsAction.tap), isFalse);
    handle.dispose();
  });

  test('every tier colour reads at 4.5:1 on the card panel and the page', () {
    // The card back writes the tier name in its colour on Panel Grey; the header on Night Floor.
    double ratio(Color a, Color b) {
      final (x, y) = (a.computeLuminance(), b.computeLuminance());
      return (math.max(x, y) + 0.05) / (math.min(x, y) + 0.05);
    }

    for (final t in AlbumTier.values) {
      expect(ratio(albumTierColor(t), AraColors.bgPanel), greaterThanOrEqualTo(4.5), reason: '$t');
      expect(ratio(albumTierColor(t), AraColors.bgPrimary), greaterThanOrEqualTo(4.5), reason: '$t');
    }
  });

  testWidgets('Art decodes at the card\'s width, not the file\'s', (tester) async {
    await _pump(tester, frames: _m31Frames, manifest: const {'M31': AlbumArt(asset: 'assets/album/art/test.webp')});
    final image = tester.widget<Image>(find.byType(Image).first);
    expect(image.image, isA<ResizeImage>());
    final width = tester.getSize(find.byType(Image).first).width;
    expect((image.image as ResizeImage).width, width.round(), reason: 'device pixel ratio 1 in tests');
  });
}

class _FakeUrlLauncher extends Fake with MockPlatformInterfaceMixin implements UrlLauncherPlatform {
  _FakeUrlLauncher({this.result = true, this.throws = false});
  final bool result;
  final bool throws;
  String? launched;

  @override
  Future<bool> launchUrl(String url, LaunchOptions options) async {
    launched = url;
    if (throws) throw Exception('no browser');
    return result;
  }
}
