import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/util/friendly_error.dart';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  // #1898: Settings › Devices has no "Guider" row; the guider connects on Camera (Guide).
  test('every no-guider hint names the Camera (Guide) row in Settings › Devices, in both languages (#1898)', () {
    for (final l in [en, lookupAppLocalizations(const Locale('pt', 'BR'))]) {
      final row = l.shellSectionGuider;
      final where = '${l.shellSettings} › ${l.shellSectionDevices}';
      for (final hint in [l.guideConnectFirst, l.calibrationGuideNoGuider, l.rigSetupCalibrationConnect]) {
        expect(hint, allOf(contains(row), contains(where)), reason: l.localeName);
      }
    }
  });

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

  test('pt-BR has one word for the rig: setup', () {
    Map<String, dynamic> arb(String f) =>
        jsonDecode(File('lib/l10n/$f').readAsStringSync()) as Map<String, dynamic>;
    final br = arb('app_pt_BR.arb');
    final other = [
      for (final e in arb('app_en.arb').entries)
        if (!e.key.startsWith('@') &&
            RegExp(r'\brigs?\b', caseSensitive: false).hasMatch(e.value as String) &&
            (br[e.key] as String).contains('equipamento'))
          e.key,
    ];
    expect(other, isEmpty);
  });

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

  test('the English is US English: the project is maintained in the US', () {
    final words = RegExp(
        r'\b(centre\w*|centred|centring|cancell\w*|colour\w*|metres?|grey|favour\w*|behaviour\w*|licence|catalogue\w*)\b',
        caseSensitive: false);
    final en = jsonDecode(File('lib/l10n/app_en.arb').readAsStringSync()) as Map<String, dynamic>;
    final british = {
      for (final e in en.entries)
        if (!e.key.startsWith('@') && e.value is String && words.hasMatch(e.value as String)) e.key: e.value,
    };
    expect(british, isEmpty);
  });
}
