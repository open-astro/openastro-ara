import 'dart:collection';

import 'package:flutter/foundation.dart';
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

  /// Per-connection resync the server sends once after every WS accept: the
  /// full open-issue set. Folding it replaces the open roll-up wholesale, so
  /// a `cleared` missed while the socket was down can no longer leave an
  /// issue stuck amber/red (the §51 reconnect-replay gap).
  static const snapshot = 'diagnostics.snapshot';
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

  /// Bounds both the recent-event [_log] and the open-issue roll-up [_open].
  /// A real daemon's open-type count is far below this; the cap is purely a
  /// defence against a misbehaving server. Shared deliberately — split into two
  /// limits if a caller ever needs a short log without also shrinking the cap.
  final int maxEvents;
  // Most-recent-first ring of recent entries. ListQueue gives O(1) prepend
  // (addFirst) and O(1) bounded eviction (removeLast); the snapshot copies it
  // out to a List for the panel's indexed rendering.
  final ListQueue<DiagnosticEvent> _log = ListQueue<DiagnosticEvent>();
  // event_type → severity level of the latest still-open issue of that type.
  final Map<String, StatusLevel> _open = <String, StatusLevel>{};

  /// Fold one event and return the new snapshot, or `null` if the event was not
  /// one this accumulator folds (a non-diagnostics type, or a `diagnostics.*`
  /// subtype not yet handled). Returning null lets the caller skip a no-op
  /// state assignment — [DiagnosticsSnapshot] has reference identity, so
  /// assigning an unchanged-but-new object would churn watchers needlessly.
  DiagnosticsSnapshot? apply(WsEvent event) {
    final bool changed;
    switch (event.type) {
      case DiagnosticsWsEvents.issueDetected:
      case DiagnosticsWsEvents.autoActionTaken:
        _applyIssue(event);
        changed = true;
      case DiagnosticsWsEvents.cleared:
        changed = _applyCleared(event);
      case DiagnosticsWsEvents.snapshot:
        changed = _applySnapshot(event);
      default:
        return null;
    }
    return changed ? snapshot : null;
  }

  /// Current roll-up. Empty state (no open issues, no log) reads as nominal.
  /// Each call returns a fresh object — [DiagnosticsSnapshot] has reference
  /// identity (no value `==`), so callers must not compare snapshots by value;
  /// change is signalled by [apply] returning non-null, not by snapshot equality.
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
    final eventType = _string(p['event_type']);
    final source = eventType ?? 'unknown';
    final level = severityToLevel(_string(p['severity']));
    final description = _string(p['description']) ?? source;
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
    // Only identifiable issues enter the open roll-up. An event_type-less issue
    // is unclearable (see _applyCleared), so tracking it would permanently
    // inflate the count and grow _open without bound under a misbehaving server
    // flooding malformed frames — log it (the _log is bounded) but don't count it.
    if (eventType != null) {
      // remove-then-insert re-positions a re-detected type at the tail, so the
      // map orders by *most-recently-seen* and the cap below evicts LRU (not the
      // type's stale original position).
      _open.remove(eventType);
      _open[eventType] = level;
      // Defence-in-depth: real daemons emit a small, fixed vocabulary of types,
      // but cap _open too so a server flooding many *distinct* valid types can't
      // grow it without bound. Evict the least-recently-seen key (the head), and
      // trace it — this is unreachable under a conformant server, so the cap
      // should never be silent if it ever fires.
      if (_open.length > maxEvents) {
        final evicted = _open.keys.first;
        _open.remove(evicted);
        debugPrint('DiagnosticsAccumulator: open-issue cap ($maxEvents) hit — '
            'evicted least-recently-seen type "$evicted"');
      }
    }
    _append(DiagnosticEvent(
      timestamp: event.ts,
      level: level,
      source: source,
      message: message,
    ));
  }

  /// Returns whether the clear changed state (a matching open issue was
  /// removed), so the caller can suppress a no-op rebuild.
  bool _applyCleared(WsEvent event) {
    final eventType = _string(event.payload['event_type']);
    // A clear with no event_type can't identify which open issue to drop, so it
    // removes nothing — symmetric with _applyIssue giving each event_type-less
    // issue a seq-unique key: an unidentifiable issue is, by design, unclearable.
    // Only log "Cleared" when a removal actually happened, so a duplicate or
    // unmatched clear doesn't surface a phantom entry for a non-existent issue.
    if (eventType == null || _open.remove(eventType) == null) {
      return false;
    }
    _append(DiagnosticEvent(
      timestamp: event.ts,
      level: StatusLevel.connected,
      source: eventType,
      message: 'Cleared',
    ));
    return true;
  }

  /// Folds a `diagnostics.snapshot` resync: replaces the open roll-up with the
  /// server's authoritative open-issue set. This is what heals the stuck-pill
  /// case — a `cleared` missed while the socket was down leaves a type in
  /// [_open] that the snapshot (sent on every reconnect) no longer lists.
  /// Returns whether the roll-up actually changed, so an identical snapshot
  /// (every reconnect where nothing was missed) doesn't churn watchers.
  ///
  /// The event log is deliberately untouched: a resync is transport
  /// bookkeeping, not a new happening — entries for the issues themselves
  /// arrived (or were missed) as their own events.
  bool _applySnapshot(WsEvent event) {
    final issues = event.payload['open_issues'];
    // A snapshot without a well-formed list is malformed — ignore it rather
    // than treating it as "no open issues" and wrongly clearing real state.
    // An EMPTY list is meaningful (all clear) and falls through below.
    if (issues is! List) return false;
    final next = <String, StatusLevel>{};
    for (final entry in issues) {
      if (entry is! Map) continue;
      final eventType = _string(entry['event_type']);
      // Same rule as _applyIssue: an event_type-less issue is unclearable, so
      // it never enters the roll-up. Same bound too — a misbehaving server
      // can't grow the map past the cap (keep the FIRST maxEvents; there is
      // no recency to preserve inside one snapshot).
      if (eventType == null || next.length >= maxEvents) continue;
      next[eventType] = severityToLevel(_string(entry['severity']));
    }
    if (next.length == _open.length &&
        next.entries.every((e) => _open[e.key] == e.value)) {
      return false; // identical roll-up — no churn
    }
    _open
      ..clear()
      ..addAll(next);
    return true;
  }

  void _append(DiagnosticEvent entry) {
    _log.addFirst(entry); // most-recent first (panel renders top-down)
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

  // Severity ordering for the worst-open roll-up. disconnected ranks lowest and
  // is unreachable here (severityToLevel never yields it); it's listed only to
  // keep the switch exhaustive if a StatusLevel is added later.
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
    // Reconnect resync: events emitted while the socket was down are still
    // lost from the LOG, but the open-issue roll-up self-heals — the server
    // sends a `diagnostics.snapshot` (the full open set) on every WS accept,
    // and _applySnapshot replaces _open from it. A missed cleared can no
    // longer leave the §51 pill stuck amber/red.
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      // Cheap early-out for the non-diagnostics majority by routing prefix
      // (matching the contract in [DiagnosticsWsEvents]); a new subtype needs no
      // edit here. apply() returns null when it didn't fold the event (a
      // not-yet-handled subtype, or a no-op clear) — only assign on a real
      // change so unchanged-but-new snapshots don't churn watchers.
      if (event == null || !event.type.startsWith(DiagnosticsWsEvents.prefix)) {
        return;
      }
      // apply() returns non-null only on a real change — that's the change
      // signal (DiagnosticsSnapshot has reference identity, no value ==), so
      // never assign `acc.snapshot` here unconditionally or watchers will churn.
      final folded = acc.apply(event);
      if (folded != null) state = folded;
    });
    return acc.snapshot;
  }
}

/// §51 diagnostics snapshot for the active server. Intentionally **not**
/// autoDispose: the health roll-up must persist app-wide (it keeps
/// accumulating while you're on another tab, not just while the Imaging pill is
/// on screen). Because it `ref.watch`es [wsEventStreamProvider] in build(), this
/// notifier itself holds the WS stream open for the whole app session — by
/// design, so diagnostics keep flowing even when no diagnostics widget is
/// mounted. Rebuilds (fresh roll-up) when the active server's stream changes.
final diagnosticsStateProvider =
    NotifierProvider<DiagnosticsNotifier, DiagnosticsSnapshot>(
        DiagnosticsNotifier.new);
