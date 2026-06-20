/// Extract a safe file name from a `Content-Disposition` header, or null.
///
/// Prefers the RFC 5987 `filename*=UTF-8''<pct-encoded>` form (what ASP.NET
/// emits for a non-ASCII name), falls back to the plain `filename="..."`, and
/// strips any path component — defence-in-depth so a server-supplied
/// `filename=../../evil.zip` can't carry a path into an OS Save dialog.
String? fileNameFromContentDisposition(String? header) {
  if (header == null) return null;
  // RFC 5987 ext-value: charset'language'value, percent-encoded, never quoted.
  // Require the charset'language' prefix so a malformed prefix-less form falls
  // through to the plain branch / default.
  final extended =
      RegExp("filename\\*=[^']*'[^']*'([^;]+)", caseSensitive: false)
          .firstMatch(header);
  if (extended != null) {
    final raw = extended.group(1)!.trim();
    String decoded;
    try {
      decoded = Uri.decodeComponent(raw);
    } catch (_) {
      decoded = raw;
    }
    return _basename(decoded);
  }
  final plain =
      RegExp('filename="?([^";]+)', caseSensitive: false).firstMatch(header);
  final name = plain?.group(1)?.trim();
  return name == null ? null : _basename(name);
}

String? _basename(String name) {
  final last = name.split(RegExp(r'[/\\]')).last.trim();
  return last.isEmpty ? null : last;
}
