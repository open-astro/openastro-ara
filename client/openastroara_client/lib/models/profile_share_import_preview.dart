/// Mirrors `ProfileShareImportPreviewDto` from the daemon's §70 profile-share
/// import API (`POST /api/v1/profiles/share-import`). The preview of a shared
/// `profile-share-v1` template before it's committed into a new profile: the
/// single-use [importToken] (carry it to the commit call), the proposed name,
/// human-readable [warnings] about what the template is/isn't, the
/// [droppedFields] the recipient must supply themselves, and the token's
/// [expiresUtc] deadline.
class ProfileShareImportPreview {
  final String importToken;
  final String profileName;
  final List<String> warnings;
  final List<String> droppedFields;
  final DateTime? expiresUtc;

  const ProfileShareImportPreview({
    required this.importToken,
    required this.profileName,
    this.warnings = const <String>[],
    this.droppedFields = const <String>[],
    this.expiresUtc,
  });

  factory ProfileShareImportPreview.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower (see Program.cs). Coerce defensively
    // so a missing/garbled field degrades to an empty value rather than throwing.
    return ProfileShareImportPreview(
      importToken: json['import_token']?.toString() ?? '',
      profileName: json['profile_name']?.toString() ?? '',
      warnings: _stringList(json['warnings']),
      droppedFields: _stringList(json['dropped_fields']),
      expiresUtc: DateTime.tryParse(json['expires_utc']?.toString() ?? ''),
    );
  }

  static List<String> _stringList(Object? value) => value is List
      ? value.map((e) => e?.toString() ?? '').toList(growable: false)
      : const <String>[];
}
