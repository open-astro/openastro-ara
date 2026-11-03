import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/util/friendly_error.dart';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  test('one hidden message is one message to a screen reader', () {
    expect(en.pillsMoreSemantics(1), '1 more message');
    expect(en.pillsMoreSemantics(3), '3 more messages');
  });

  test(
    'Brazilian Portuguese has every English message, and the iPad set to pt-BR gets it',
    () {
      Set<String> keys(String f) => (jsonDecode(
        File('lib/l10n/$f').readAsStringSync(),
      ) as Map<String, dynamic>).keys.where((k) => !k.startsWith('@')).toSet();
      expect(keys('app_pt_BR.arb'), keys('app_en.arb'));
      expect(keys('app_pt.arb'), isEmpty,
          reason: 'only the base gen-l10n needs: pt alone is Portugal too');
      final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
      expect(pt.localeName, 'pt_BR');
      expect(pt.pillsMoreSemantics(1), isNot(en.pillsMoreSemantics(1)));
    },
  );

  test('Portuguese from Portugal reads English: only pt-BR is translated', () {
    final ptPt = lookupAppLocalizations(const Locale('pt', 'PT'));
    expect(ptPt.pillsMoreSemantics(1), en.pillsMoreSemantics(1));
  });

  test('shared wording reads in Portuguese, the action included', () {
    final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
    expect(
      friendlyError(
        pt,
        const FormatException('x'),
        action: pt.filterDropdownActionChange,
      ),
      startsWith('Não foi possível trocar o filtro — '),
    );
    expect(friendlyError(pt, Exception()), 'Não foi possível fazer isso.');
    expect(guiderRuntimeText(pt, GuiderRuntimeState.stopped), 'Sem guiagem');
  });
}
