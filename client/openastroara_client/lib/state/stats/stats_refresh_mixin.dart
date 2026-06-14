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
/// matches. **The counter guards server switches only, NOT concurrent refreshes**
/// — two overlapping [refreshUsing] calls are last-writer-wins. Callers must gate
/// concurrency themselves (the widgets disable the refresh button while one is in
/// flight), so there's no refresh-vs-refresh race in practice.
mixin StatsRefreshMixin<T> on AsyncNotifier<T?> {
  int _generation = 0;

  /// Call once at the top of `build()` so an in-flight refresh from the previous
  /// active server is discarded when the server changes.
  void markBuild() => _generation++;

  /// Run [fetch] and, on success, swap its result into `state`. Leaves `state`
  /// untouched on failure and rethrows — **unless** the active server changed
  /// while the fetch was in flight, in which case both a successful result and a
  /// failure are discarded (the rebuild owns the fresh load).
  ///
  /// The `!ref.mounted` early-return is a no-op safety valve, not a success
  /// signal: it only fires when the notifier is already being torn down (a
  /// non-autoDispose provider stays mounted for the app's life, so it's
  /// effectively unreachable), at which point there's no live widget left to
  /// observe the result anyway.
  ///
  /// On a server-switch generation mismatch neither the success nor the failure
  /// path surfaces anything: a `build()` re-runs for the new server (loading
  /// fresh data and, via the widget's `ref.listen`, clearing any stale-error
  /// flag), so a stale failure must NOT be rethrown — otherwise the widget would
  /// flash a "stale" indicator over the new server's data. A failure whose
  /// generation still matches (a genuine same-server refresh error) does rethrow
  /// so the widget can show the stale banner over the prior data.
  Future<void> refreshUsing(Future<T?> Function() fetch) async {
    if (!ref.mounted) return;
    final gen = _generation;
    try {
      final result = await fetch();
      if (ref.mounted && gen == _generation) state = AsyncData(result);
    } catch (_) {
      if (gen != _generation) return; // server switched mid-fetch — discard
      rethrow;
    }
  }
}
