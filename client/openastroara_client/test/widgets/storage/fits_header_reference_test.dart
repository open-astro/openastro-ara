import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/widgets/storage/fits_header_reference.dart';

void main() {
  test('every keyword is legal FITS: unique and at most 8 characters', () {
    final seen = <String>{};
    for (final group in fitsHeaderReference) {
      for (final e in group.entries) {
        expect(e.keyword.length, lessThanOrEqualTo(8),
            reason: '${e.keyword} exceeds the FITS 8-character keyword limit');
        expect(e.keyword, equals(e.keyword.toUpperCase()),
            reason: 'FITS keywords are uppercase');
        expect(seen.add(e.keyword), isTrue,
            reason: '${e.keyword} is listed twice');
        expect(e.longName, isNotEmpty);
        expect(e.example, isNotEmpty);
      }
    }
  });

  test('every switch-controlled group names a real switch', () {
    const switches = {
      'Who took it',
      'Your site',
      'Optics',
      'Sensor temperature',
      'Sky & weather',
      'Sun & moon',
    };
    final named = fitsHeaderReference
        .where((g) => g.controlledBy != null)
        .map((g) => g.controlledBy!)
        .toSet();
    expect(named, switches,
        reason: 'the reference and the panel must describe the same switches');
  });

  testWidgets('the sheet reads as words, not just keywords', (tester) async {
    await tester.pumpWidget(ProviderScope(
        child: MaterialApp(
      home: Builder(
        builder: (context) => Scaffold(
          body: Center(
            child: TextButton(
              onPressed: () => showFitsHeaderReference(context),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    )));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.text('Everything a frame can carry'), findsOneWidget);
    // The essentials are present, first, and explicitly not switchable.
    expect(find.textContaining('ALWAYS WRITTEN'), findsOneWidget);
    expect(find.text('IMAGETYP'), findsOneWidget);
    expect(find.text('Frame type'), findsOneWidget);
    // Deeper groups live below the fold of the virtualized list — scroll.
    await tester.scrollUntilVisible(find.text('SQM'), 300,
        scrollable: find.byType(Scrollable).last);
    expect(find.text('Sky quality in magnitudes per arcsec²'), findsOneWidget);
    await tester.scrollUntilVisible(find.text('MOONPHSE'), 300,
        scrollable: find.byType(Scrollable).last);
    expect(find.text('Moon phase by name'), findsOneWidget);
  });

  testWidgets('settings-backed headers show the user\'s own values',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    container.read(siteSettingsProvider.notifier)
      ..setObserverName('Test Observer')
      ..setLatitudeDeg(51.477811)
      ..setLongitudeDeg(-0.001475)
      ..setElevationM(46);
    await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          home: Builder(
            builder: (context) => Scaffold(
              body: Center(
                child: TextButton(
                  onPressed: () => showFitsHeaderReference(context),
                  child: const Text('open'),
                ),
              ),
            ),
          ),
        )));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    // Live values replace the catalog examples...
    await tester.scrollUntilVisible(find.text('OBSERVER'), 100,
        scrollable: find.byType(Scrollable).last);
    expect(find.text('Test Observer'), findsOneWidget);
    expect(find.text('Jane Doe'), findsNothing);
    await tester.scrollUntilVisible(find.text('SITELAT'), 100,
        scrollable: find.byType(Scrollable).last);
    expect(find.text('51.477811'), findsOneWidget);
    // ...while unset ones keep their generic example.
    await tester.scrollUntilVisible(find.text('FOCALLEN'), 100,
        scrollable: find.byType(Scrollable).last);
    expect(find.text('448.0'), findsOneWidget);
  });

  testWidgets('a group whose switch is off reads as not written',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    container.read(filenamesSettingsProvider.notifier).setHeaderSite(false);
    // Real coordinates exist, but the group is off — the sheet must show the
    // generic example, not data it just said won't be written.
    container.read(siteSettingsProvider.notifier)
      ..setLatitudeDeg(51.477811)
      ..setLongitudeDeg(-0.001475);
    await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          home: Builder(
            builder: (context) => Scaffold(
              body: Center(
                child: TextButton(
                  onPressed: () => showFitsHeaderReference(context),
                  child: const Text('open'),
                ),
              ),
            ),
          ),
        )));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('SITELAT'), 100,
        scrollable: find.byType(Scrollable).last);
    // The off group is marked and struck through...
    expect(find.text('off — not written'), findsOneWidget);
    final lat = tester.widget<Text>(find.text('SITELAT'));
    expect(lat.style?.decoration, TextDecoration.lineThrough);
    // ...and its value column falls back to the generic example.
    expect(find.text('32.780278'), findsOneWidget);
    expect(find.text('51.477811'), findsNothing);
    // ...while an on group keeps its normal caption and no strike.
    expect(find.text('switch: Who took it'), findsOneWidget);
    final obs = tester.widget<Text>(find.text('OBSERVER'));
    expect(obs.style?.decoration, isNot(TextDecoration.lineThrough));
  });
}
