/// Finds string literals written straight into user-visible positions of a
/// Dart source: a `Text(...)`, a `text:` span, a tooltip, a snack bar message,
/// a named label/title/message/hint/semantics argument, or the `action:` of
/// `friendlyError`. Such text belongs in the `.arb` catalogue (issue #1751).
///
/// A literal is allowed when its line ends in [optOutMarker]: product names,
/// units and language names that read the same in every language.
library;

const optOutMarker = 'l10n-ok';

/// One literal found in a user-visible position.
class HardcodedString {
  const HardcodedString(this.line, this.value);
  final int line;
  final String value;
}

/// Constructors whose first positional argument is shown to a person.
const _textConstructors = {'Text', 'SelectableText'};

/// Named arguments whose string value is shown to a person.
const _labelNames = {
  'text',
  'label',
  'labelText',
  'title',
  'subtitle',
  'message',
  'tooltip',
  'hint',
  'hintText',
  'helperText',
  'errorText',
  'semanticLabel',
  'semanticsLabel',
  'description',
  'caption',
  'summary',
  'heading',
  'placeholder',
  'prefixText',
  'suffixText',
  'counterText',
  'action',
};

class _Tok {
  _Tok(this.kind, this.text, this.line, [this.value = '']);
  final String kind; // 'id' | 'p' | 's'
  final String text;
  final int line;
  final String value; // for 's': the raw literal body
}

bool _isIdStart(String c) => RegExp(r'[A-Za-z_]').hasMatch(c);
bool _isIdPart(String c) => RegExp(r'[A-Za-z0-9_]').hasMatch(c);

List<_Tok> _lex(String src, [int startLine = 1]) {
  final out = <_Tok>[];
  var i = 0;
  var line = startLine;

  // Returns the index just past the literal opening at [start] (a quote, or an
  // r-prefixed one); adjusts [line] for newlines inside it.
  int skipString(int start, List<String> body, List<_Tok> nested) {
    var j = start;
    var raw = false;
    if (src[j] == 'r') {
      raw = true;
      j++;
    }
    final q = src[j];
    final triple = src.startsWith(q * 3, j);
    j += triple ? 3 : 1;
    final b = StringBuffer();
    while (j < src.length) {
      final c = src[j];
      if (c == '\n') {
        line++;
      }
      if (!raw && c == r'\') {
        b.write(src.substring(j, j + 2 > src.length ? src.length : j + 2));
        if (j + 1 < src.length && src[j + 1] == '\n') {
          line++;
        }
        j += 2;
        continue;
      }
      if (triple ? src.startsWith(q * 3, j) : c == q) {
        j += triple ? 3 : 1;
        body.add(b.toString());
        return j;
      }
      if (!raw && c == r'$' && j + 1 < src.length && src[j + 1] == '{') {
        var depth = 1;
        b.write(r'${');
        j += 2;
        final exprStart = j;
        final exprLine = line;
        while (j < src.length && depth > 0) {
          final d = src[j];
          if (d == "'" || d == '"') {
            j = skipString(j, <String>[], <_Tok>[]);
            b.write("''");
            continue;
          }
          if (d == '\n') {
            line++;
          }
          if (d == '{') {
            depth++;
          } else if (d == '}') {
            depth--;
          }
          b.write(d);
          j++;
        }
        // Lex the body so a literal inside `${}` is seen too (issue #1751).
        nested.addAll(_lex(src.substring(exprStart, j - 1), exprLine));
        continue;
      }
      b.write(c);
      j++;
    }
    body.add(b.toString());
    return j;
  }

  while (i < src.length) {
    final c = src[i];
    if (c == '\n') {
      line++;
      i++;
    } else if (c == ' ' || c == '\t' || c == '\r') {
      i++;
    } else if (src.startsWith('//', i)) {
      while (i < src.length && src[i] != '\n') {
        i++;
      }
    } else if (src.startsWith('/*', i)) {
      final end = src.indexOf('*/', i + 2);
      final stop = end < 0 ? src.length : end + 2;
      line += '\n'.allMatches(src.substring(i, stop)).length;
      i = stop;
    } else if (c == "'" ||
        c == '"' ||
        (c == 'r' &&
            i + 1 < src.length &&
            (src[i + 1] == "'" || src[i + 1] == '"'))) {
      final startLine = line;
      final body = <String>[];
      final nested = <_Tok>[];
      i = skipString(i, body, nested);
      out.add(_Tok('s', '', startLine, body.single));
      out.addAll(nested); // after the outer token: same argument context
    } else if (_isIdStart(c)) {
      final s = i;
      while (i < src.length && _isIdPart(src[i])) {
        i++;
      }
      out.add(_Tok('id', src.substring(s, i), line));
    } else {
      out.add(_Tok('p', c, line));
      i++;
    }
  }
  return out;
}

