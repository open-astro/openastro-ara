import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../ws/ws_providers.dart';

/// §68.1 warn-band event token — emitted when a device connects through an
/// AlpacaBridge in the 1.2–1.5 band (supported, but a newer hub is recommended).
const String alpacaBridgeOutdatedWarnEvent = 'equipment.alpaca_bridge_outdated_warn';

/// §68.2 — the latest "newer AlpacaBridge recommended" advisory for the active
/// server, folded from the [alpacaBridgeOutdatedWarnEvent] WS event.
class AlpacaBridgeWarning {
  /// The bridge version the daemon read (may be `'unknown'` if the server ever
  /// emits the event without a parsed version).
  final String version;

  /// The minimum supported AlpacaBridge version (below it, equipment is blocked).
  final String minimum;

  /// The recommended version — the banner nudges the user toward it.
  final String recommended;

  const AlpacaBridgeWarning({
    required this.version,
    required this.minimum,
    required this.recommended,
  });

  /// Build from the WS event payload `{version, minimum, recommended}`, tolerating
  /// a missing/null field rather than dropping an otherwise-valid advisory.
  factory AlpacaBridgeWarning.fromPayload(Map<String, dynamic> payload) =>
      AlpacaBridgeWarning(
        version: (payload['version'] as String?) ?? 'unknown',
        minimum: (payload['minimum'] as String?) ?? '1.2.0',
        recommended: (payload['recommended'] as String?) ?? '1.5.0',
      );
}

/// §68.2 — holds the most recent warn-band advisory (or null) for the active
/// server. Folds the [alpacaBridgeOutdatedWarnEvent] WS event; resets to null
/// when the active server's stream changes (a fresh server starts clean).
///
/// Intentionally NOT autoDispose — like [diagnosticsStateProvider], it must keep
/// folding events app-wide so a warn fired while the user is off the Equipment
/// screen isn't missed; the banner widget that watches it can come and go.
class AlpacaBridgeWarningNotifier extends Notifier<AlpacaBridgeWarning?> {
  @override
  AlpacaBridgeWarning? build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) {
      return null;
    }
    // wsEventsProvider is a StreamProvider (delivers on the next microtask, never
    // synchronously inside build()), so `state` is always assigned after the
    // initial null below.
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != alpacaBridgeOutdatedWarnEvent) {
        return;
      }
      state = AlpacaBridgeWarning.fromPayload(event.payload);
    });
    return null;
  }
}

final alpacaBridgeWarningProvider =
    NotifierProvider<AlpacaBridgeWarningNotifier, AlpacaBridgeWarning?>(
        AlpacaBridgeWarningNotifier.new);

/// Holds the bridge version the user has dismissed the §68.2 banner for (or null).
/// Kept in a provider — NOT widget state — so the dismissal survives the banner
/// being torn down and remounted as the user navigates between settings panels
/// (the banner is conditionally mounted only on the `eq.*` panels). A later warn
/// carrying a *different* version re-shows, since this only matches that one version.
class AlpacaBridgeWarningDismissedNotifier extends Notifier<String?> {
  @override
  String? build() {
    // Reset the dismissal when the active server changes — mirrors
    // [alpacaBridgeWarningProvider], so a banner dismissed on one server doesn't
    // stay hidden on a different server that happens to report the same version.
    ref.watch(wsEventStreamProvider);
    return null;
  }

  void dismiss(String version) => state = version;
}

final alpacaBridgeWarningDismissedVersionProvider =
    NotifierProvider<AlpacaBridgeWarningDismissedNotifier, String?>(
        AlpacaBridgeWarningDismissedNotifier.new);
