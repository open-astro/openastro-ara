/// Sky Album state (docs/specs/sky-album-v1.md, "Client / State"): the
/// device's collection cache of album rows, merged across every rig and kept
/// in a JSON file (ADR 0001: a high-water mark), and each Book's Cards matched
/// against the full bundled catalog.
library;

import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../../services/dso_catalog_service.dart';
import '../sky_atlas/dso_catalog_state.dart';
import '../sky_atlas/site_location_state.dart';
import 'album_frame.dart';
import 'album_matching.dart';

/// One `GET /api/v1/stats/album-frames` reply: the rows asked for, plus the
/// daemon's count of every album light (informational only).
class AlbumFramesPage {
  final int totalLights;
  final List<AlbumFrame> frames;
  const AlbumFramesPage({required this.totalLights, required this.frames});
}

typedef AlbumFetcher = Future<AlbumFramesPage> Function(String baseUrl, {DateTime? since});

/// The album fetch; overridden in tests.
final albumFetcherProvider = Provider<AlbumFetcher>((ref) => fetchAlbumFrames);

Future<AlbumFramesPage> fetchAlbumFrames(String baseUrl, {DateTime? since}) async {
  final dio = Dio(BaseOptions(
    baseUrl: baseUrl,
    connectTimeout: const Duration(seconds: 3),
    receiveTimeout: const Duration(seconds: 20),
  ));
  try {
    final res = await dio.get<dynamic>('/api/v1/stats/album-frames',
        queryParameters: {if (since != null) 'since': since.toUtc().toIso8601String()});
    final data = res.data;
    if (data is! Map<String, dynamic> || data['frames'] is! List) {
      throw const FormatException('stats/album-frames body has no frames array');
    }
    final total = data['total_lights'];
    return AlbumFramesPage(
      totalLights: total is num ? total.toInt() : 0,
      frames: [
        for (final r in (data['frames'] as List).whereType<Map<String, dynamic>>())
          if (r['frame_id'] is String) AlbumFrame.fromJson(r),
      ],
    );
  } finally {
    dio.close();
  }
}

/// The device's collection: every album row any rig has served, by frame id,
/// and per rig (base URL) the newest updated time it served, its next `since`.
class AlbumCollection {
  final Map<String, AlbumFrame> frames;
  final Map<String, DateTime> since;
  const AlbumCollection({this.frames = const {}, this.since = const {}});
}

/// The collection cache file in the app support directory, the pattern the
/// safety alarm prefs use. A missing or unreadable file is an empty collection.
class AlbumCollectionStore {
  AlbumCollectionStore({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'album_collection.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async => File('${(await _supportDir()).path}/$_fileName');

  Future<AlbumCollection> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return const AlbumCollection();
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return const AlbumCollection();
      final rows = decoded['frames'];
      final since = decoded['since'];
      return AlbumCollection(
        frames: {
          if (rows is List)
            for (final r in rows.whereType<Map<String, dynamic>>())
              if (r['frame_id'] is String) r['frame_id'] as String: AlbumFrame.fromJson(r),
        },
        since: {
          if (since is Map)
            for (final MapEntry(:key, :value) in since.entries)
              if (key is String && value is String && DateTime.tryParse(value) != null)
                key: DateTime.parse(value).toUtc(),
        },
      );
    } catch (_) {
      return const AlbumCollection();
    }
  }

  Future<void> save(AlbumCollection c) {
    final task = _chain.then((_) async {
      final f = await _file();
      await f.parent.create(recursive: true);
      // Write beside, then rename: a kill mid-write leaves the old file whole (ADR 0001).
      final part = File('${f.path}.part');
      await part.writeAsString(
          jsonEncode({
            'frames': [for (final r in c.frames.values) r.toJson()],
            'since': {for (final MapEntry(:key, :value) in c.since.entries) key: value.toUtc().toIso8601String()},
          }),
          flush: true);
      await part.rename(f.path);
    });
    // A failed write must not wedge the next one.
    _chain = task.catchError((_) {});
    return task;
  }
}

final albumCollectionStoreProvider = Provider<AlbumCollectionStore>((ref) => AlbumCollectionStore());

/// The collection, loaded from the cache file and grown by [AlbumFramesNotifier.refresh].
final albumFramesProvider =
    AsyncNotifierProvider<AlbumFramesNotifier, AlbumCollection>(AlbumFramesNotifier.new);

class AlbumFramesNotifier extends AsyncNotifier<AlbumCollection> {
  @override
  Future<AlbumCollection> build() => ref.read(albumCollectionStoreProvider).load();

  /// Fetches what the rig at [baseUrl] changed since its newest row held,
  /// merges it in and saves. A failure rethrows and the collection stays.
  Future<void> refresh(String baseUrl) async {
    final held = await future;
    final page = await ref.read(albumFetcherProvider)(baseUrl, since: held.since[baseUrl]);
    final newest = [held.since[baseUrl], albumSince(page.frames)].nonNulls;
    final next = AlbumCollection(
      frames: mergeAlbumRows(held.frames, page.frames),
      since: {
        ...held.since,
        if (newest.isNotEmpty) baseUrl: newest.reduce((a, b) => a.isAfter(b) ? a : b),
      },
    );
    state = AsyncData(next);
    await ref.read(albumCollectionStoreProvider).save(next);
  }
}

/// A Book ready to show: its objects in catalog order and the stats of each
/// object some frame contains.
class AlbumBookView {
  final List<PlanningDso> objects;
  final Map<String, CardStats> stats;
  const AlbumBookView({required this.objects, required this.stats});

  int get collected => stats.length;
}

/// [book] matched against the full bundled catalog (not the culled planning
/// list, so every Book object is present).
final albumBookProvider = FutureProvider.family<AlbumBookView, AlbumBook>((ref, book) async {
  final objects = bookObjects(await ref.watch(bundledCatalogProvider.future), book);
  final collection = await ref.watch(albumFramesProvider.future);
  // The site's longitude dates the Nights; no site (offline, none set) falls back to the device.
  final site = await ref.watch(siteLocationProvider.future).then<SiteLocation?>((s) => s, onError: (_) => null);
  return AlbumBookView(objects: objects, stats: aggregateBook(collection.frames.values, objects, siteLongitudeDeg: site?.longitudeDeg));
});
