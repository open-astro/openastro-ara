import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import 'guider_state.dart';

/// §63.18 live guiding panel — rolling RMS history for the Imaging tab.
///
/// The daemon exposes the live RMS only on the REST `GuiderStateDto` (the
/// `guider.state` WS event is catalogued server-side but not yet published, and
/// the §50.7 `/stats/guiding` series is a per-session aggregate, not a rolling
/// live window). So the panel keeps a client-local ring buffer fed from
/// [guiderStatusProvider] snapshots, with a poll timer that re-reads status
/// while the panel is mounted (the provider is autoDispose — polling stops the
/// moment no widget listens).

/// One RMS observation (arcsec, as reported by the daemon). Value equality so
/// tests can compare buffers structurally.
class RmsSample {
  final DateTime time;
  final double total;
  final double? ra;
  final double? dec;

  const RmsSample({required this.time, required this.total, this.ra, this.dec});

  @override
  bool operator ==(Object other) =>
      other is RmsSample &&
      other.time == time &&
      other.total == total &&
      other.ra == ra &&
      other.dec == dec;

  @override
  int get hashCode => Object.hash(time, total, ra, dec);
}

/// Pure rolling window of [RmsSample]s: keeps at most [window] of history and
/// at most [maxSamples] entries (a hard cap so a misbehaving clock can't grow
/// the buffer unboundedly). Not a Flutter type — unit-testable as-is.
class RmsRingBuffer {
  final Duration window;
  final int maxSamples;
  final List<RmsSample> _samples = [];

  RmsRingBuffer({
    this.window = const Duration(minutes: 5),
    this.maxSamples = 600,
  });

  /// Read-only view of the retained samples, oldest first.
  List<RmsSample> get samples => List.unmodifiable(_samples);

  bool get isEmpty => _samples.isEmpty;

  /// Appends [sample] and evicts anything older than [window] relative to the
  /// newest sample (not the wall clock, so a paused stream doesn't self-erase
  /// on the next append). Out-of-order samples (time <= newest) are ignored —
  /// a re-poll of an unchanged status must not duplicate points.
  void add(RmsSample sample) {
    if (_samples.isNotEmpty && !sample.time.isAfter(_samples.last.time)) {
      return;
    }
    _samples.add(sample);
    final cutoff = sample.time.subtract(window);
    while (_samples.length > maxSamples || _samples.first.time.isBefore(cutoff)) {
      _samples.removeAt(0);
    }
  }

  void clear() => _samples.clear();
}

/// Image scale of the guide train in arcsec/px, from the §63.5 guide focal
/// length (mm) and pixel size (µm) — `206.265 * µm / mm`. Returns `null` when
/// either is unset (<= 0), in which case the panel shows px as unavailable.
double? guiderArcsecPerPixel(int focalLengthMm, double pixelSizeUm) {
  if (focalLengthMm <= 0 || pixelSizeUm <= 0) return null;
  return 206.265 * pixelSizeUm / focalLengthMm;
}

/// How often the mounted panel re-reads guider status. 2 s tracks PHD2's
/// typical guide-exposure cadence without hammering the daemon.
const kLiveGuidingPollInterval = Duration(seconds: 2);

/// Rolling RMS history for the active server, updated live while listened to.
///
/// Folds every [guiderStatusProvider] data emission that carries an RMS while
/// the runtime is actively guiding (guiding/dithering) into the buffer, and
/// drives its own poll timer so the history keeps flowing without any other
/// widget refreshing status. autoDispose: the timer and buffer die with the
/// last listener (leaving the panel stops the polling).
class LiveGuidingNotifier extends Notifier<List<RmsSample>> {
  final RmsRingBuffer _buffer = RmsRingBuffer();
  Timer? _timer;

  /// Injectable clock for tests (samples are stamped client-side — the DTO
  /// carries no timestamp).
  DateTime Function() now = DateTime.now;

  @override
  List<RmsSample> build() {
    // Seed from whatever status is already loaded so the panel doesn't start
    // blank when the chip/dialog polled recently.
    final current = ref.read(guiderStatusProvider).asData?.value;
    if (current != null) _fold(current);
    // Deliberately NOT ref.listen on guiderStatusProvider: GuiderStatus has
    // value equality, so steady guiding with an unchanged RMS would never fire
    // the listener — the trace would freeze and old points would never age out
    // (eviction runs on append). Instead every poll tick appends a
    // freshly-timestamped sample, so a constant RMS still draws a live,
    // sliding trace.
    _timer?.cancel();
    _timer = Timer.periodic(kLiveGuidingPollInterval, (_) => pollTick());
    ref.onDispose(() {
      _timer?.cancel();
      _timer = null;
    });
    return _buffer.samples;
  }

  /// One poll cycle: re-read guider status, then fold the result (also called
  /// directly by tests — the timer is just a scheduler around this).
  /// refresh() is null-api-safe and self-serializing; a poll on a disconnected
  /// daemon is a cheap no-op error kept out of this buffer.
  Future<void> pollTick() async {
    await ref.read(guiderStatusProvider.notifier).refresh();
    if (!ref.mounted) return;
    final status = ref.read(guiderStatusProvider).asData?.value;
    if (status != null) _fold(status);
  }

  // Strictly-monotonic sample clock: on coarse-resolution clocks (Windows'
  // ~15 ms granularity) two rapid poll ticks can share a DateTime.now() value,
  // and the buffer's timestamp-keyed duplicate rejection would silently drop
  // the second point (the CI-observed flake). Bump equal/backward readings by
  // 1 ms past the last stamp so every tick appends.
  DateTime? _lastStamp;

  DateTime _nextStamp() {
    var t = now();
    final last = _lastStamp;
    if (last != null && !t.isAfter(last)) {
      t = last.add(const Duration(milliseconds: 1));
    }
    _lastStamp = t;
    return t;
  }

  void _fold(GuiderStatus status) {
    final actively = status.runtimeState == GuiderRuntimeState.guiding ||
        status.runtimeState == GuiderRuntimeState.dithering;
    final total = status.rmsTotal;
    if (!actively || total == null) return;
    _buffer.add(RmsSample(
      time: _nextStamp(),
      total: total,
      ra: status.rmsRa,
      dec: status.rmsDec,
    ));
    state = _buffer.samples;
  }
}

/// Live rolling RMS window (§63.18). autoDispose by design — see
/// [LiveGuidingNotifier].
final liveGuidingRmsProvider =
    NotifierProvider.autoDispose<LiveGuidingNotifier, List<RmsSample>>(
        LiveGuidingNotifier.new);
