import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/ws_event.dart';
import '../../widgets/status_indicator.dart';
import '../ws/ws_providers.dart';

/// §60.9 `diagnostics.*` WS event-type tokens the §51 panel consumes (mirrors
/// `WsEventCatalog` on the server). Routing is by these strings.
abstract final class DiagnosticsWsEvents {
  static const prefix = 'diagnostics.';
  static const issueDetected = 'diagnostics.issue_detected';
  static const autoActionTaken = 'diagnostics.auto_action_taken';
  static const cleared = 'diagnostics.cleared';
}

/// Snapshot of the daemon's diagnostic state, rolled up from §60.9
/// `diagnostics.*` WS events: an overall health [level]/[label] plus a rolling
/// log of recent [events] (most-recent first).
class DiagnosticsSnapshot {
  final StatusLevel level;
  final String label;
  final List<DiagnosticEvent> events;
  const DiagnosticsSnapshot({
    required this.level,
    required this.label,
    this.events = const <DiagnosticEvent>[],
  });
}

/// One entry in the §51 diagnostic event log.
class DiagnosticEvent {
  final DateTime timestamp;
  final StatusLevel level;
  final String source;
  final String message;
  const DiagnosticEvent({
    required this.timestamp,
    required this.level,
    required this.source,
    required this.message,
  });
}

/// Severity token (`green`/`yellow`/`red` from the server) → status pill level.
/// An unknown/absent token maps to [StatusLevel.info] rather than masquerading
/// as healthy.
StatusLevel severityToLevel(String? severity) {
  switch (severity) {
    case 'red':
      return StatusLevel.error;
    case 'yellow':
      return StatusLevel.busy;
    case 'green':
      return StatusLevel.connected;
    default:
      return StatusLevel.info;
  }
}

/// Folds the live `diagnostics.*` WS event stream into a [DiagnosticsSnapshot]:
/// a rolling log of the most recent [maxEvents] entries plus the worst-severity
/// roll-up across currently-open (un-cleared) issue types. Pure — no Riverpod,
/// no I/O — so the reduction is unit-testable in isolation.
class DiagnosticsAccumulator {
  DiagnosticsAccumulator({this.maxEvents = 50});

  final int maxEvents;
  final List<DiagnosticEvent> _log = <DiagnosticEvent>[];
  // event_type → severity level of the latest still-open issue of that type.
  final Map<String, StatusLevel> _open = <String, StatusLevel>{};

  /// Apply one event and return the resulting snapshot. Non-diagnostics events
  /// (and unknown diagnostics subtypes) leave state untouched and return the
  /// prior snapshot.
  DiagnosticsSnapshot apply(WsEvent event) {
    switch (event.type) {
      case DiagnosticsWsEvents.issueDetected:
      case DiagnosticsWsEvents.autoActionTaken:
        _applyIssue(event);
      case DiagnosticsWsEvents.cleared:
        _applyCleared(event);
    }
    return snapshot;
  }

  /// Current roll-up. Empty state (no open issues, no log) reads as nominal.
  DiagnosticsSnapshot get snapshot {
    final level = _overallLevel();
    return DiagnosticsSnapshot(
      level: level,
      label: _label(level),
      events: List<DiagnosticEvent>.unmodifiable(_log),
    );
  }

  void _applyIssue(WsEvent event) {
    final p = event.payload;
    // event_type is the open-issue map key. A conformant server always sends it;
    // fall back to a seq-unique token so two distinct malformed events don't
    // collide on one key (which would under-count open issues and drop a severity).
    final eventType = _string(p['event_type']) ?? 'unknown_${event.seq}';
    final level = severityToLevel(_string(p['severity']));
    final description = _string(p['description']) ?? eventType;
    final autoTaken = p['auto_action_taken'] == true;
    final autoDesc = _string(p['auto_action_description']);
    final recommended = _string(p['recommended_action']);
    // Prefer the action context (what was done / what to do) over the bare
    // description when present, so the one-line log entry is actionable.
    final message = autoTaken && autoDesc != null
        ? '$description — $autoDesc'
        : recommended != null
            ? '$description — $recommended'
            : description;
    _open[eventType] = level;
    _append(DiagnosticEvent(
      timestamp: event.ts,
      level: level,
      source: eventType,
      message: message,
    ));
  }

