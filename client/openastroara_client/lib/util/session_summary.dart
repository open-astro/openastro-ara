import '../models/sequence/sequence_summary.dart';
import '../services/notifications_api.dart';

/// §58.13 / #1138 — "what the user sees when they come back": the night's
/// outcome in one card, built from what the daemon already keeps (the run's
/// final state and the notifications it posted) rather than a new report
/// endpoint. Pure — unit-tested.
class SessionSummary {
  final String sequenceId;
  final String runId;
  final String sequenceName;
  final SequenceRunState state;
  final String? targetName;
  final DateTime? startedUtc;

  /// When the run ended, or when the last incident landed for a run the
  /// daemon left paused awaiting the user (the §58.12 shutdown leaves it so).
  final DateTime? endedUtc;
  final int framesCaptured;

  /// Warning-and-above notifications posted during the run, newest first.
  final List<AraNotification> incidents;

  const SessionSummary({
    required this.sequenceId,
    required this.runId,
    required this.sequenceName,
    required this.state,
    this.targetName,
    this.startedUtc,
    this.endedUtc,
    this.framesCaptured = 0,
    this.incidents = const [],
  });

  /// "Session finished overnight" / "Session ended overnight" / "Session needs you".
  String get title => switch (state) {
    SequenceRunState.completed => 'Session finished overnight',
    SequenceRunState.pausedAwaitingUser => 'Session paused — the rig needs you',
    _ => 'Session ended overnight',
  };

  /// The one-paragraph account: what happened to which sequence and when.
  String get headline {
    final when = endedUtc == null ? '' : ' at ${_hhmm(endedUtc!)}';
    final on = targetName == null || targetName!.trim().isEmpty
        ? sequenceName
        : '$sequenceName ($targetName)';
    return switch (state) {
      SequenceRunState.completed => '$on ran to completion$when.',
      SequenceRunState.failed => '$on failed$when.',
      SequenceRunState.stopped => '$on was stopped$when.',
      SequenceRunState.pausedAwaitingUser =>
        '$on paused$when and is waiting for you — resume it or stop it.',
      _ => '$on ended$when.',
    };
  }

  String get framesLine => framesCaptured == 1
      ? '1 frame captured'
      : '$framesCaptured frames captured';

  static String _hhmm(DateTime utc) {
    final d = utc.toLocal();
    return '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';
  }
}

/// The run states a session summary is about: the run is over, or the daemon
/// parked it awaiting the user (§58.12 unattended shutdown leaves the run
/// paused so a morning resume is possible).
bool isSummaryWorthyState(SequenceRunState? s) =>
    s == SequenceRunState.completed ||
    s == SequenceRunState.failed ||
    s == SequenceRunState.stopped ||
    s == SequenceRunState.pausedAwaitingUser;

/// Incidents are warning-and-above notifications posted from the run's start
/// until shortly after it ended (the shutdown ladder posts its record a few
/// minutes after the pause), newest first, capped so the card stays a card.
List<AraNotification> incidentsFor(
  SequenceRunStateInfo run,
  Iterable<AraNotification> notifications, {
  Duration tail = const Duration(minutes: 20),
  int cap = 6,
}) {
  final start = run.startedUtc;
  if (start == null) return const [];
  final end = (run.completedUtc ?? DateTime.now().toUtc()).add(tail);
  final hits =
      notifications
          .where(
            (n) =>
                n.severity.index >= NotificationSeverity.warning.index &&
                !n.postedUtc.isBefore(start) &&
                !n.postedUtc.isAfter(end),
          )
          .toList()
        ..sort((a, b) => b.postedUtc.compareTo(a.postedUtc));
  return hits.take(cap).toList();
}

/// Build the card for one run, or null when its state is not worth a card.
SessionSummary? buildSessionSummary({
  required SequenceListItem sequence,
  required SequenceRunStateInfo run,
  required Iterable<AraNotification> notifications,
}) {
  final state = run.state;
  if (state == null || !isSummaryWorthyState(state)) return null;
  final incidents = incidentsFor(run, notifications);
  final ended =
      run.completedUtc ??
      (incidents.isNotEmpty ? incidents.first.postedUtc : null);
  return SessionSummary(
    sequenceId: run.sequenceId.isEmpty ? sequence.id : run.sequenceId,
    runId: run.runId,
    sequenceName: sequence.name.trim().isEmpty
        ? 'Your sequence'
        : sequence.name,
    state: state,
    targetName: run.currentTargetName,
    startedUtc: run.startedUtc,
    endedUtc: ended,
    framesCaptured: run.framesCaptured,
    incidents: incidents,
  );
}

/// Which of several ended runs is "the night": the one that ended last
/// (a run with no end stamp falls back to its start).
SessionSummary? newestSummary(Iterable<SessionSummary> all) {
  SessionSummary? best;
  for (final s in all) {
    final t =
        s.endedUtc ?? s.startedUtc ?? DateTime.fromMillisecondsSinceEpoch(0);
    final bt =
        best?.endedUtc ??
        best?.startedUtc ??
        DateTime.fromMillisecondsSinceEpoch(0);
    if (best == null || t.isAfter(bt)) best = s;
  }
  return best;
}

/// Show the card only for a night the user did not watch end: not already
/// acknowledged, not seen ending live on this client, and recent enough to be
/// "overnight" rather than history.
bool shouldShowSessionSummary(
  SessionSummary s, {
  required Set<String> acknowledgedRunIds,
  required Set<String> seenEndingLive,
  required DateTime now,
  Duration maxAge = const Duration(hours: 36),
}) {
  if (acknowledgedRunIds.contains(s.runId) ||
      seenEndingLive.contains(s.runId)) {
    return false;
  }
  final t = s.endedUtc ?? s.startedUtc;
  if (t == null) return false;
  return now.difference(t) <= maxAge;
}
