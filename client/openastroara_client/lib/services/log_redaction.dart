/// §54.6 always-blacklisted patterns, applied to the client log on export.
///
/// The daemon's bug-report bundle is downloaded from the daemon, so anything
/// the daemon strips never sees `client-errors.log`: the client writes that
/// file itself, beside the ZIP (#1112). The same blacklist therefore runs
/// here, on the way out. It is not user-toggleable and it is deliberately
/// narrow — credentials only. Hostnames, paths and coordinates stay in the
/// file, as §54.1 wants: the user reviews the export before sharing it.
class LogRedaction {
  LogRedaction._();

  static const tokenPlaceholder = '[REDACTED-TOKEN]';
  static const keyPlaceholder = '[REDACTED-KEY]';

  /// Line-local patterns. Order matters only for readability: each is
  /// applied to the whole line, so an overlap just redacts twice.
  static final List<RegExp> _lineRules = [
    // Our own header, in any casing, with or without a colon-space, and in
    // the `x-ara-token: [value]` form Dio's Headers.toString() prints.
    RegExp(
      r'(x-ara-token\s*[:=]\s*\[?)[A-Za-z0-9_\-.]{8,}',
      caseSensitive: false,
    ),
    RegExp(
      r'(x-openastroara-token\s*[:=]\s*\[?)[A-Za-z0-9_\-.]{8,}',
      caseSensitive: false,
    ),
    // Authorization: Bearer <jwt or opaque>
    RegExp(r'(\bBearer\s+)[A-Za-z0-9_\-.]{20,}'),
    // Query-string token=… (the DSS route cannot set a header, #1143).
    RegExp(r'([?&]token=)[A-Za-z0-9_\-.]{8,}', caseSensitive: false),
    // Vendor key prefixes that someone may have in their environment.
    RegExp(r'(?<![A-Za-z0-9])(sk_(?:live|test)?_?)[A-Za-z0-9]{8,}'),
    RegExp(r'(?<![A-Za-z0-9])(gh[pousr]_)[A-Za-z0-9]{20,}'),
    RegExp(r'(?<![A-Za-z0-9])(github_pat_)[A-Za-z0-9_]{20,}'),
    RegExp(r'(?<![A-Za-z0-9])(xox[bapors]-)[A-Za-z0-9\-]{10,}'),
    RegExp(r'(?<![A-Za-z0-9])(AKIA)[A-Z0-9]{16}'),
  ];

  /// A base64-looking run of 32+ characters ending in `=` padding. Kept
  /// separate because it has no prefix to preserve.
  static final RegExp _base64Rule = RegExp(
    r'(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{32,}={1,2}(?![A-Za-z0-9+/=])',
  );

  static final RegExp _beginKey = RegExp(
    r'-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----',
  );
  static final RegExp _endKey = RegExp(r'-----END [A-Z0-9 ]*PRIVATE KEY-----');

  /// Redacts the credential-shaped substrings of one line. Pure; the
  /// multi-line private-key rule lives in [LineRedactor].
  static String redactLine(String line) {
    var out = line;
    for (final rule in _lineRules) {
      out = out.replaceAllMapped(rule, (m) => '${m[1]}$tokenPlaceholder');
    }
    out = out.replaceAll(_base64Rule, tokenPlaceholder);
    return out;
  }

  /// Redacts a whole text, including private-key blocks that span lines.
  /// Newlines are preserved as `\n`.
  static String redactText(String text) {
    final r = LineRedactor();
    final lines = text.split('\n');
    final out = <String>[];
    for (final line in lines) {
      final kept = r.push(line);
      if (kept != null) out.add(kept);
    }
    return out.join('\n');
  }
}

/// Stateful line filter: swallows the inside of a PEM private-key block and
/// replaces the whole block with one placeholder line. Everything else goes
/// through [LogRedaction.redactLine]. Returns null for a line that is dropped.
class LineRedactor {
  bool _inKey = false;

  /// A line that starts a new log entry ([ClientErrorLog.entryMarker]) ends
  /// an unterminated key block: a key cut by the entry byte cap would
  /// otherwise hide every later entry in the export.
  static bool _isEntryStart(String line) => line.startsWith('=== ');

  String? push(String line) {
    if (_inKey) {
      if (LogRedaction._endKey.hasMatch(line)) {
        _inKey = false;
        return null;
      }
      if (!_isEntryStart(line)) return null;
      _inKey = false;
      // fall through: the marker line itself is kept
    }
    if (LogRedaction._beginKey.hasMatch(line)) {
      // A one-line key (BEGIN … END on the same line) closes immediately.
      _inKey = !LogRedaction._endKey.hasMatch(line);
      return LogRedaction.keyPlaceholder;
    }
    return LogRedaction.redactLine(line);
  }
}