  void _applyCleared(WsEvent event) {
    final eventType = _string(event.payload['event_type']);
    // A clear with no event_type can't identify which open issue to drop, so it
    // removes nothing — symmetric with _applyIssue giving each event_type-less
    // issue a seq-unique key: an unidentifiable issue is, by design, unclearable.
    if (eventType != null) {
      _open.remove(eventType);
    }
    _append(DiagnosticEvent(
      timestamp: event.ts,
      level: StatusLevel.connected,
      source: eventType ?? 'unknown',
      message: 'Cleared',
    ));
  }

  void _append(DiagnosticEvent entry) {
    _log.insert(0, entry); // most-recent first (panel renders top-down)
    if (_log.length > maxEvents) {
      _log.removeLast();
    }
  }

  StatusLevel _overallLevel() {
    if (_open.isEmpty) return StatusLevel.connected;
    return _open.values.reduce((a, b) => _rank(a) >= _rank(b) ? a : b);
  }

  String _label(StatusLevel level) {
    if (_open.isEmpty) return 'Diagnostics: nominal';
    final n = _open.length;
    final plural = n == 1 ? '' : 's';
    // The count is the honest total of open issues; the suffix names the *worst*
    // severity (not a per-severity count) so a mixed set reads accurately — and
    // so the §53 a11y text conveys severity a screen reader can't see in the
    // pill colour.
    switch (level) {
      case StatusLevel.error:
        return 'Diagnostics: $n issue$plural — critical';
      case StatusLevel.busy:
        return 'Diagnostics: $n issue$plural — warning';
      case StatusLevel.info:
        // Worst open issue had an unknown/absent severity — still name it so the
        // a11y text isn't blank for exactly the malformed-payload case.
        return 'Diagnostics: $n issue$plural — info';
      case StatusLevel.connected:
        // All open issues are green-severity — not warnings, so no suffix.
        return 'Diagnostics: $n open issue$plural';
      case StatusLevel.disconnected:
        // Not reachable from _overallLevel (it never yields disconnected), but
        // the switch is exhaustive over StatusLevel.
        return 'Diagnostics: $n open issue$plural';
    }
  }

  static int _rank(StatusLevel level) {
    switch (level) {
      case StatusLevel.error:
        return 4;
      case StatusLevel.busy:
        return 3;
      case StatusLevel.info:
        return 2;
      case StatusLevel.connected:
        return 1;
      case StatusLevel.disconnected:
        return 0;
    }
  }

  static String? _string(dynamic value) =>
      value is String && value.isNotEmpty ? value : null;
}

/// Live §51 diagnostics snapshot, driven by the active server's `diagnostics.*`
/// WS events. A fresh accumulator is built per active stream (so a server
/// switch resets the roll-up); when no server is saved the panel reads as
/// "not connected".
class DiagnosticsNotifier extends Notifier<DiagnosticsSnapshot> {
  @override
  DiagnosticsSnapshot build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) {
      return const DiagnosticsSnapshot(
        level: StatusLevel.disconnected,
        label: 'Diagnostics: not connected',
      );
    }
    final acc = DiagnosticsAccumulator();
    // The listener mutates `acc` and assigns `state`. This is safe because
    // wsEventsProvider is a StreamProvider — it delivers asynchronously (next
    // microtask), never synchronously inside this build(), so `state` is always
    // assigned after build() has returned the initial snapshot below.
    // TODO: no replay on reconnect — events the server emits while the socket is
    // down (WsEventStream auto-reconnects, so the stream stays non-null) are
    // lost, so the pill can read stale-nominal until the next live event.
    // Resolve when server-side history-on-connect lands.
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      // Filter to the `diagnostics.*` family by routing prefix (matching the
      // contract in [DiagnosticsWsEvents]); a new subtype needs no edit here.
      // The accumulator already ignores any subtype it doesn't fold.
      if (event == null || !event.type.startsWith(DiagnosticsWsEvents.prefix)) {
        return;
      }
      state = acc.apply(event);
    });
    return acc.snapshot;
  }
}

/// §51 diagnostics snapshot for the active server. Intentionally **not**
/// autoDispose: the health roll-up must persist app-wide (it keeps
/// accumulating while you're on another tab, not just while the Imaging pill is
/// on screen). The always-visible WS connection indicator already holds the
/// underlying stream open, so this adds no extra lifetime. Rebuilds (fresh
/// roll-up) when the active server's stream changes.
final diagnosticsStateProvider =
    NotifierProvider<DiagnosticsNotifier, DiagnosticsSnapshot>(
        DiagnosticsNotifier.new);
