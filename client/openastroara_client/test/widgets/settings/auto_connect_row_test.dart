import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/widgets/settings/auto_connect_row.dart';
import 'package:openastroara/widgets/settings/section_load_state.dart';

import '../../support/fake_section_rig.dart';

Future<void> _pump(WidgetTester tester, FakeSectionRig? rig) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [profileApiProvider.overrideWithValue(rig?.api())],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(body: AutoConnectRow(EquipmentDeviceType.camera)),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Switch _switch(WidgetTester tester) =>
    tester.widget<Switch>(find.byType(Switch));

FakeSectionRig _rig() => FakeSectionRig({
  'equipment-connection': rigDefaults['equipment-connection'],
});

void main() {
  testWidgets('Auto-connect row: no rig reads off and cannot be switched', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(_switch(tester).value, isFalse);
    expect(_switch(tester).onChanged, isNull);
  });

  testWidgets('Auto-connect row: a load that failed reads off and cannot be '
      'switched', (tester) async {
    final rig = _rig()..failGet.add('equipment-connection');
    await _pump(tester, rig);
    expect(_switch(tester).value, isFalse);
    expect(_switch(tester).onChanged, isNull);
    expect(rig.puts, isEmpty);
  });

  testWidgets('Auto-connect row: no rig says the rig is not connected', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.text('Rig not connected'), findsOneWidget);
  });

  testWidgets('Auto-connect row: a failed load says it is not loaded', (
    tester,
  ) async {
    await _pump(tester, _rig()..failGet.add('equipment-connection'));
    expect(find.textContaining('Not loaded'), findsOneWidget);
  });

  testWidgets("Auto-connect row: a failed load offers Retry, and Retry loads "
      "the rig's copy", (tester) async {
    final rig = _rig()..failGet.add('equipment-connection');
    await _pump(tester, rig);
    rig.failGet.clear(); // The rig answers again.
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();
    expect(_switch(tester).value, isTrue);
    expect(_switch(tester).onChanged, isNotNull);
    expect(find.text('Retry'), findsNothing);
  });

  testWidgets("Auto-connect row: shows the rig's value and saves a change", (
    tester,
  ) async {
    final rig = _rig();
    await _pump(tester, rig);
    expect(_switch(tester).value, isTrue);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(rig.puts.single.body, containsPair('camera', false));
    expect(_switch(tester).value, isFalse);
  });

  testWidgets("Auto-connect row: a failed save puts the rig's value back", (
    tester,
  ) async {
    final rig = _rig()..failPut.add('equipment-connection');
    await _pump(tester, rig);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(rig.puts, isEmpty);
    expect(_switch(tester).value, isTrue);
    expect(find.byType(SnackBar), findsOneWidget);
  });

  testWidgets('Auto-connect row: a copy another device replaced is refused '
      'with a reload', (tester) async {
    final rig = _rig();
    await _pump(tester, rig);
    rig.savedElsewhere('equipment-connection');
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(rig.puts, isEmpty);
    expect(find.byType(SectionConflictBanner), findsOneWidget);
    // Reload brings the rig's copy back, with the switch where the rig has it.
    await tester.tap(find.byType(TextButton));
    await tester.pumpAndSettle();
    expect(find.byType(SectionConflictBanner), findsNothing);
    expect(_switch(tester).value, isTrue);
  });
}
