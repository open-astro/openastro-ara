import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_matching.dart';
import 'package:openastroara/state/album/album_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';

/// Sky Album slice 4 (#1519): the per-connection cache fetches with `since`,
/// merges by frame id, and reloads in full when the count drifts.
void main() {
  AlbumFrame row(String id, int day) => AlbumFrame(frameId: id, updatedUtc: DateTime.utc(2026, 3, day));

  test('fetch with since, merge, reload in full on a count change, keep rows on failure', () async {
    final calls = <DateTime?>[];
    final replies = <AlbumFramesPage Function()>[
      () => AlbumFramesPage(totalLights: 2, frames: [row('a', 1), row('b', 2)]),
      // since reply: b moved, a is untouched.
      () => AlbumFramesPage(totalLights: 2, frames: [row('b', 4)]),
      // since reply after a deletion: nothing new, but the total dropped...
      () => const AlbumFramesPage(totalLights: 1, frames: []),
      // ...so the full refetch replaces the cache.
      () => AlbumFramesPage(totalLights: 1, frames: [row('b', 4)]),
      () => throw StateError('offline'),
    ];
    final container = ProviderContainer(overrides: [
      albumFetcherProvider.overrideWithValue((baseUrl, {since}) async {
        expect(baseUrl, 'http://rig:1888');
        calls.add(since);
        return replies.removeAt(0)();
      }),
    ]);
    addTearDown(container.dispose);
    final album = albumFramesProvider('http://rig:1888');

    expect((await container.read(album.future)).keys, unorderedEquals(['a', 'b']));
    expect(calls, [null]);

    await container.read(album.notifier).refresh();
    expect(calls.last, DateTime.utc(2026, 3, 2));
    expect(container.read(album).value!['b']!.updatedUtc, DateTime.utc(2026, 3, 4));

    await container.read(album.notifier).refresh();
    expect(calls.sublist(2), [DateTime.utc(2026, 3, 4), null], reason: 'count drift: one full refetch');
    expect(container.read(album).value!.keys, ['b']);

    await expectLater(container.read(album.notifier).refresh(), throwsStateError);
    expect(container.read(album).value!.keys, ['b'], reason: 'a failed fetch keeps the cached rows');
  });

  test('without updated times every fetch is full and replaces the cache', () async {
    final replies = [
      const AlbumFramesPage(totalLights: 2, frames: [AlbumFrame(frameId: 'a'), AlbumFrame(frameId: 'b')]),
      const AlbumFramesPage(totalLights: 1, frames: [AlbumFrame(frameId: 'b')]),
    ];
    final container = ProviderContainer(overrides: [
      albumFetcherProvider.overrideWithValue((baseUrl, {since}) async {
        expect(since, isNull);
        return replies.removeAt(0);
      }),
    ]);
    addTearDown(container.dispose);
    final album = albumFramesProvider('http://rig:1888');

    await container.read(album.future);
    await container.read(album.notifier).refresh();
    expect(container.read(album).value!.keys, ['b'], reason: 'a deleted frame leaves the cache');
  });

  test('a Book is matched against the full bundled catalog', () async {
    PlanningDso dso(String id, {int? m, int? c}) =>
        PlanningDso(id: id, name: id, type: 'G', magnitude: 5, raDeg: 10, decDeg: 20, messierNum: m, caldwellNum: c);
    final container = ProviderContainer(overrides: [
      bundledCatalogProvider.overrideWith((ref) async => [dso('NGC2', m: 2), dso('NGC1', m: 1), dso('NGC9', c: 9)]),
      albumFetcherProvider.overrideWithValue((baseUrl, {since}) async => const AlbumFramesPage(totalLights: 1, frames: [
            AlbumFrame(frameId: 'f', raDeg: 10, decDeg: 20, width: 100, height: 100, pixelScaleArcsec: 1, exposureSeconds: 60),
          ])),
    ]);
    addTearDown(container.dispose);

    final view = await container.read(albumBookProvider(('http://rig:1888', AlbumBook.messier)).future);
    expect(view.objects.map((d) => d.id), ['NGC1', 'NGC2']);
    expect(view.collected, 2, reason: 'one frame on top of both objects credits each');
    expect(view.stats['NGC1']!.tier, AlbumTier.bronze);
  });
}
