import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/album/sky_album_screen.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_state.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';

/// Sky Album slice 5 (#1520, docs/specs/sky-album-v1.md "Layout" and
/// "Tonight's Sky tie-in"): the screen with a fake callback and neither shell.

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

Future<void> _pump(
  WidgetTester tester, {
  required List<AlbumFrame> frames,
  List<PlanningDso> tonight = const [],
  void Function(PlanningDso)? onImageTarget,
}) async {
  tester.view.physicalSize = const Size(1200, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      activeServerProvider.overrideWithValue(const AraServer(hostname: 'rig', port: 1888)),
      bundledCatalogProvider.overrideWith((ref) async => [_m42, _c14, _m31, _m1]),
      albumFetcherProvider.overrideWithValue(
          (baseUrl, {since}) async => AlbumFramesPage(totalLights: frames.length, frames: frames)),
      tonightSkyProvider.overrideWith((ref) async => [for (final d in tonight) _tonight(d)]),
      cachedProfilesProvider.overrideWith((ref) async => const CachedProfileList(
          activeId: 'p1', profiles: [CachedProfile(id: 'p1', name: 'Backyard refractor', hasSections: false)])),
    ],
    child: MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SkyAlbumScreen(onImageTarget: onImageTarget ?? (_) {}),
    ),
  ));
  await tester.pumpAndSettle();
}

// Two hours on M31 (silver), by target name: no pointing stored.
final _m31Frames = [
  for (var i = 0; i < 4; i++)
    AlbumFrame(frameId: 'f$i', targetName: 'M 31', exposureSeconds: 1800, filterName: 'L',
        compositeScore: 0.5 + i / 10, profileId: 'p1', capturedUtc: DateTime.utc(2026, 3, 2 + i)),
];

void main() {
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

  testWidgets('a collected card shows its figures and offers more time', (tester) async {
    PlanningDso? picked;
    await _pump(tester, frames: _m31Frames, onImageTarget: (d) => picked = d);

    expect(find.text('1 / 3 collected'), findsOneWidget);
    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pumpAndSettle();
    expect(find.text('Backyard refractor'), findsOneWidget);
    expect(find.text('2.0 h · 4 lights'), findsOneWidget);
    expect(find.text('0.80'), findsOneWidget, reason: 'best frame score');
    expect(find.text('3.0 h to gold'), findsOneWidget);

    await tester.tap(find.text('Add more time'));
    await tester.pumpAndSettle();
    expect(picked, same(_m31));
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
    expect(find.text('0 / 3 collected'), findsOneWidget);
    // The shelf keeps the ranker's order, only this Book's missing cards.
    expect(find.text('2 missing cards well placed tonight'), findsOneWidget);
  });

  testWidgets('the shelf leaves out collected cards and hides when nothing is left', (tester) async {
    await _pump(tester, frames: _m31Frames, tonight: [_m31]);
    expect(find.textContaining('well placed tonight'), findsNothing);
  });

  testWidgets('the Book switch shows the Caldwell cards', (tester) async {
    await _pump(tester, frames: _m31Frames);
    await tester.tap(find.text('Caldwell'));
    await tester.pumpAndSettle();
    expect(find.text('Double Cluster'), findsOneWidget);
    expect(find.text('0 / 1 collected'), findsOneWidget);
  });
}
