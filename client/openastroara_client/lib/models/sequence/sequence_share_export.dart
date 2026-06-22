/// Mirrors the daemon's §70.5 export response (`SequenceShareDto` from
/// `POST /api/v1/sequences/{id}/share-export`). The [manifest] is the full
/// sequence body JSON that the client writes straight to the user's chosen
/// `.araseq.json` file; [sequenceName] seeds the suggested filename.
///
/// (The daemon's `payload_bytes` is intentionally not surfaced: it's the size of
/// the compact wire JSON, but the client writes a pretty-printed file, so the
/// two never match — exposing it would mislead about the on-disk size. Same
/// rationale as `ProfileShareExport`.)
class SequenceShareExport {
  final String sequenceName;
  final Map<String, dynamic> manifest;

  const SequenceShareExport({
    required this.sequenceName,
    required this.manifest,
  });

  factory SequenceShareExport.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower. Fail loudly if the manifest is
    // absent/empty (null body, undecodable response, unexpected DTO) rather than
    // write an empty `{}` to disk and report a bogus success.
    final rawManifest = json['manifest'];
    if (rawManifest is! Map || rawManifest.isEmpty) {
      throw const FormatException(
          'Export response did not include a sequence-share manifest.');
    }
    return SequenceShareExport(
      sequenceName: json['sequence_name']?.toString() ?? '',
      manifest: Map<String, dynamic>.from(rawManifest),
    );
  }
}
