import 'package:flutter/foundation.dart';
import 'package:flutter/scheduler.dart';

/// `--dart-define=ARA_PERF=true`: the performance overlay on, and raster and
/// build frame times logged every 5 s (p50, p95, worst, frames over the
/// 60 Hz budget). For profiling on a device; off in every normal build.
const bool kAraPerf = bool.fromEnvironment('ARA_PERF');

void startPerfLog() {
  if (!kAraPerf) return;
  final raster = <int>[];
  final build = <int>[];
  var since = DateTime.now();
  SchedulerBinding.instance.addTimingsCallback((timings) {
    for (final t in timings) {
      raster.add(t.rasterDuration.inMicroseconds);
      build.add(t.buildDuration.inMicroseconds);
    }
    if (DateTime.now().difference(since) < const Duration(seconds: 5)) return;
    since = DateTime.now();
    String stats(List<int> us) {
      us.sort();
      double ms(int i) => us[i.clamp(0, us.length - 1)] / 1000;
      final over = us.where((v) => v > 16667).length;
      return 'p50 ${ms(us.length ~/ 2).toStringAsFixed(1)} '
          'p95 ${ms((us.length * 0.95).floor()).toStringAsFixed(1)} '
          'max ${ms(us.length - 1).toStringAsFixed(1)} ms, '
          '$over/${us.length} over 16.7';
    }

    debugPrint('ARA_PERF raster ${stats(raster)} | build ${stats(build)}');
    raster.clear();
    build.clear();
  });
}
