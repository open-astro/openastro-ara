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
