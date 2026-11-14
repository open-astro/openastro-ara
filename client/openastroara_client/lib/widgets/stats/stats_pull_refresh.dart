import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The refreshes of the Stats sections on screen; the page's pull down runs
/// them all.
final statsPullRefreshersProvider = Provider<Set<Future<void> Function()>>(
  (ref) => {},
);

/// A Stats section's state: joins [statsPullRefreshersProvider] while it is on
/// screen, so the page's pull down runs [pullRefresh].
mixin StatsPullRefresh<T extends ConsumerStatefulWidget> on ConsumerState<T> {
  late final Set<Future<void> Function()> _refreshers;

  Future<void> pullRefresh();

  @override
  void initState() {
    super.initState();
    _refreshers = ref.read(statsPullRefreshersProvider)..add(pullRefresh);
  }

  @override
  void dispose() {
    _refreshers.remove(pullRefresh);
    super.dispose();
  }
}
