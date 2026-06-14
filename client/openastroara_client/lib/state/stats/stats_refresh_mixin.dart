import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Shared refresh semantics for the live §50 Stats notifiers.
///
/// [refreshUsing] re-reads and swaps the result in **only on success**: it never
/// drops to a bare loading or error state, so the last-good data stays on screen
/// during and after a failed manual refresh. A failure propagates to the caller
/// (the widget catches it to drive a stale banner + clears the header spinner);
/// `state` is left untouched. RP3 has no public `copyWithPrevious` to carry the
/// previous value through an `AsyncError`, so an error state here would blank
/// `asData?.value` and the content would vanish — hence the swap-on-success-only
/// design. The initial no-data load (and its error) is owned by `build()`.
///
/// The generation counter discards a result whose active server was switched out
/// mid-fetch: call [markBuild] at the top of `build()` (which re-runs on a server
/// change), and [refreshUsing] only writes when the captured generation still
/// matches. Concurrent refreshes are prevented at the widget layer (the refresh
/// button is disabled while one is in flight), so there's no refresh-vs-refresh
/// race to guard here.
mixin StatsRefreshMixin<T> on AsyncNotifier<T?> {
  int _generation = 0;

  /// Call once at the top of `build()` so an in-flight refresh from the previous
  /// active server is discarded when the server changes.
  void markBuild() => _generation++;

  /// Run [fetch] and, on success, swap its result into `state`. Leaves `state`
  /// untouched (and rethrows) on failure, and discards the result if the active
  /// server changed while the fetch was in flight.
  Future<void> refreshUsing(Future<T?> Function() fetch) async {
    if (!ref.mounted) return;
    final gen = _generation;
    final result = await fetch();
    if (ref.mounted && gen == _generation) state = AsyncData(result);
  }
}
