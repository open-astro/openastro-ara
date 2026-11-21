import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_live_stack_row.dart';
import 'package:openastroara/services/session_live_stack_api.dart';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  test('every Live Stack string reads in pt-BR, not as the English text', () {
    final strings = <String Function(AppLocalizations)>[
      (l) => l.simpleLiveStackReset,
      (l) => l.simpleLiveStackViewLastLight,
      (l) => l.simpleLiveStackLights(2),
      (l) => l.simpleLiveStackLeftOut(2),
      (l) => l.simpleLiveStackNotColour,
      (l) => l.simpleLiveStackTooLarge,
    ];
    for (final s in strings) {
      expect(s(pt), isNot(s(en)));
    }
  });

  test('the pt-BR readout counts the lights and the left-out ones', () {
    const status = SessionLiveStackStatus(
      enabled: true,
      stackSeq: 3,
      lightsStacked: 3,
      lightsUnregistered: 1,
    );
    expect(
      simpleLiveStackReadout(pt, status),
      'Live Stack · 3 quadros · 1 de fora',
    );
    expect(
      simpleLiveStackReadout(
        pt,
        const SessionLiveStackStatus(lightsStacked: 1),
      ),
      'Live Stack · 1 quadro',
    );
  });
}
