import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/ws_event_stream.dart';
import '../state/client_settings_state.dart';
import '../state/profile_live_sync.dart';
import '../state/ws/ws_providers.dart';

/// #1644 — the always-alive end of multi-device sync. Keeps the rig's
/// client-settings loaded (the synced preferences read it), re-reads it on
/// `client_settings.updated` (except this device's own save) and after every
/// reconnect, and refreshes a profile section on `profile.updated`.
class PreferenceSyncListener extends ConsumerStatefulWidget {
  const PreferenceSyncListener({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<PreferenceSyncListener> createState() =>
      _PreferenceSyncListenerState();
}

class _PreferenceSyncListenerState
    extends ConsumerState<PreferenceSyncListener> {
  @override
  void initState() {
    super.initState();
    ref.listenManual(clientSettingsProvider, (_, _) {});
    ref.listenManual(wsEventsProvider, (_, next) {
      final event = next.asData?.value;
      if (event == null) return;
      switch (event.type) {
        case 'client_settings.updated':
          final stamp = event.payload['updated_utc'];
          unawaited(
            ref
                .read(clientSettingsProvider.notifier)
                .onUpdatedEvent(
                  stamp is String ? DateTime.tryParse(stamp)?.toUtc() : null,
                ),
          );
        case 'profile.updated':
          final section = event.payload['section'];
          if (section is String) {
            unawaited(
              applyProfileUpdated(ProviderScope.containerOf(context), section),
            );
          }
      }
    });
    // Events sent while the socket was down are gone: read the rig's copy
    // again on every return to connected.
    ref.listenManual(wsConnectionStateProvider, (prev, next) {
      final was = prev?.asData?.value;
      if (next.asData?.value == WsConnectionState.connected &&
          was != WsConnectionState.connected) {
        unawaited(ref.read(clientSettingsProvider.notifier).refetch());
      }
    });
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
