import 'dart:convert';

/// Decode the bytes of a user-picked profile-share file into the manifest map
/// the daemon's §70 import-preview endpoint expects.
///
/// Throws a [FormatException] carrying a user-facing message when the bytes
/// aren't a JSON object, so the picker flow can surface "that doesn't look like
/// a profile share file" instead of a raw decode error. (Schema validation —
/// is this actually a `profile-share-v1`? — stays server-side: the daemon
/// answers 422, the single source of truth.)
Map<String, dynamic> parseShareManifest(List<int> bytes) {
  Object? decoded;
  try {
    decoded = jsonDecode(utf8.decode(bytes));
  } catch (_) {
    throw const FormatException(
        "That file isn't a profile share — it couldn't be read as JSON.");
  }
  if (decoded is! Map<String, dynamic>) {
    throw const FormatException(
        "That file isn't a profile share — expected a JSON object.");
  }
  return decoded;
}

/// A safe suggested filename for an exported share, derived from the entity's
/// [name]. Strips characters that are illegal in filenames on any of the desktop
/// targets (`/ \ : * ? " < > |` + control chars), collapses runs of
/// whitespace/dashes, and falls back to [fallbackBase] when nothing usable is
/// left — then appends `.[extension]` so shares are recognizable.
///
/// Defaults produce a profile share (`<name>.araprofile.json`); the §70.5
/// sequence export passes `fallbackBase: 'sequence', extension: 'araseq.json'`.
String shareFileName(String name,
    {String fallbackBase = 'profile', String extension = 'araprofile.json'}) {
  final cleaned = name
      // filename-illegal chars + control chars (incl. tab/LF/CR) → dash.
      .replaceAll(RegExp(r'[\/\\:*?"<>|\x00-\x1f]'), '-')
      // remaining whitespace (spaces, NBSP, …) → dash, then collapse runs.
      .replaceAll(RegExp(r'\s+'), '-')
      .replaceAll(RegExp(r'-+'), '-')
      .replaceAll(RegExp(r'^-+|-+$'), '');
  var base = cleaned.isEmpty ? fallbackBase : cleaned;
  // An entity literally named after a Windows reserved device (CON, NUL, COM1…)
  // would otherwise yield e.g. NUL.araprofile.json, which Windows redirects to
  // the device. Prefix an underscore so the name is always a real file.
  if (_windowsReserved.hasMatch(base)) base = '_$base';
  return '$base.$extension';
}

final RegExp _windowsReserved =
    RegExp(r'^(con|prn|aux|nul|com[1-9]|lpt[1-9])$', caseSensitive: false);

/// A relative "import within ~N minutes" note for the import confirm dialog, or
/// null when there's no expiry. Relative (computed against the current instant)
/// rather than a wall-clock time, so it sidesteps date / timezone ambiguity and
/// reads naturally for the daemon's short (15-min) TTL tokens. [now] is injectable
/// for tests; both sides are normalized to UTC before comparing.
String? shareExpiryNote(DateTime? expiresUtc, {DateTime? now}) {
  if (expiresUtc == null) return null;
  final remaining =
      expiresUtc.toUtc().difference((now ?? DateTime.now()).toUtc());
  if (remaining.inSeconds <= 0) {
    return 'This preview has expired — pick the file again to import.';
  }
  final mins = remaining.inMinutes;
  final label = mins < 1
      ? 'less than a minute'
      : mins == 1
          ? 'about a minute'
          : 'about $mins minutes';
  return 'Import within $label — this preview expires after that.';
}
