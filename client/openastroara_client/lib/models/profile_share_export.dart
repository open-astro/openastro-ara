/// Mirrors the daemon's §70 export response (`ProfileShareDto` from
/// `POST /api/v1/profiles/{id}/share-export`). The [manifest] is the full
/// `profile-share-v1` JSON (already stripped of paths / secrets / location /
/// network per §70.1) that the client writes straight to the user's chosen
/// file; [profileName] seeds the suggested filename.
///
/// (The daemon's `payload_bytes` is intentionally not surfaced: it's the size of
/// the compact wire JSON, but the client writes a pretty-printed file, so the
/// two never match — exposing it would mislead about the on-disk size.)
class ProfileShareExport {
  final String profileName;
  final Map<String, dynamic> manifest;

  const ProfileShareExport({
    required this.profileName,
    required this.manifest,
  });

  factory ProfileShareExport.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower (see Program.cs). Fail loudly if the
    // manifest is absent/empty (null body, undecodable response, unexpected DTO)
    // rather than write an empty `{}` to disk and report a bogus success.
    final rawManifest = json['manifest'];
    if (rawManifest is! Map || rawManifest.isEmpty) {
      throw const FormatException(
          'Export response did not include a profile-share manifest.');
    }
    return ProfileShareExport(
      profileName: json['profile_name']?.toString() ?? '',
      manifest: Map<String, dynamic>.from(rawManifest),
    );
  }
}
