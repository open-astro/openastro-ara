import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/usage_history.dart';
import '../../services/usage_history_api.dart';
import '../saved_server_state.dart';

/// Builds a [UsageHistoryClient] for a server. Overridable in tests.
final usageHistoryApiFactoryProvider =
    Provider<UsageHistoryClient Function(AraServer)>(
      (ref) => UsageHistoryApi.new,
    );

final usageHistoryApiProvider = Provider<UsageHistoryClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(usageHistoryApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The rig's 12 h of storage / CPU / memory readings (#1582). Loaded when the
/// panel opens, then [refresh] asks only for what is newer. AutoDispose: the
/// client keeps nothing once the panel closes; the rig is the history. `null`
/// data means no server, or a daemon without the route (404).
class UsageHistoryNotifier extends AsyncNotifier<UsageHistory?> {
  @override
  Future<UsageHistory?> build() async =>
      ref.watch(usageHistoryApiProvider)?.fetch();

  bool _inFlight = false;

  /// Skips the tick when the last read is still out (a slow rig must not stack requests).
  Future<void> refresh() async {
    final current = state.value;
    final api = ref.read(usageHistoryApiProvider);
    if (current == null || api == null || _inFlight) return;
    _inFlight = true;
    try {
      final since = current.samples.isEmpty ? null : current.samples.last.at;
      final newer = await api.fetch(since: since);
      if (newer == null || !ref.mounted) return;
      state = AsyncData(current.append(newer));
    } catch (_) {
      // A missed poll leaves the graph as it was; the next one asks again.
    } finally {
      _inFlight = false;
    }
  }
}

final usageHistoryProvider =
    AsyncNotifierProvider.autoDispose<UsageHistoryNotifier, UsageHistory?>(
      UsageHistoryNotifier.new,
    );
