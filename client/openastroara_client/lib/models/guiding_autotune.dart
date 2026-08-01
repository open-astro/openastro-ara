/// Guiding auto-tune wire models. Unknown nested plan fields stay maps so the
/// client remains compatible while the deterministic server adds metrics.
class GuidingAutoTuneCapabilities {
  final bool enabled;
  final bool connected;
  final bool hasTelemetry;
  final bool canAnalyze;
  final bool canApply;
  final bool guideRateChangesSupported;
  final List<String> lockedReasons;

  const GuidingAutoTuneCapabilities({
    required this.enabled,
    required this.connected,
    required this.hasTelemetry,
    required this.canAnalyze,
    required this.canApply,
    required this.guideRateChangesSupported,
    required this.lockedReasons,
  });

  factory GuidingAutoTuneCapabilities.fromJson(Map<String, dynamic> json) =>
      GuidingAutoTuneCapabilities(
        enabled: json['enabled'] as bool? ?? false,
        connected: json['connected'] as bool? ?? false,
        hasTelemetry: json['has_telemetry'] as bool? ?? false,
        canAnalyze: json['can_analyze'] as bool? ?? false,
        canApply: json['can_apply'] as bool? ?? false,
        guideRateChangesSupported:
            json['guide_rate_changes_supported'] as bool? ?? false,
        lockedReasons: (json['locked_reasons'] as List<dynamic>? ?? const [])
            .whereType<String>()
            .toList(growable: false),
      );
}

class GuidingAutoTuneStatus {
  final String sessionId;
  final String state;
  final double progress;
  final String currentStep;
  final String? behaviorClass;
  final double? behaviorConfidence;
  final int telemetrySamples;
  final double? baselineScore;
  final double? bestScore;
  final bool canApply;
  final bool canRollback;
  final List<String> warnings;
  final Map<String, dynamic>? plan;
  final Map<String, dynamic>? bestCandidate;
  final DateTime startedAtUtc;
  final DateTime updatedAtUtc;

  const GuidingAutoTuneStatus({
    required this.sessionId,
    required this.state,
    required this.progress,
    required this.currentStep,
    required this.behaviorClass,
    required this.behaviorConfidence,
    required this.telemetrySamples,
    required this.baselineScore,
    required this.bestScore,
    required this.canApply,
    required this.canRollback,
    required this.warnings,
    required this.plan,
    required this.bestCandidate,
    required this.startedAtUtc,
    required this.updatedAtUtc,
  });

  factory GuidingAutoTuneStatus.fromJson(Map<String, dynamic> json) =>
      GuidingAutoTuneStatus(
        sessionId: json['session_id'] as String? ?? '',
        state: json['state'] as String? ?? 'idle',
        progress: (json['progress'] as num?)?.toDouble() ?? 0,
        currentStep: json['current_step'] as String? ?? '',
        behaviorClass: json['behavior_class'] as String?,
        behaviorConfidence: (json['behavior_confidence'] as num?)?.toDouble(),
        telemetrySamples: (json['telemetry_samples'] as num?)?.toInt() ?? 0,
        baselineScore: (json['baseline_score'] as num?)?.toDouble(),
        bestScore: (json['best_score'] as num?)?.toDouble(),
        canApply: json['can_apply'] as bool? ?? false,
        canRollback: json['can_rollback'] as bool? ?? false,
        warnings: (json['warnings'] as List<dynamic>? ?? const [])
            .whereType<String>()
            .toList(growable: false),
        plan: (json['plan'] as Map?)?.cast<String, dynamic>(),
        bestCandidate:
            (json['best_candidate'] as Map?)?.cast<String, dynamic>(),
        startedAtUtc: DateTime.tryParse(json['started_at_utc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        updatedAtUtc: DateTime.tryParse(json['updated_at_utc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      );
}

class GuidingAutoTuneReport {
  final String sessionId;
  final String markdown;

  const GuidingAutoTuneReport({required this.sessionId, required this.markdown});

  factory GuidingAutoTuneReport.fromJson(Map<String, dynamic> json) =>
      GuidingAutoTuneReport(
        sessionId: json['session_id'] as String? ?? '',
        markdown: json['markdown'] as String? ?? '',
      );
}
