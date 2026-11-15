import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/imaging_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart'
    show polarAlignApiProvider;
import 'package:openastroara/state/saved_server_state.dart'
    show activeServerProvider;

class _Api implements PolarAlignClient {
  PolarAlignSettings settings = const PolarAlignSettings();
  final saved = <PolarAlignSettings>[];
  Object? getError;
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async {
    if (getError != null) throw getError!;
    return settings;
  }

  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings next) async {
    saved.add(next);
    return settings = next;
  }

  @override
  Future<void> start({String? stage}) async {}
  @override
  Future<void> next() async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> complete() async {}
  @override
  Future<void> requestCapture() async {}
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

Future<_Api> _pump(WidgetTester tester, {_Api? rig, Locale? locale}) async {
  final api = rig ?? _Api();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'rig', port: 1),
        ),
      ],
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(body: ImagingPolarPanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('the Home switch saves use_home to the profile (#1736)', (
    tester,
  ) async {
    final api = await _pump(tester);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(api.saved.single.useHome, isFalse);
  });

  testWidgets(
    'the turn, verify step, rough exposure and tolerance each save their own field (#1736)',
    (tester) async {
      final api = await _pump(tester);
      final fields = find.byType(TextField);
      expect(fields, findsNWidgets(4));
      // Row order: tolerance, turn, verify step, rough exposure.
      final entered = ['2', '45', '20', '7.5'];
      for (var i = 0; i < 4; i++) {
        await tester.enterText(fields.at(i), entered[i]);
        await tester.testTextInput.receiveAction(TextInputAction.done);
        await tester.pumpAndSettle();
      }
      final last = api.saved.last;
      expect(last.targetToleranceArcmin, 2.0);
      expect(last.seedRotationDeg, 45.0);
      expect(last.checkStepDeg, 20.0);
      expect(last.coarseExposureSeconds, 7.5);
    },
  );

  testWidgets(
    'a failed load shows not loaded with Retry, and nothing can be saved (#1736)',
    (tester) async {
      final rig = _Api()..getError = StateError('rig gone');
      await _pump(tester, rig: rig);
      expect(find.byKey(const Key('section-retry')), findsOneWidget);
      expect(find.byType(TextField), findsNothing);
      expect(find.byType(Switch), findsNothing);
      expect(rig.saved, isEmpty);

      rig.getError = null;
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.byType(TextField), findsNWidgets(4));
    },
  );

  testWidgets(
    'a turn of 200 or a tolerance of 50 is refused: no PUT, the field keeps the saved value (#1736)',
    (tester) async {
      final api = await _pump(tester);
      final fields = find.byType(TextField);
      // Row order: tolerance, turn, verify step, rough exposure.
      await tester.enterText(fields.at(1), '200');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      await tester.enterText(fields.at(0), '50');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.saved, isEmpty);
      expect(tester.widget<TextField>(fields.at(1)).controller!.text, '60');
      expect(tester.widget<TextField>(fields.at(0)).controller!.text, '1');
      expect(find.textContaining('5 to 120'), findsOneWidget);
    },
  );

  testWidgets(
    'in pt-BR the section and every knob label are translated (#1736)',
    (tester) async {
      await _pump(tester, locale: const Locale('pt', 'BR'));
      for (final english in [
        'Polar align',
        'Tolerance',
        'Turn',
        'Verify step',
        'Rough step exposure',
      ]) {
        expect(find.textContaining(english), findsNothing, reason: english);
      }
      expect(find.text('Alinhamento polar'), findsOneWidget);
    },
  );
}
