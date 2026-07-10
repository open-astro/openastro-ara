/// §42.5 — one persisted fault-history row from `GET /api/v1/faults`.
/// `equipmentType` and `faultType` carry the same lowercase wire tokens as the
/// live `equipment.fault` WS event (`camera`…, `disconnected`/`tracking_lost`/
/// `stall_timeout`/`value_mismatch`/`op_error`/`cooling_drift`), so history
/// rows correlate with live events without a second vocabulary.
class FaultRow {
  final String id;
  final String? sessionId;
  final DateTime detectedUtc;
  final String equipmentType;
  final String? equipmentId;
  final String? equipmentName;
  final String faultType;
  final String? details;

  /// The §42.3 reaction outcome (`notify_only` / `sequence_paused` /
  /// `reconnecting` / `recovered` / `gave_up:<terminal>`); null while an
  /// episode is still deciding or when no reaction applies.
  final String? actionTaken;
  final DateTime? resolvedUtc;

  /// Frame ids captured inside the fault window (§42.6). Empty until the
  /// server-side frame-correlation slice populates it.
  final List<String> affectedFrames;

  const FaultRow({
    required this.id,
    required this.sessionId,
    required this.detectedUtc,
    required this.equipmentType,
    required this.equipmentId,
    required this.equipmentName,
    required this.faultType,
    required this.details,
    required this.actionTaken,
    required this.resolvedUtc,
    this.affectedFrames = const <String>[],
  });

  bool get resolved => resolvedUtc != null;

  factory FaultRow.fromJson(Map<String, dynamic> json) => FaultRow(
        id: (json['id'] as String?) ?? '',
        sessionId: _string(json['session_id']),
        detectedUtc:
            DateTime.tryParse((json['detected_utc'] as String?) ?? '') ??
                DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        equipmentType: (json['equipment_type'] as String?) ?? 'unknown',
        equipmentId: _string(json['equipment_id']),
        equipmentName: _string(json['equipment_name']),
        faultType: (json['fault_type'] as String?) ?? 'unknown',
        details: _string(json['details']),
        actionTaken: _string(json['action_taken']),
        resolvedUtc: DateTime.tryParse((json['resolved_utc'] as String?) ?? ''),
        affectedFrames: (json['affected_frames'] as List? ?? const [])
            .whereType<String>()
            .toList(growable: false),
      );

  static String? _string(dynamic value) =>
      value is String && value.isNotEmpty ? value : null;
}
