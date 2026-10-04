import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../util/phd2_guide_log.dart';
import '../ws/ws_providers.dart';
import 'guide_step_state.dart';

/// A PHD2 guide log playing back through the Live-tab graph "like a movie":
/// one logged frame per [kGuideReplayTick], markers landing where PHD2
/// logged them. For checking the graph against a real night without waiting
/// for one. A live `guider.step` from the daemon ends the replay and clears
/// the graph so the two are never mixed.
class GuideReplay {
  final String fileName;
  final int played;
  final int total;
  final bool playing;
  const GuideReplay({
    required this.fileName,
    required this.played,
    required this.total,
    required this.playing,
  });

  GuideReplay copyWith({int? played, bool? playing}) => GuideReplay(
      fileName: fileName,
      played: played ?? this.played,
      total: total,
      playing: playing ?? this.playing);
}

/// PHD2 logs a frame every 2–3 s; 150 ms per frame is ~20× real time, so a
/// half-hour session plays in about a minute and a half.
const Duration kGuideReplayTick = Duration(milliseconds: 150);

class GuideReplayNotifier extends Notifier<GuideReplay?> {
  Timer? _timer;
  List<GuideStep> _steps = const [];
  List<GuideMarker> _markers = const [];
  int _next = 0;
  int _nextMarker = 0;

  @override
  GuideReplay? build() {
    ref.onDispose(() => _timer?.cancel());
    ref.listen(wsEventsProvider, (prev, next) {
      final e = next.asData?.value;
      if (e != null && e.type == 'guider.step' && state != null) {
        // Real guiding resumed: the replay must not pollute it.
        _timer?.cancel();
        state = null;
        ref.read(guideStepsProvider.notifier).clear();
        ref.read(guideMarkersProvider.notifier).clear();
      }
    });
    return null;
  }

  /// Starts playing [log] from its first frame, replacing whatever the graph
  /// held. Returns false when the log has no guiding session.
  bool start(String fileName, Phd2GuideLog log, {Duration tick = kGuideReplayTick}) {
    _timer?.cancel();
    _steps = log.allSteps;
    _markers = log.allMarkers;
    if (_steps.isEmpty) return false;
    _next = 0;
    _nextMarker = 0;
    ref.read(guideStepsProvider.notifier).clear();
    ref.read(guideMarkersProvider.notifier).clear();
    state = GuideReplay(fileName: fileName, played: 0, total: _steps.length, playing: true);
    _timer = Timer.periodic(tick, (_) => tickOnce());
    return true;
  }

  /// One frame of playback (public so tests drive it without a timer).
  void tickOnce() {
    if (_next >= _steps.length) {
      _timer?.cancel();
      // Flush trailing markers (guiding stopped) so the end is drawn.
      _flushMarkersUpTo(null);
      state = state?.copyWith(playing: false);
      return;
    }
    final step = _steps[_next++];
    _flushMarkersUpTo(step.at);
    ref.read(guideStepsProvider.notifier).add(step);
    state = state?.copyWith(played: _next);
  }

  void _flushMarkersUpTo(DateTime? at) {
    final markers = ref.read(guideMarkersProvider.notifier);
    while (_nextMarker < _markers.length &&
        (at == null || !_markers[_nextMarker].at.isAfter(at))) {
      markers.add(_markers[_nextMarker++]);
    }
  }

  /// Stops playback and clears the graph.
  void stop() {
    _timer?.cancel();
    _timer = null;
    state = null;
    _steps = const [];
    _markers = const [];
    ref.read(guideStepsProvider.notifier).clear();
    ref.read(guideMarkersProvider.notifier).clear();
  }
}

final guideReplayProvider =
    NotifierProvider<GuideReplayNotifier, GuideReplay?>(GuideReplayNotifier.new);
