import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/ws_event.dart';
import '../ws/ws_providers.dart';
import 'guider_calibration_state.dart';

/// §63.6 `guider.*` calibration-build WS event tokens (mirrors
/// `GuiderService.DarkLibrary` on the server). The daemon's builds are blocking
/// RPCs with NO granular progress — the stream is started/complete/failed only,
/// so the UI shows an indeterminate "building" state, not a percentage.
abstract final class GuiderBuildWsEvents {
  static const darkLibraryPrefix = 'guider.dark_library.';
  static const defectMapPrefix = 'guider.defect_map.';
  static const started = 'started';
  static const complete = 'complete';
  static const failed = 'failed';
}

/// Which calibration artifact a build event belongs to. Values double as the
/// state-map keys.
enum CalibrationArtifact { darkLibrary, defectMap }

/// The live phase of one artifact's build, folded from the WS stream.
enum CalibrationBuildPhase { building, complete, failed }

/// One artifact's most recent build activity. [error] is set only for
/// [CalibrationBuildPhase.failed] (the daemon's `error` payload field).
class CalibrationBuildActivity {
  final CalibrationBuildPhase phase;
  final String? error;
  const CalibrationBuildActivity({required this.phase, this.error});
}

/// Pure fold of one WS event into the per-artifact build-activity map.
/// Returns null when the event is not a calibration-build event (so callers
/// can skip a no-op state write). Exposed for unit tests.
Map<CalibrationArtifact, CalibrationBuildActivity>? foldGuiderBuildEvent(
  Map<CalibrationArtifact, CalibrationBuildActivity> current,
  WsEvent event,
) {
  final CalibrationArtifact artifact;
  final String phaseToken;
  if (event.type.startsWith(GuiderBuildWsEvents.darkLibraryPrefix)) {
    artifact = CalibrationArtifact.darkLibrary;
    phaseToken = event.type.substring(GuiderBuildWsEvents.darkLibraryPrefix.length);
  } else if (event.type.startsWith(GuiderBuildWsEvents.defectMapPrefix)) {
    artifact = CalibrationArtifact.defectMap;
    phaseToken = event.type.substring(GuiderBuildWsEvents.defectMapPrefix.length);
  } else {
    return null;
  }
  final CalibrationBuildPhase phase;
  switch (phaseToken) {
    case GuiderBuildWsEvents.started:
      phase = CalibrationBuildPhase.building;
    case GuiderBuildWsEvents.complete:
      phase = CalibrationBuildPhase.complete;
    case GuiderBuildWsEvents.failed:
      phase = CalibrationBuildPhase.failed;
    default:
      return null; // a future subtype this build of the app doesn't know
  }
  final error = phase == CalibrationBuildPhase.failed
      ? (event.payload['error'] is String ? event.payload['error'] as String : null)
      : null;
  return {
    ...current,
    artifact: CalibrationBuildActivity(phase: phase, error: error),
  };
}

/// Live per-artifact calibration-build activity for the active server, folded
/// from `guider.dark_library.*` / `guider.defect_map.*` WS events. Empty until
/// the first build event; a server switch resets it (the stream provider
/// rebuilds). On a build completing, the calibration status is re-read so the
/// dialog's exists/loaded rows reflect the new artifact without a manual
/// Refresh (the §39 calibration_state refresh-on-frame.complete precedent).
class GuiderBuildActivityNotifier
    extends Notifier<Map<CalibrationArtifact, CalibrationBuildActivity>> {
  @override
  Map<CalibrationArtifact, CalibrationBuildActivity> build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) return const {};
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      final folded = foldGuiderBuildEvent(state, event);
      if (folded == null) return;
      state = folded;
      if (event.type.endsWith('.${GuiderBuildWsEvents.complete}') ||
          event.type.endsWith('.${GuiderBuildWsEvents.failed}')) {
        // Terminal either way: refresh so exists/loaded/counts reflect reality
        // (a failed build can still have mutated daemon state, e.g. cleared
        // darks before failing). Fire-and-forget — refresh() self-serializes.
        Future<void>.sync(
          () => ref.read(guiderCalibrationProvider.notifier).refresh(),
        ).ignore();
      }
    });
    return const {};
  }
}

/// Per-artifact build activity for the §63.6 calibration dialog. Deliberately
/// **not** autoDispose: a build runs for minutes and the user will close the
/// dialog — the terminal complete/failed event must still be folded (and the
/// status refreshed) while no widget is watching.
final guiderBuildActivityProvider = NotifierProvider<
    GuiderBuildActivityNotifier,
    Map<CalibrationArtifact, CalibrationBuildActivity>>(
  GuiderBuildActivityNotifier.new,
);
