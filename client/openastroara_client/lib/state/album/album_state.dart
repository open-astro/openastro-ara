/// Sky Album state (docs/specs/sky-album-v1.md, "Client / State"): a cache of
/// the daemon's album rows per connection, refreshed with `since`, and each
/// Book's Cards matched against the full bundled catalog.
library;

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/dso_catalog_service.dart';
import '../sky_atlas/dso_catalog_state.dart';
import 'album_frame.dart';
import 'album_matching.dart';

/// One `GET /api/v1/stats/album-frames` reply: the rows asked for, plus the
/// daemon's count of every album light (the reload check).
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

/// The album rows of the server at the family's base URL, keyed by frame id.
/// Kept for the app's life so reopening the album, or switching back to a
/// server, fetches only what changed.
final albumFramesProvider =
    AsyncNotifierProvider.family<AlbumFramesNotifier, Map<String, AlbumFrame>, String>(AlbumFramesNotifier.new);

class AlbumFramesNotifier extends AsyncNotifier<Map<String, AlbumFrame>> {
  AlbumFramesNotifier(this.baseUrl);
  final String baseUrl;

  @override
  Future<Map<String, AlbumFrame>> build() => _sync(const {});

  /// Fetches what changed since the newest row held. Swaps the result in only
  /// on success: a failure rethrows and the cached rows stay.
  Future<void> refresh() async {
    final next = await _sync(state.value ?? const {});
    state = AsyncData(next);
  }

  Future<Map<String, AlbumFrame>> _sync(Map<String, AlbumFrame> cache) async {
    final fetch = ref.read(albumFetcherProvider);
    final since = albumSince(cache);
    final page = await fetch(baseUrl, since: since);
    // A reply without since is the whole set: it replaces the cache, so deleted frames leave.
    if (since == null) return mergeAlbumRows(const {}, page.frames);
    final merged = mergeAlbumRows(cache, page.frames);
    if (!needsFullReload(merged, totalLights: page.totalLights)) return merged;
    final full = await fetch(baseUrl);
    return mergeAlbumRows(const {}, full.frames);
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

/// [book] for the server at the base URL, matched against the full bundled
/// catalog (not the culled planning list, so every Book object is present).
final albumBookProvider = FutureProvider.family<AlbumBookView, (String, AlbumBook)>((ref, key) async {
  final (baseUrl, book) = key;
  final objects = bookObjects(await ref.watch(bundledCatalogProvider.future), book);
  final frames = await ref.watch(albumFramesProvider(baseUrl).future);
  return AlbumBookView(objects: objects, stats: aggregateBook(frames.values, objects));
});
