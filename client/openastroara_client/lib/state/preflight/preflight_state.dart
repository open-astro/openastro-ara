import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/preflight_checklist.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../models/server.dart';
import '../../services/preflight_api.dart';
import '../../services/preflight_prefs_service.dart';
import '../saved_server_state.dart';
import '../sequencer/sequence_list_state.dart';
import '../settings/site_settings_state.dart';
import '../setup/setup_readiness.dart';
import '../sky_atlas/tonight_sky_state.dart';

/// Test seam: how a [PreflightApi] is built for the active server.
final preflightApiFactoryProvider = Provider<PreflightApi Function(AraServer)>(
  (ref) => PreflightApi.new,
);

final preflightPrefsProvider = Provider<PreflightPrefsService>(
  (ref) => PreflightPrefsService(),
);

/// The wall clock the night key and the darkness test use (tests pin it).
final preflightClockProvider = Provider<DateTime Function()>(
  (_) => DateTime.now,
);

/// §58.14 — tonight's checklist from the daemon. Null = no server, or a daemon
/// that predates the route. An unreachable daemon is an error state (the pane
/// says so; the bedtime card never fires on it).
class PreflightChecklistNotifier extends AsyncNotifier<PreflightChecklist?> {
  @override
  Future<PreflightChecklist?> build() async {
    final server = ref.watch(activeServerProvider);
    if (server == null) return null;
    final api = ref.read(preflightApiFactoryProvider)(server);
    try {
      return await api.fetchToday();
    } finally {
      api.close();
    }
  }

  /// Re-run the self-test (the pane's Refresh, and the card before it shows:
  /// the user is about to go to bed on this answer, so it is never stale).
  Future<PreflightChecklist?> refresh() {
    ref.invalidateSelf();
    return future;
  }
}

final preflightChecklistProvider =
    AsyncNotifierProvider<PreflightChecklistNotifier, PreflightChecklist?>(
      PreflightChecklistNotifier.new,
    );

class PreflightPromptState {
  /// Fire the bedtime card now.
  final bool shouldPrompt;

  /// Tonight has already been answered on this device.
  final bool acknowledgedTonight;
  const PreflightPromptState({
    required this.shouldPrompt,
    required this.acknowledgedTonight,
  });
}

/// §58.14 — when to show the bedtime card: a sequence is running, the site is
/// dark, and tonight has not been answered yet. Follows the 5-minute sky tick
/// and the sequence list, so a run started after dusk still gets its card.
class PreflightPromptNotifier extends AsyncNotifier<PreflightPromptState> {
  Set<String> _acknowledged = const {};

  @override
  Future<PreflightPromptState> build() async {
    // Merge, never replace: a rebuild that started before acknowledgeTonight()
    // and lands after it must not resurrect the card from a stale disk read.
    _acknowledged = {
      ..._acknowledged,
      ...await ref.read(preflightPrefsProvider).loadAcknowledgedNights(),
    };
    ref.watch(skyClockProvider);
    final site = ref.watch(siteSettingsProvider);
    // Await the list (not its AsyncValue): the first build must not answer
    // "nothing running" while the list is still loading.
    List<SequenceListItem>? runs;
    try {
      runs = await ref.watch(sequenceListProvider.future);
    } catch (_) {
      runs = null; // an unreachable daemon: nothing to prompt about
    }
    final now = ref.read(preflightClockProvider)();
    final nightKey = preflightNightKey(now);
    final acked = _acknowledged.contains(nightKey);
    return PreflightPromptState(
      shouldPrompt: shouldPromptPreflight(
        sequenceRunning: anySequenceLive(runs),
        dark: isDarkNow(site, nowUtc: now.toUtc()),
        nightKey: nightKey,
        acknowledgedNights: _acknowledged,
      ),
      acknowledgedTonight: acked,
    );
  }

  /// "All good — let me go to bed" (or Adjust): tonight is answered on this
  /// device; the card stays reachable on Setup → Before you sleep.
  Future<void> acknowledgeTonight() async {
    final key = preflightNightKey(ref.read(preflightClockProvider)());
    _acknowledged = {..._acknowledged, key};
    state = const AsyncData(
      PreflightPromptState(shouldPrompt: false, acknowledgedTonight: true),
    );
    await ref
        .read(preflightPrefsProvider)
        .saveAcknowledgedNights(_acknowledged);
  }
}

final preflightPromptProvider =
    AsyncNotifierProvider<PreflightPromptNotifier, PreflightPromptState>(
      PreflightPromptNotifier.new,
    );

/// A run the user could sleep through: running, starting or paused (a paused
/// run still needs the rig watched).
bool anySequenceLive(List<SequenceListItem>? runs) =>
    runs?.any(
      (s) => switch (s.currentRunState) {
        SequenceRunState.running ||
        SequenceRunState.starting ||
        SequenceRunState.paused ||
        SequenceRunState.pausedAwaitingUser => true,
        _ => false,
      },
    ) ??
    false;

/// Setup → Before you sleep readiness glyph. Pure — unit-tested.
/// problem = a failing item or a flip below the floor; done = answered tonight
/// with nothing failing (a warning is the user's informed call at Adjust);
/// pending otherwise (not read yet, no daemon, or not answered).
SetupStepState preSleepStepState(
  AsyncValue<PreflightChecklist?> checklist,
  bool acknowledgedTonight,
) {
  final c = checklist.asData?.value;
  if (c == null) return SetupStepState.pending;
  if (c.hasFailure) return SetupStepState.problem;
  return acknowledgedTonight ? SetupStepState.done : SetupStepState.pending;
}

final setupPreSleepStateProvider = Provider<SetupStepState>((ref) {
  final acked =
      ref.watch(preflightPromptProvider).asData?.value.acknowledgedTonight ??
      false;
  return preSleepStepState(ref.watch(preflightChecklistProvider), acked);
});

/// Bumped by the bedtime card's Adjust button so the Setup tab opens on the
/// Before you sleep step (the tab's selected step is otherwise local state).
class SetupPreSleepRequestNotifier extends Notifier<int> {
  @override
  int build() => 0;
  void request() => state++;
}

final setupPreSleepRequestProvider =
    NotifierProvider<SetupPreSleepRequestNotifier, int>(
      SetupPreSleepRequestNotifier.new,
    );
