import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/ws_event_stream.dart';
import '../state/time_sync_state.dart';
import '../state/ws/ws_providers.dart';

/// §31.1 — "WILMA pushes time on every connect". Wraps the app shell (next to
/// the §27 ConnectionPolicyListener) and fires the best-effort time-sync push
/// on every transition INTO `connected` — first connect and every reconnect,
/// exactly the playbook's cadence. The push itself no-ops when the daemon
/// already holds a fresh, trustworthy sync.
class TimeSyncOnConnectListener extends ConsumerWidget {
  final Widget child;

  const TimeSyncOnConnectListener({super.key, required this.child});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    ref.listen(wsConnectionStateProvider, (previous, next) {
      final became = next.asData?.value;
      final was = previous?.asData?.value;
      if (became == WsConnectionState.connected &&
          was != WsConnectionState.connected) {
        unawaited(ref.read(timeSyncOnConnectProvider).syncIfNeeded());
      }
    });
    return child;
  }
}
