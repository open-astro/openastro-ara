import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  // #1593 S4: Preflight and the Queue's run are state, read across Screens;
  // they reach no Screen's file.
  for (final path in ['lib/state/simple_preflight_state.dart', 'lib/state/simple_run_state.dart']) {
    test('$path imports nothing from lib/screens', () {
      final imports = File(path)
          .readAsLinesSync()
          .where((l) => l.startsWith('import ') && l.contains('screens/'));
      expect(imports, isEmpty);
    });
  }
}
