import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_matching.dart';
import 'package:openastroara/state/album/album_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/sky_atlas/site_location_state.dart';

/// Sky Album slice 8 (#1595, ADR 0001): the device's collection cache fetches
/// each rig with its own `since`, merges by frame id, never removes a row, and
/// survives a restart in its JSON file.
void main() {
  AlbumFrame row(String id, int day) => AlbumFrame(frameId: id, updatedUtc: DateTime.utc(2026, 3, day));

  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('album_cache'));
  tearDown(() => dir.deleteSync(recursive: true));
  AlbumCollectionStore store() => AlbumCollectionStore(supportDir: () async => dir);

  ProviderContainer container(AlbumFetcher fetch, {List<Override> more = const []}) {
    final c = ProviderContainer(overrides: [
      albumCollectionStoreProvider.overrideWithValue(store()),
      albumFetcherProvider.overrideWithValue(fetch),
      ...more,
    ]);
    addTearDown(c.dispose);
    return c;
  }

  test('fetch with since per rig, merge, never remove, keep rows on failure', () async {
    final calls = <(String, DateTime?)>[];
    final replies = <AlbumFramesPage Function()>[
      () => AlbumFramesPage(totalLights: 2, frames: [row('a', 1), row('b', 2)]),
      // since reply: b moved, a is untouched.
      () => AlbumFramesPage(totalLights: 2, frames: [row('b', 4)]),
      // The rig deleted a: nothing new and a smaller total. Informational only.
      () => const AlbumFramesPage(totalLights: 1, frames: []),
      // A second rig starts from nothing.
      () => AlbumFramesPage(totalLights: 1, frames: [row('c', 3)]),
      () => throw StateError('offline'),
    ];
    final c = container((baseUrl, {since}) async {
      calls.add((baseUrl, since));
      return replies.removeAt(0)();
    });
    final album = c.read(albumFramesProvider.notifier);

    await album.refresh('http://rig:1888');
    expect(c.read(albumFramesProvider).value!.frames.keys, unorderedEquals(['a', 'b']));
    await album.refresh('http://rig:1888');
    expect(c.read(albumFramesProvider).value!.frames['b']!.updatedUtc, DateTime.utc(2026, 3, 4));
    await album.refresh('http://rig:1888');
    expect(c.read(albumFramesProvider).value!.frames.keys, unorderedEquals(['a', 'b']),
        reason: 'a frame deleted on the rig stays collected');
    await album.refresh('http://other:1888');
    expect(calls, [
      ('http://rig:1888', null),
      ('http://rig:1888', DateTime.utc(2026, 3, 2)),
      ('http://rig:1888', DateTime.utc(2026, 3, 4)),
      ('http://other:1888', null),
    ]);
    expect(c.read(albumFramesProvider).value!.frames.keys, unorderedEquals(['a', 'b', 'c']), reason: 'merged across rigs');

    await expectLater(album.refresh('http://rig:1888'), throwsStateError);
    expect(c.read(albumFramesProvider).value!.frames.length, 3, reason: 'a failed fetch keeps the collection');
  });

  test('the cache file survives an app restart', () async {
    final first = container((baseUrl, {since}) async => AlbumFramesPage(totalLights: 2, frames: [row('a', 1), row('b', 2)]));
    await first.read(albumFramesProvider.notifier).refresh('http://rig:1888');
    first.dispose();

    final sinces = <DateTime?>[];
    final second = container((baseUrl, {since}) async {
      sinces.add(since);
      return const AlbumFramesPage(totalLights: 0, frames: []);
    });
    final restored = await second.read(albumFramesProvider.future);
    expect(restored.frames.keys, unorderedEquals(['a', 'b']));
    await second.read(albumFramesProvider.notifier).refresh('http://rig:1888');
    expect(sinces, [DateTime.utc(2026, 3, 2)], reason: 'the rig\'s since came back with the rows');
  });

  test('a failed write leaves the saved collection whole', () async {
    final s = store();
    await s.save(AlbumCollection(frames: {'a': row('a', 1)}));
    Directory('${dir.path}/album_collection.json.part').createSync();
    await expectLater(s.save(AlbumCollection(frames: {'b': row('b', 2)})), throwsA(anything));
    expect((await store().load()).frames.keys, ['a']);
  });

  test('a missing or corrupt cache file is an empty collection', () async {
    expect((await store().load()).frames, isEmpty);
    File('${dir.path}/album_collection.json').writeAsStringSync('{not json');
    expect((await store().load()).frames, isEmpty);
  });

  test('a Book is matched against the full bundled catalog', () async {
    PlanningDso dso(String id, {int? m, int? c}) =>
        PlanningDso(id: id, name: id, type: 'G', magnitude: 5, raDeg: 10, decDeg: 20, messierNum: m, caldwellNum: c);
    final c = container(
        (baseUrl, {since}) async => const AlbumFramesPage(totalLights: 1, frames: [
              AlbumFrame(frameId: 'f', raDeg: 10, decDeg: 20, width: 100, height: 100, pixelScaleArcsec: 1, exposureSeconds: 60),
            ]),
        more: [
          bundledCatalogProvider.overrideWith((ref) async => [dso('NGC2', m: 2), dso('NGC1', m: 1), dso('NGC9', c: 9)]),
        ]);
    await c.read(albumFramesProvider.notifier).refresh('http://rig:1888');

    final view = await c.read(albumBookProvider(AlbumBook.messier).future);
    expect(view.objects.map((d) => d.id), ['NGC1', 'NGC2']);
    expect(view.collected, 2, reason: 'one frame on top of both objects credits each');
    expect(view.stats['NGC1']!.tier, AlbumTier.bronze);
  });

  test('the Book dates Nights by the site longitude read from the profile', () async {
    // Sydney Opera House: 10:00 and 13:00 UTC are 20:00 and 23:00 by the sun, one Night.
    AlbumFrame at(String id, int hour) => AlbumFrame(
        frameId: id, raDeg: 10, decDeg: 20, width: 100, height: 100, pixelScaleArcsec: 1, exposureSeconds: 60,
        capturedUtc: DateTime.utc(2026, 3, 2, hour));
    final c = container(
        (baseUrl, {since}) async => AlbumFramesPage(totalLights: 2, frames: [at('a', 10), at('b', 13)]),
        more: [
          bundledCatalogProvider.overrideWith((ref) async => [
                PlanningDso(id: 'NGC1', name: 'NGC1', type: 'G', magnitude: 5, raDeg: 10, decDeg: 20, messierNum: 1),
              ]),
          siteLocationProvider.overrideWith((ref) async => const SiteLocation(latitudeDeg: -33.856784, longitudeDeg: 151.215297, elevationM: 42)),
        ]);
    c.listen(siteLocationProvider, (_, _) {});
    await c.read(siteLocationProvider.future);
    await c.read(albumFramesProvider.notifier).refresh('http://rig:1888');
    final view = await c.read(albumBookProvider(AlbumBook.messier).future);
    expect(view.stats['NGC1']!.nights.map((n) => (n.key, n.lights)), [('2026-03-02', 2)]);
  });

  test('the fetch reads the daemon\'s snake_case reply and sends since as UTC', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    addTearDown(() => server.close(force: true));
    final queries = <Map<String, String>>[];
    Object body = {
      'total_lights': 7,
      'frames': [
        {'frame_id': 'f1', 'target_name': 'M31', 'ra_deg': 10.68, 'updated_utc': '2026-03-02T11:20:31Z'},
        {'target_name': 'no id'}, // no frame_id: dropped
      ],
    };
    server.listen((req) {
      queries.add(req.uri.queryParameters);
      expect(req.uri.path, '/api/v1/stats/album-frames');
      req.response
        ..headers.contentType = ContentType.json
        ..write(jsonEncode(body))
        ..close();
    });
    final base = 'http://127.0.0.1:${server.port}';

    final page = await fetchAlbumFrames(base, since: DateTime.parse('2026-03-02T23:20:31+12:00'));
    expect(page.totalLights, 7);
    expect(page.frames.map((f) => (f.frameId, f.targetName, f.raDeg)), [('f1', 'M31', 10.68)]);
    expect(queries.single['since'], '2026-03-02T11:20:31.000Z');

    await fetchAlbumFrames(base);
    expect(queries.last.containsKey('since'), isFalse, reason: 'a full fetch sends no since');

    body = {'total_lights': 0};
    await expectLater(fetchAlbumFrames(base), throwsA(isA<FormatException>()));
  });
}
