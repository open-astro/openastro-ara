import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../models/server.dart';
import '../../services/library_api.dart';
import '../saved_server_state.dart';

/// §40 live library state (12f.2) — the factory → api → notifier trio
/// mirroring `sequence_list_state.dart`. The demo data in `library_state.dart`
/// now only feeds the Stats dashboard (until §50 live-wiring).

/// Builds a [LibraryClient] for a server. Overridable in tests.
final libraryApiFactoryProvider =
    Provider<LibraryClient Function(AraServer)>((ref) => LibraryApi.new);

/// [LibraryClient] bound to the active server, or null when none is saved.
final libraryApiProvider = Provider.autoDispose<LibraryClient?>((ref) {
  final server =
      ref.watch(savedServersProvider.select((async) => async.maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          )));
  if (server == null) return null;
  final api = ref.watch(libraryApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The catalog's sessions, newest-first. Null data = no server bound; empty =
/// a server whose catalog has no sessions yet. autoDispose so reopening the
/// Image Library always refetches.
class LiveLibrarySessionsNotifier
    extends AsyncNotifier<List<LibrarySession>?> {
  // Last-issued-wins refresh guard (same shape as SequenceListNotifier).
  int _refreshGen = 0;
  String? _nextCursor;
  bool _hasMore = false;
  bool _loadingMore = false;

  /// Whether a further page exists for the Load-more affordance.
  bool get hasMore => _hasMore;


  @override
  Future<List<LibrarySession>?> build() async {
    _refreshGen++;
    final api = ref.watch(libraryApiProvider);
    if (api == null) return null;
    final page = await api.listSessions();
    _nextCursor = page.nextCursor;
    _hasMore = page.hasMore;
    return page.items;
  }

  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    final next = await AsyncValue.guard(() async {
      final page = await api.listSessions();
      if (gen == _refreshGen) {
        _nextCursor = page.nextCursor;
        _hasMore = page.hasMore;
      }
      return page.items;
    });
    if (gen == _refreshGen) state = next;
  }

  /// Append the next page. No-op when everything is already loaded or a
  /// refresh superseded this call (generation guard).
  Future<void> loadMore() async {
    final cursor = _nextCursor;
    // The in-flight guard makes repeated taps a no-op (r1): two concurrent
    // calls would share the same cursor, double-append the page, and let the
    // slower response clobber the newer cursor.
    if (!_hasMore || cursor == null || _loadingMore) return;
    _loadingMore = true;
    final gen = _refreshGen;
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    if (api == null || current == null) {
      _loadingMore = false;
      return;
    }
    try {
      final page = await api.listSessions(cursor: cursor);
      if (gen != _refreshGen) return; // a refresh/server-switch won the race
      _nextCursor = page.nextCursor;
      _hasMore = page.hasMore;
      state = AsyncData([...current, ...page.items]);
    } catch (_) {
      // Catch-all like refresh()'s AsyncValue.guard (r4): even a TypeError
      // from a malformed page must leave the loaded pages + retry button
      // intact rather than escaping as an unhandled rejection.
    } finally {
      _loadingMore = false;
    }
  }
}

final liveLibrarySessionsProvider = AsyncNotifierProvider.autoDispose<
    LiveLibrarySessionsNotifier,
    List<LibrarySession>?>(LiveLibrarySessionsNotifier.new);

/// Per-session frame strip, loaded lazily as each card builds. Family-keyed by
/// session id; autoDispose so strips are released with the screen.
final sessionFramesProvider = FutureProvider.autoDispose
    .family<List<LibraryFrameItem>, String>((ref, sessionId) async {
  final api = ref.watch(libraryApiProvider);
  if (api == null) return const [];
  return api.sessionFrames(sessionId);
});

/// §40 header-bar filters (12f.3): narrow the library by filter name, minimum
/// rating, and a target-name search. Sessions hide when the search misses
/// their target; frame strips hide frames failing the filter/rating criteria.
class LibraryFilter {
  final String? filterName; // null = all filters
  final int minRating; // 0 = any
  final String query; // target-name substring, case-insensitive

  const LibraryFilter({this.filterName, this.minRating = 0, this.query = ''});

  bool get isActive => filterName != null || minRating > 0 || query.isNotEmpty;

  LibraryFilter copyWith({
    String? Function()? filterName,
    int? minRating,
    String? query,
  }) =>
      LibraryFilter(
        filterName: filterName != null ? filterName() : this.filterName,
        minRating: minRating ?? this.minRating,
        query: query ?? this.query,
      );

  bool matchesSession(LibrarySession s) =>
      query.isEmpty ||
      s.targetName.toLowerCase().contains(query.toLowerCase());

  bool matchesFrame(LibraryFrameItem f) =>
      (filterName == null || f.filterName == filterName) &&
      f.rating >= minRating;
}

class LibraryFilterNotifier extends Notifier<LibraryFilter> {
  @override
  LibraryFilter build() => const LibraryFilter();

  void setFilterName(String? name) =>
      state = state.copyWith(filterName: () => name);
  void setMinRating(int rating) => state = state.copyWith(minRating: rating);
  void setQuery(String query) => state = state.copyWith(query: query);
  void clear() => state = const LibraryFilter();
}

final libraryFilterProvider =
    NotifierProvider<LibraryFilterNotifier, LibraryFilter>(
        LibraryFilterNotifier.new);
