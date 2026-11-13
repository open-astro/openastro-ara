// #1582 — `GET /api/v1/server/usage-history`: what the rig sampled of its own
// storage, CPU and memory (every 15 s, the last 12 h, oldest first). A series
// the rig could not read is null in that sample, never 0.

/// One reading. CPU is a share of ALL cores (0-100).
class UsageSample {
  const UsageSample({
    required this.at,
    this.storageFreeBytes,
    this.storageTotalBytes,
    this.cpuPercent,
    this.memoryBytes,
  });

  final DateTime at;
  final int? storageFreeBytes;
  final int? storageTotalBytes;
  final double? cpuPercent;
  final int? memoryBytes;

  factory UsageSample.fromJson(Map<String, dynamic> json) => UsageSample(
    at: DateTime.parse(json['at_utc'] as String).toUtc(),
    storageFreeBytes: (json['storage_free_bytes'] as num?)?.toInt(),
    storageTotalBytes: (json['storage_total_bytes'] as num?)?.toInt(),
    cpuPercent: (json['cpu_percent'] as num?)?.toDouble(),
    memoryBytes: (json['memory_bytes'] as num?)?.toInt(),
  );
}

class UsageHistory {
  const UsageHistory({
    required this.intervalSeconds,
    required this.processorCount,
    required this.samples,
  });

  /// The rig keeps 12 h at 15 s a sample.
  static const keepSamples = 2880;

  final int intervalSeconds;
  final int processorCount;
  final List<UsageSample> samples;

  factory UsageHistory.fromJson(Map<String, dynamic> json) => UsageHistory(
    intervalSeconds: (json['interval_seconds'] as num?)?.toInt() ?? 15,
    processorCount: (json['processor_count'] as num?)?.toInt() ?? 1,
    samples: [
      for (final s in (json['samples'] as List<dynamic>? ?? const []))
        UsageSample.fromJson(s as Map<String, dynamic>),
    ],
  );

  /// This history plus [newer] (a `since` read), capped to what the rig keeps.
  UsageHistory append(UsageHistory newer) {
    final last = samples.isEmpty ? null : samples.last.at;
    final fresh = [
      for (final s in newer.samples)
        if (last == null || s.at.isAfter(last)) s,
    ];
    final all = [...samples, ...fresh];
    return UsageHistory(
      intervalSeconds: newer.intervalSeconds,
      processorCount: newer.processorCount,
      samples: all.length > keepSamples
          ? all.sublist(all.length - keepSamples)
          : all,
    );
  }
}
