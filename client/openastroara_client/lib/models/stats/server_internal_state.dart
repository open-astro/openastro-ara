// §66.7 — `GET /api/v1/server/internal-state`: the server's live process, GC,
// thread-pool, WebSocket and host figures behind the Stats "Pi performance"
// section. Sampled when read; nothing runs on the server while nobody looks.

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
double? _dblOrNull(dynamic v) => v is num ? v.toDouble() : null;

class ServerInternalState {
  final DateTime? capturedUtc;
  final double uptimeSeconds;
  final int workingSetBytes;
  final int managedHeapBytes;
  final int heapCommittedBytes;
  final int gen0Collections;
  final int gen1Collections;
  final int gen2Collections;
  final double gcPauseTotalMs;
  /// Share of one core's wall time since the previous read (400 = four cores
  /// flat out); null on the server's first read after boot.
  final double? cpuPercent;
  final int threadCount;
  final int threadPoolBusyWorkers;
  final int threadPoolPendingWorkItems;
  final int wsOpenConnections;
  final int wsMaxConnections;
  final int processorCount;
  final double? loadAverage1Min;
  final String osDescription;
  final String architecture;
  final String runtimeVersion;

  const ServerInternalState({
    this.capturedUtc,
    this.uptimeSeconds = 0,
    this.workingSetBytes = 0,
    this.managedHeapBytes = 0,
    this.heapCommittedBytes = 0,
    this.gen0Collections = 0,
    this.gen1Collections = 0,
    this.gen2Collections = 0,
    this.gcPauseTotalMs = 0,
    this.cpuPercent,
    this.threadCount = 0,
    this.threadPoolBusyWorkers = 0,
    this.threadPoolPendingWorkItems = 0,
    this.wsOpenConnections = 0,
    this.wsMaxConnections = 0,
    this.processorCount = 0,
    this.loadAverage1Min,
    this.osDescription = '',
    this.architecture = '',
    this.runtimeVersion = '',
  });

  factory ServerInternalState.fromJson(Map<String, dynamic> json) {
    final process = (json['process'] as Map?)?.cast<String, dynamic>() ?? const {};
    final ws = (json['web_sockets'] as Map?)?.cast<String, dynamic>() ?? const {};
    final host = (json['host'] as Map?)?.cast<String, dynamic>() ?? const {};
    return ServerInternalState(
      capturedUtc: DateTime.tryParse(json['captured_utc']?.toString() ?? '')?.toUtc(),
      uptimeSeconds: _dbl(json['uptime_seconds']),
      workingSetBytes: _int(process['working_set_bytes']),
      managedHeapBytes: _int(process['managed_heap_bytes']),
      heapCommittedBytes: _int(process['heap_committed_bytes']),
      gen0Collections: _int(process['gen0_collections']),
      gen1Collections: _int(process['gen1_collections']),
      gen2Collections: _int(process['gen2_collections']),
      gcPauseTotalMs: _dbl(process['gc_pause_total_ms']),
      cpuPercent: _dblOrNull(process['cpu_percent']),
      threadCount: _int(process['thread_count']),
      threadPoolBusyWorkers: _int(process['thread_pool_busy_workers']),
      threadPoolPendingWorkItems: _int(process['thread_pool_pending_work_items']),
      wsOpenConnections: _int(ws['open_connections']),
      wsMaxConnections: _int(ws['max_connections']),
      processorCount: _int(host['processor_count']),
      loadAverage1Min: _dblOrNull(host['load_average1_min']),
      osDescription: host['os_description']?.toString() ?? '',
      architecture: host['architecture']?.toString() ?? '',
      runtimeVersion: host['runtime_version']?.toString() ?? '',
    );
  }
}

/// "3d 4h", "4h 12m", "12m 05s" — the uptime tile.
String formatUptime(double seconds) {
  final s = seconds.floor();
  final d = s ~/ 86400, h = (s % 86400) ~/ 3600, m = (s % 3600) ~/ 60, sec = s % 60;
  if (d > 0) return '${d}d ${h}h';
  if (h > 0) return '${h}h ${m.toString().padLeft(2, '0')}m';
  return '${m}m ${sec.toString().padLeft(2, '0')}s';
}

/// Bytes as MB/GB with one decimal: the memory tiles.
String formatBytes(int bytes) {
  const mb = 1024 * 1024;
  if (bytes >= 1024 * mb) return '${(bytes / (1024 * mb)).toStringAsFixed(2)} GB';
  return '${(bytes / mb).toStringAsFixed(1)} MB';
}
