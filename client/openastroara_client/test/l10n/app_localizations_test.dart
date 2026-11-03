import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  test('one hidden message is one message to a screen reader', () {
    expect(en.pillsMoreSemantics(1), '1 more message');
    expect(en.pillsMoreSemantics(3), '3 more messages');
  });

  test(
    'Portuguese has every English message, and the iPad set to pt-BR gets it',
    () {
      Set<String> keys(String f) => (jsonDecode(
        File('lib/l10n/$f').readAsStringSync(),
      ) as Map<String, dynamic>).keys.where((k) => !k.startsWith('@')).toSet();
      expect(keys('app_pt.arb'), keys('app_en.arb'));
      final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
      expect(pt.localeName, 'pt');
      expect(pt.pillsMoreSemantics(1), isNot(en.pillsMoreSemantics(1)));
    },
  );
}
