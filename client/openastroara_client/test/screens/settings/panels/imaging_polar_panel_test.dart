import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/settings/panels/imaging_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart'
    show polarAlignApiProvider;

class _Api implements PolarAlignClient {
  PolarAlignSettings settings = const PolarAlignSettings();
  final saved = <PolarAlignSettings>[];
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async => settings;
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

Future<_Api> _pump(WidgetTester tester) async {
  final api = _Api();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [polarAlignApiProvider.overrideWithValue(api)],
      child: const MaterialApp(home: Scaffold(body: ImagingPolarPanel())),
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
}
