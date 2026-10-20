/// §63.4 — what the daemon knows about a profile's OpenAstro Guider profile
/// (its "twin"), from `GET /api/v1/profiles/{id}/guider-twin`. The delete
/// dialog uses it to decide whether, and how, to offer deleting the twin.
class GuiderTwinPreview {
  /// The names the twin may carry on the guider: the profile's display name
  /// and the legacy `ara-<slug>-<id8>` name.
  final List<String> twinNames;

  /// Whether either name is on the guider; null when no guider is connected.
  final bool? exists;

  /// Another Ara profile with the same name shares this twin, so deleting it
  /// takes the survivor's calibration and dark library too.
  final bool shared;
  final String? sharedWith;
  final bool selectedOnDaemon;

  /// The delete can run right now; otherwise it is queued and [reason] says why.
  final bool canDeleteNow;
  final String? reason;

  const GuiderTwinPreview({
    required this.twinNames,
    required this.exists,
    required this.shared,
    this.sharedWith,
    this.selectedOnDaemon = false,
    required this.canDeleteNow,
    this.reason,
  });

  /// The name shown to the user: the display-name twin.
  String get displayName => twinNames.isEmpty ? '' : twinNames.first;

  factory GuiderTwinPreview.fromJson(Map<String, dynamic> json) {
    final names = json['twin_names'];
    return GuiderTwinPreview(
      twinNames: names is List ? [for (final n in names) n.toString()] : const [],
      exists: json['exists'] is bool ? json['exists'] as bool : null,
      shared: json['shared'] == true,
      sharedWith: json['shared_with']?.toString(),
      selectedOnDaemon: json['selected_on_daemon'] == true,
      canDeleteNow: json['can_delete_now'] == true,
      reason: json['reason']?.toString(),
    );
  }
}

/// What `DELETE /api/v1/profiles/{id}` did about the guider twin.
enum GuiderTwinOutcome { deleted, queued, kept, notFound, failed }

/// The body of a successful profile delete.
class ProfileDeleteResult {
  final GuiderTwinOutcome guiderProfile;
  final String? reason;

  const ProfileDeleteResult({required this.guiderProfile, this.reason});

  /// An old daemon answers 204 with no body: nothing was asked of the guider.
  static const kept = ProfileDeleteResult(guiderProfile: GuiderTwinOutcome.kept);

  factory ProfileDeleteResult.fromJson(Map<String, dynamic> json) {
    final outcome = switch (json['guider_profile']?.toString()) {
      'deleted' => GuiderTwinOutcome.deleted,
      'queued' => GuiderTwinOutcome.queued,
      'not_found' => GuiderTwinOutcome.notFound,
      'failed' => GuiderTwinOutcome.failed,
      _ => GuiderTwinOutcome.kept,
    };
    return ProfileDeleteResult(
        guiderProfile: outcome, reason: json['reason']?.toString());
  }

  /// The snackbar line after a delete, or null when there is nothing to say
  /// (the guider profile was not asked about).
  String? get message => switch (guiderProfile) {
        GuiderTwinOutcome.deleted => 'Guider profile deleted',
        GuiderTwinOutcome.queued =>
          'Guider profile will be deleted when the guider is free',
        GuiderTwinOutcome.notFound => 'No guider profile to delete',
        GuiderTwinOutcome.failed =>
          "Couldn't delete the guider profile: ${reason ?? 'the guider refused'}",
        GuiderTwinOutcome.kept => null,
      };

  bool get isFailure => guiderProfile == GuiderTwinOutcome.failed;
}
