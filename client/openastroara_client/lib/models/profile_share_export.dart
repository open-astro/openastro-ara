/// Mirrors the daemon's §70 export response (`ProfileShareDto` from
/// `POST /api/v1/profiles/{id}/share-export`). The [manifest] is the full
/// `profile-share-v1` JSON (already stripped of paths / secrets / location /
/// network per §70.1) that the client writes straight to the user's chosen
/// file; [profileName] seeds the suggested filename and [payloadBytes] is the
/// serialized size for a confirmation.
class ProfileShareExport {
  final String profileName;
  final Map<String, dynamic> manifest;
  final int payloadBytes;

  const ProfileShareExport({
    required this.profileName,
    required this.manifest,
    this.payloadBytes = 0,
  });

  factory ProfileShareExport.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower (see Program.cs). The manifest is a
    // nested object; coerce defensively so a non-Map degrades to an empty map.
    final rawManifest = json['manifest'];
    return ProfileShareExport(
      profileName: json['profile_name']?.toString() ?? '',
      manifest: rawManifest is Map
          ? rawManifest.map((k, v) => MapEntry(k.toString(), v))
          : const <String, dynamic>{},
      payloadBytes: switch (json['payload_bytes']) {
        final int n => n,
        final num n => n.toInt(),
        _ => 0,
      },
    );
  }
}
