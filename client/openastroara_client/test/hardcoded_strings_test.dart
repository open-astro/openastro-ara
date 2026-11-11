import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import '../../../scripts/l10n/hardcoded_strings.dart';

/// Ratchet for #1751 (every string in pt-BR): text shown to a person belongs in
/// `lib/l10n/*.arb`, not in a Dart literal. [scanHardcodedStrings] finds the
/// literals in display positions; `hardcoded_strings_ratchet.txt` lists, per
/// file, how many are still allowed. A count may only go down: a file over its
/// number fails, and so does a file under it (lower the number), so the list
/// shrinks to empty and stays there.
///
/// To pass a literal that reads the same in every language (a product name, a
/// unit, a language's own name), end its line with `// l10n-ok`.
/// After migrating a file, run with `UPDATE_RATCHET=1` to write the new counts.
const _ratchetPath = 'test/hardcoded_strings_ratchet.txt';

Map<String, int> _readRatchet() {
  final out = <String, int>{};
  for (final raw in File(_ratchetPath).readAsLinesSync()) {
    final line = raw.trim();
    if (line.isEmpty || line.startsWith('#')) {
      continue;
    }
    final at = line.lastIndexOf(' ');
    out[line.substring(0, at)] = int.parse(line.substring(at + 1));
  }
  return out;
}

Map<String, List<HardcodedString>> _scanLib() {
  final found = <String, List<HardcodedString>>{};
  final files =
      Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where(
            (f) => f.path.endsWith('.dart') && !f.path.startsWith('lib/l10n/'),
          )
          .toList()
        ..sort((a, b) => a.path.compareTo(b.path));
  for (final f in files) {
    final hits = scanHardcodedStrings(f.readAsStringSync());
    if (hits.isNotEmpty) {
      found[f.path] = hits;
    }
  }
  return found;
}

void main() {
  test('user-visible strings come from the catalogue, within the ratchet', () {
    final found = _scanLib();
    if (Platform.environment['UPDATE_RATCHET'] == '1') {
      final keys = found.keys.toList()..sort();
      File(_ratchetPath).writeAsStringSync(
        [
          '# File and the number of hard-coded user-visible literals still allowed (#1751).',
          '# Counts only go down. See test/hardcoded_strings_test.dart.',
          for (final k in keys) '$k ${found[k]!.length}',
          '',
        ].join('\n'),
      );
      return;
    }
    final problems = ratchetProblems(found, _readRatchet());
    expect(problems, isEmpty, reason: '\n${problems.join('\n\n')}\n');
  });

  group('the ratchet', () {
    final one = {
      'lib/a.dart': scanHardcodedStrings("Widget a() => Text('New words');\n"),
    };

    test('a new literal fails, naming file, line and string', () {
      final p = ratchetProblems(one, {});
      expect(
        p.single,
        allOf(
          contains('lib/a.dart'),
          contains('lib/a.dart:1'),
          contains('New words'),
        ),
      );
    });

    test('a stale entry fails: lowered count and vanished file', () {
      expect(
        ratchetProblems(one, {'lib/a.dart': 3}).single,
        contains('Lower the entry to 1'),
      );
      expect(
        ratchetProblems({}, {'lib/gone.dart': 1}).single,
        contains('stale'),
      );
    });

    test('a marked literal passes', () {
      final marked = {
        'lib/a.dart': scanHardcodedStrings(
          "Widget a() => Text('Ara'); // l10n-ok\n",
        ),
      };
      expect(
        ratchetProblems({...marked}..removeWhere((_, v) => v.isEmpty), {}),
        isEmpty,
      );
    });

    test('a file within its count passes', () {
      expect(ratchetProblems(one, {'lib/a.dart': 1}), isEmpty);
    });
  });

  group('the scanner', () {
    test('flags a literal in Text, a tooltip, a snack bar and friendlyError action', () {
      const src = r'''
Widget a() => Text('Hello there');
Widget b() => IconButton(tooltip: 'Close it', onPressed: null);
void c() => showSnackBar(SnackBar(content: Text('Saved')));
String d() => friendlyError(l, e, action: 'save that');
Widget e() => Text(ok ? 'Yes' : 'No');
Widget f() => Text('${ok ? 'Maybe' : 'Never'}');
String g() => 'Guider: ${friendlyError(l, e, action: 'push it')}';
''';
      expect(scanHardcodedStrings(src).map((h) => h.value), [
        'Hello there',
        'Close it',
        'Saved',
        'save that',
        'Yes',
        'No',
        'Maybe',
        'Never',
        'push it',
      ]);
    });

    test('names the line of each finding', () {
      const src = "var a = 1;\n\nWidget a() => Text('Late');\n";
      expect(scanHardcodedStrings(src).single.line, 3);
    });

    test('lets a marked literal pass', () {
      const src = "Widget a() => Text('OpenAstro Ara'); // l10n-ok\n";
      expect(scanHardcodedStrings(src), isEmpty);
    });

    test('ignores catalogue text, ids, symbols and comments', () {
      const src = r'''
// Text('in a comment')
Widget a() => Text(l10n.title);
Widget b() => Text('•');
Widget c() => Text('$count');
final m = {'title': 'a key value'};
Widget d() => Foo(id: 'machine id', child: Text(l10n.x));
''';
      expect(scanHardcodedStrings(src), isEmpty);
    });

    test('reads adjacent and interpolated literals', () {
      const src = r'''
Widget a() => Text('Shot $n of '
    'the ${total} total');
''';
      expect(scanHardcodedStrings(src).length, 2);
    });
  });
}
