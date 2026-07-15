/// §2/§28.9 offline planning — a client-managed draft sequence: a raw §38.1
/// body held locally (app-support JSON file) until it's pushed to a daemon as
/// a real sequence. Drafts are WILMA-side by design ("build offline, push when
/// ready"); the daemon never sees one until the push.
class DraftSequence {
  /// Local id, always carrying the [draftIdPrefix] so every consumer of
  /// `selectedSequenceIdProvider` can tell a draft from a daemon sequence id.
  final String id;
  final String name;

  /// Last-saved instant (UTC) — drives newest-first ordering in pickers.
  final DateTime updatedUtc;

  /// The raw NINA/§38.1 body, same shape as `SequenceDetail.body`.
  final Map<String, dynamic> body;

  const DraftSequence({
    required this.id,
    required this.name,
    required this.updatedUtc,
    required this.body,
  });

  Map<String, dynamic> toJson() => {
        'id': id,
        'name': name,
        'updated_utc': updatedUtc.toIso8601String(),
        'body': body,
      };

  /// Null on any malformed record — a corrupt draft file is skipped, never a
  /// crash (drafts are best-effort local state).
  static DraftSequence? fromJson(Object? json) {
    if (json is! Map) return null;
    final id = json['id'];
    final body = json['body'];
    if (id is! String || !isDraftSequenceId(id) || body is! Map) return null;
    return DraftSequence(
      id: id,
      name: json['name'] is String ? json['name'] as String : '',
      updatedUtc:
          DateTime.tryParse(json['updated_utc']?.toString() ?? '')?.toUtc() ??
              DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      body: Map<String, dynamic>.from(body),
    );
  }
}

/// Namespace prefix that keeps local draft ids disjoint from daemon sequence
/// ids (GUIDs), so a selected draft can never be sent to the daemon's
/// run-state / lifecycle endpoints by accident.
const String draftIdPrefix = 'draft:';

bool isDraftSequenceId(String? id) => id != null && id.startsWith(draftIdPrefix);
