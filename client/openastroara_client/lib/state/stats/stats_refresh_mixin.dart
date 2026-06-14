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
  /// untouched (and rethrows) on failure, and discards the result if the active
  /// server changed while the fetch was in flight.
  ///
  /// The `!ref.mounted` early-return is a no-op safety valve, not a success
  /// signal: it only fires when the notifier is already being torn down (a
  /// non-autoDispose provider stays mounted for the app's life, so it's
  /// effectively unreachable), at which point there's no live widget left to
  /// observe the result anyway.
  ///
  /// Neither early-return (unmounted, or a server-switch generation mismatch)
  /// throws, so a caller can't distinguish "discarded" from "succeeded". That's
  /// intentional and harmless: a server switch re-runs `build()`, which loads
  /// fresh data and (via the widget's `ref.listen`) clears any stale-error flag,
  /// so there's nothing the caller needs to do differently in the discard case.
  Future<void> refreshUsing(Future<T?> Function() fetch) async {
    if (!ref.mounted) return;
    final gen = _generation;
    final result = await fetch();
    if (ref.mounted && gen == _generation) state = AsyncData(result);
  }
}
