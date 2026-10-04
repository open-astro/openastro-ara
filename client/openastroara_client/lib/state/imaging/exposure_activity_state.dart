import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/ws_event_stream.dart';
import '../ws/ws_providers.dart';

/// The exposure the daemon's camera is running RIGHT NOW, whoever asked for
/// it — Take One, a sequence light/flat/dark, the Smart Focus probe, a
/// plate-solve capture. Fed from the `camera.exposure_started` /
/// `camera.exposure_complete` / `camera.exposure_failed` WS events the daemon
/// publishes from its one shared capture core, so the Live tab can show a
/// timer for every exposure, not only the ones this client started.
class ExposureActivity {
  final String frameId;
  final Duration exposure;
  /// Stamped on the CLIENT clock when the started event arrived — the two
  /// machines' clocks can disagree by seconds (the Pi syncs from the client,
  /// §31), and a countdown that starts at −4 s or jumps to done reads as a
  /// bug. The WS hop adds a few tens of ms at most.
  final DateTime startedAt;
  /// `light` / `flat` / `dark` / `bias` for a catalogued frame, `analysis`
  /// (an autofocus probe) or `plate-solve` for the unpersisted captures.
  final String kind;
  final String? filterName;

  const ExposureActivity({
    required this.frameId,
    required this.exposure,
    required this.startedAt,
    required this.kind,
    this.filterName,
  });

  Duration elapsed(DateTime now) {
    final d = now.difference(startedAt);
    return d.isNegative ? Duration.zero : d;
  }

  Duration remaining(DateTime now) {
    final r = exposure - elapsed(now);
    return r.isNegative ? Duration.zero : r;
  }

  /// 0..1 of the exposure itself (the download that follows is open-ended).
  double progress(DateTime now) {
    if (exposure <= Duration.zero) return 1;
    return (elapsed(now).inMilliseconds / exposure.inMilliseconds)
        .clamp(0.0, 1.0);
  }

  /// The shutter should have closed: the daemon is reading the sensor out
  /// and (for a stored frame) writing the FITS. Inferred from the clock —
  /// the daemon's complete event only fires once the pixels are downloaded.
  bool isDownloading(DateTime now) => elapsed(now) >= exposure;

  /// Human label for the kind, matching the client's existing frame-type
  /// vocabulary.
  String get kindLabel => switch (kind) {
        'light' || 'snapshot' => 'Light',
        'flat' => 'Flat',
        'dark' || 'darkflat' => 'Dark',
        'bias' => 'Bias',
        'analysis' => 'Focus probe',
        'plate-solve' => 'Plate solve',
        _ => kind,
      };

  @override
  bool operator ==(Object other) =>
      other is ExposureActivity &&
      other.frameId == frameId &&
      other.exposure == exposure &&
      other.startedAt == startedAt &&
      other.kind == kind &&
      other.filterName == filterName;

  @override
  int get hashCode =>
      Object.hash(frameId, exposure, startedAt, kind, filterName);
}

/// How long past the announced exposure the activity may linger with no
/// complete/failed event before it is dropped as stale: a download on a slow
/// bridge is tens of seconds, a WS reconnect gap can swallow the complete
/// event entirely, and a timer that never ends is worse than one that ends a
/// little early.
const Duration kExposureActivityGrace = Duration(minutes: 2);

class ExposureActivityNotifier extends Notifier<ExposureActivity?> {
  Timer? _watchdog;

  @override
  ExposureActivity? build() {
    ref.onDispose(() => _watchdog?.cancel());
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      final payload = event.payload;
      switch (event.type) {
        case 'camera.exposure_started':
          final frameId = payload['frame_id'];
          final secs = payload['exposure_sec'];
          if (frameId is! String || frameId.isEmpty || secs is! num) return;
          final kind = payload['kind'];
          final filter = payload['filter_name'];
          _begin(ExposureActivity(
            frameId: frameId,
            exposure: Duration(milliseconds: (secs * 1000).round()),
            startedAt: clock.now(),
            kind: kind is String && kind.isNotEmpty ? kind : 'light',
            filterName: filter is String && filter.isNotEmpty ? filter : null,
          ));
        case 'camera.exposure_complete':
        case 'camera.exposure_failed':
        case 'frame.complete':
          // frame.complete is the belt to exposure_complete's braces: if the
          // complete event was lost the catalogued frame still closes the
          // timer. Only for THIS exposure's id, for all three: a stale frame
          // registering late (the §28.8 orphan scan) or a late failed for an
          // earlier exposure must not kill a live timer.
          final id = payload['frame_id'];
          final current = state;
          if (current == null) return;
          if (id == current.frameId) _end();
      }
    });
    // A dropped link means we may never hear the complete event; a frozen
    // "Exposing" over a reconnecting client is misleading, so clear it and let
    // the next started event (or the watchdog) take over.
    ref.listen(wsConnectionStateProvider, (prev, next) {
      final s = next.asData?.value;
      if (s != null && s != WsConnectionState.connected) _end();
    });
    return null;
  }

  void _begin(ExposureActivity activity) {
    _watchdog?.cancel();
    state = activity;
    _watchdog = Timer(activity.exposure + kExposureActivityGrace, () {
      if (state?.frameId == activity.frameId) state = null;
    });
  }

  /// The user cancelled from this client (the abort POST was accepted): drop
  /// the timer now rather than wait for the daemon's failed event.
  void endLocally() => _end();

  void _end() {
    _watchdog?.cancel();
    _watchdog = null;
    state = null;
  }
}

final exposureActivityProvider =
    NotifierProvider<ExposureActivityNotifier, ExposureActivity?>(
        ExposureActivityNotifier.new);
