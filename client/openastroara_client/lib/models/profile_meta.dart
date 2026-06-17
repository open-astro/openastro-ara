/// Mirrors `ProfileMetaDto` from the daemon's §37 multi-profile API
/// (`/api/v1/profiles`). Identity + timestamps for a saved profile; the
/// settings live in the section endpoints.
class ProfileMeta {
  final String id;
  final String name;
  final DateTime? createdUtc;
  final DateTime? updatedUtc;

  const ProfileMeta({
    required this.id,
    required this.name,
    this.createdUtc,
    this.updatedUtc,
  });

  factory ProfileMeta.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower (see Program.cs).
    return ProfileMeta(
      id: json['id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      createdUtc: DateTime.tryParse(json['created_utc'] as String? ?? ''),
      updatedUtc: DateTime.tryParse(json['updated_utc'] as String? ?? ''),
    );
  }
}
