import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// #1685: Simple Mode (its screens and the Sky Album) names its corner radii
/// and font sizes with the tokens in `lib/theme/ara_metrics.dart`, so a role
/// changes in one place and a new window starts on the tokens. This gate fails
/// on a bare number passed as a radius or a font size there.

/// The folders and files the gate reads.
const _scope = [
  'lib/screens/simple',
  'lib/screens/album/sky_album_screen.dart',
  'lib/widgets/album/album_card.dart',
];

/// What to write instead of each value, by role (DESIGN.md Shapes, Typography).
final _radiusTokens = <double, String>{
  4.0: 'AraRadius.field',
  6.0: 'AraRadius.card',
  8.0: 'AraRadius.container',
  10.0: 'AraRadius.simplePanel',
  12.0: 'AraRadius.pill',
};
final _fontTokens = <double, String>{
  12.0: 'kSimpleCaption',
  13.0: 'kSimpleUnit',
  22.0: 'kSimpleReading',
  30.0: 'kSimpleReadingLarge',
};

/// A bare value allowed in one file: a size that matches no documented role.
/// Key is `file:kind:value`, with the reason it is not a token. None of these
/// changed value in #1685; each is a design question, not a refactor.
const _allowed = <String, String>{
  'lib/screens/album/sky_album_screen.dart:radius:22': 'the search pill: round ends on a 44 px field',
  'lib/screens/album/sky_album_screen.dart:radius:16': 'the Album sheet: no documented role',
  'lib/screens/album/sky_album_screen.dart:fontSize:14': "kSimpleText's size on a button's own text style",
  'lib/screens/simple/simple_device_windows.dart:fontSize:26': 'the Dome and Safety readings: between reading (22) and large reading (30)',
  'lib/screens/simple/simple_focus_panel.dart:fontSize:40': "Manual focus's one hero number",
  'lib/screens/simple/simple_plan_panel.dart:radius:3': 'the progress bar: round ends on a 6 px bar',
  'lib/screens/simple/simple_plan_panel.dart:fontSize:11.5': "AraText.caption's size on a plan square's small line",
  'lib/screens/simple/simple_plan_panel.dart:fontSize:16': "Plan sheet's heading, used once",
  'lib/screens/simple/simple_preflight.dart:fontSize:11': "a Preflight step's label under its icon, used once",
  'lib/screens/simple/simple_preview_panel.dart:radius:2': 'the histogram bars: a hairline clip',
  'lib/screens/simple/simple_preview_panel.dart:radius:36': "an InkResponse's splash radius, not a corner",
  'lib/screens/simple/simple_plan_panel.dart:radius:36': "an InkResponse's splash radius, not a corner",
  'lib/screens/simple/simple_rig_storage.dart:fontSize:20': "Storage's drive name: no documented role",
  'lib/screens/simple/simple_safety_alarm.dart:fontSize:26': "the alarm's title, sized for contrast on Fault Red",
  'lib/screens/simple/simple_safety_alarm.dart:fontSize:16': "the alarm's detail",
  'lib/screens/simple/simple_safety_alarm.dart:fontSize:20': "the alarm's button: large text passes 3:1 on Fault Red",
  'lib/screens/simple/simple_shell.dart:fontSize:20': "the guide preview's empty line and the time wheel's digits: no documented role",
  'lib/screens/simple/simple_shell.dart:fontSize:14': "kSimpleText's size on the Ara sheet's links",
};

/// `Radius.circular(n)`, and a widget's own `radius: n` argument.
final _radius = RegExp(r'(?:Radius\.circular\(|\bradius:)\s*(\d+(?:\.\d+)?)\s*[,)\n]');
final _font = RegExp(r'fontSize:\s*(\d+(?:\.\d+)?)\s*[,)\n]');

/// Comments blanked to spaces, so offsets still give the right line.
String _code(String source) => source
    .replaceAllMapped(RegExp(r'/\*[\s\S]*?\*/'), (m) => m[0]!.replaceAll(RegExp(r'[^\n]'), ' '))
    .replaceAllMapped(RegExp(r'//[^\n]*'), (m) => ' ' * m[0]!.length);

/// Every bare radius and font size in [source], as "path:line: what to use".
List<String> bareSizes(String path, String source) {
  final code = _code(source);
  int line(int at) => '\n'.allMatches(code.substring(0, at)).length + 1;
  final found = <String>[];
  for (final (kind, re, tokens) in [
    ('radius', _radius, _radiusTokens),
    ('fontSize', _font, _fontTokens),
  ]) {
    for (final m in re.allMatches(code)) {
      final value = double.parse(m[1]!);
      if (_allowed.containsKey('$path:$kind:${m[1]}')) continue;
      final use = tokens[value];
      found.add('$path:${line(m.start)}: bare $kind ${m[1]}; '
          '${use == null ? 'no token has this value: add it to the allow-list with a reason' : 'use $use'}');
    }
  }
  return found;
}

void main() {
  test('the gate reads a multi-line call and ignores comments', () {
    const fixture = '''
final a = BorderRadius.circular(
  10);
// BorderRadius.circular(8) in a comment
/* fontSize: 12 */
final b = TextStyle(fontSize: 13,);
final c = SimpleSegment(radius: 8, label: 'x');
''';
    expect(bareSizes('x.dart', fixture), [
      'x.dart:1: bare radius 10; use AraRadius.simplePanel',
      'x.dart:6: bare radius 8; use AraRadius.container',
      'x.dart:5: bare fontSize 13; use kSimpleUnit',
    ]);
  });

  test('Simple Mode and the Sky Album name every corner radius and font size', () {
    final offenders = <String>[];
    for (final entry in _scope) {
      final files = FileSystemEntity.isDirectorySync(entry)
          ? Directory(entry).listSync(recursive: true).whereType<File>().where((f) => f.path.endsWith('.dart'))
          : [File(entry)];
      for (final f in files) {
        // Windows lists `lib/screens/simple\x.dart`; the allow-list keys use `/`.
        offenders.addAll(bareSizes(f.path.replaceAll(r'\', '/'), f.readAsStringSync()));
      }
    }
    expect(offenders, isEmpty, reason: '${offenders.length} bare sizes:\n${offenders.join('\n')}');
  });
}