/// The prose of a literal body: interpolations removed.
String _prose(String body) => body
    .replaceAll(RegExp(r'\$\{(?:[^{}]|\{[^{}]*\})*\}'), '')
    .replaceAll(RegExp(r'\$[A-Za-z_][A-Za-z0-9_]*'), '');

/// Every literal of [source] in a user-visible position that does not carry
/// [optOutMarker] on its line.
List<HardcodedString> scanHardcodedStrings(String source) {
  final toks = _lex(source);
  final lines = source.split('\n');
  final found = <HardcodedString>[];
  for (var k = 0; k < toks.length; k++) {
    final t = toks[k];
    if (t.kind != 's' || !RegExp(r'[A-Za-z]').hasMatch(_prose(t.value))) {
      continue;
    }
    if (!_inDisplayPosition(toks, k)) {
      continue;
    }
    if (t.line - 1 < lines.length && lines[t.line - 1].contains(optOutMarker)) {
      continue;
    }
    found.add(HardcodedString(t.line, t.value));
  }
  return found;
}

bool _inDisplayPosition(List<_Tok> toks, int k) {
  var depth = 0;
  var j = k - 1;
  for (; j >= 0; j--) {
    final t = toks[j];
    if (t.kind != 'p') {
      continue;
    }
    if (')]}'.contains(t.text)) {
      depth++;
    } else if ('([{'.contains(t.text)) {
      if (depth == 0) {
        break;
      }
      depth--;
    } else if (depth == 0 && (t.text == ',' || t.text == ';')) {
      break;
    }
  }
  // [j] is the opener or separator that begins this argument (or -1).
  final start = j + 1;
  if (start < toks.length &&
      toks[start].kind == 'id' &&
      start + 1 < toks.length &&
      toks[start + 1].kind == 'p' &&
      toks[start + 1].text == ':' &&
      _labelNames.contains(toks[start].text)) {
    return true;
  }
  if (j >= 1 && toks[j].text == '(' && toks[j - 1].kind == 'id') {
    // First positional argument of Text( / SelectableText( / Text.rich-free.
    return _textConstructors.contains(toks[j - 1].text);
  }
  return false;
}

/// The ratchet key for a file path: forward slashes on every platform
/// (Windows lists `lib\help\registry.dart`).
String ratchetKey(String path) => path.replaceAll(r'\', '/');

/// The problems of [found] against the per-file counts in [allowed]: a file
/// over its count, a file under it (lower the entry), a stale entry.
List<String> ratchetProblems(
  Map<String, List<HardcodedString>> found,
  Map<String, int> allowed,
) {
  final problems = <String>[];
  for (final e in found.entries) {
    final limit = allowed[e.key] ?? 0;
    if (e.value.length > limit) {
      problems.add(
        '${e.key}: ${e.value.length} hard-coded strings, ratchet allows $limit. '
        'Move the text to app_en.arb (and app_pt_BR.arb), or mark a language-neutral one `// $optOutMarker`:\n'
        '${e.value.map((h) => _show(e.key, h)).join('\n')}',
      );
    } else if (e.value.length < limit) {
      problems.add(
        '${e.key}: ratchet allows $limit but only ${e.value.length} are left. '
        'Lower the entry to ${e.value.length} (UPDATE_RATCHET=1).',
      );
    }
  }
  for (final k in allowed.keys) {
    if (!found.containsKey(k)) {
      problems.add(
        '$k: ratchet entry is stale, the file has no hard-coded strings left. Delete the entry.',
      );
    }
  }
  return problems;
}

String _show(String path, HardcodedString h) {
  final v = h.value.length > 80 ? '${h.value.substring(0, 80)}…' : h.value;
  return '  $path:${h.line}  "$v"';
}
