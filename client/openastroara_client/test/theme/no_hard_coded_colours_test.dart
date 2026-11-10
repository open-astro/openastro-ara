import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// DESIGN.md: "Don't hard-code colours outside `lib/theme/`" (#1552). A colour
/// literal or a Material `Colors.*` constant anywhere else fails here with its
/// file and line; name its role in AraColors instead. `Colors.transparent` is
/// no colour and stays allowed.
final _hardCoded = RegExp(r'Color\(0x|(?<![A-Za-z])Colors\.(?!transparent\b)[a-z]');

void main() {
  test('no Color(0x…) literal outside lib/theme', () {
    final found = <String>[];
    for (final file in Directory('lib').listSync(recursive: true).whereType<File>()) {
      final path = file.path.replaceAll(r'\', '/');
      if (!path.endsWith('.dart') || path.startsWith('lib/theme/')) continue;
      final lines = file.readAsLinesSync();
      for (var i = 0; i < lines.length; i++) {
        final code = lines[i].split('//').first;
        if (_hardCoded.hasMatch(code)) found.add('$path:${i + 1}: ${lines[i].trim()}');
      }
    }
    expect(found, isEmpty, reason: 'name these colours in AraColors:\n${found.join('\n')}');
  });
}
