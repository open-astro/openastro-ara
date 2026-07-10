import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/widgets/sky_atlas/tonight_sky_panel.dart';

// §36.8 slice 4b — the what-if optics/mosaic dialog: what Apply/Reset write to
// tonightSkyOverridesProvider, what validation blocks, and that re-opening
// pre-fills the applied values. The panel itself is hosted so the flow starts
// from the real tune button.

Widget _host() => ProviderScope(
      overrides: [
        // The list content is irrelevant here — the dialog is the subject.
        tonightSkyProvider.overrideWith((ref) async => const []),
      ],
      child: const MaterialApp(home: Scaffold(body: TonightSkyPanel())),
    );

Future<void> _openDialog(WidgetTester tester) async {
  await tester.pumpWidget(_host());
  await tester.pump(); // resolve the tonightSky future
  await tester.tap(find.byIcon(Icons.tune));
  await tester.pumpAndSettle();
}

ProviderContainer _container(WidgetTester tester) =>
    ProviderScope.containerOf(
        tester.element(find.byType(TonightSkyPanel)));

Future<void> _enter(WidgetTester tester, String label, String text) =>
    tester.enterText(find.widgetWithText(TextFormField, label), text);

void main() {
  testWidgets('Apply writes the typed fields to the overrides provider',
      (tester) async {
    await _openDialog(tester);
    final container = _container(tester);

    await _enter(tester, 'Focal length', '530');
    await _enter(tester, 'Reducer / barlow', '0.7');
    await _enter(tester, 'Wide', '2');
    await _enter(tester, 'High', '3');
    await tester.tap(find.text('Apply'));
    await tester.pumpAndSettle();

    expect(find.byType(TonightOverridesDialog), findsNothing);
    expect(
      container.read(tonightSkyOverridesProvider),
      const TonightOverrides(
          focalLengthMm: 530, reducer: 0.7, mosaicX: 2, mosaicY: 3),
    );
  });

  testWidgets('blank optics fields stay null (profile-merged server-side)',
      (tester) async {
    await _openDialog(tester);
    final container = _container(tester);

    await _enter(tester, 'Pixel size', '3.76');
    await tester.tap(find.text('Apply'));
    await tester.pumpAndSettle();

    final o = container.read(tonightSkyOverridesProvider);
    expect(o, const TonightOverrides(pixelUm: 3.76));
    expect(o.focalLengthMm, isNull);
    expect(o.mosaicX, 1);
  });

  testWidgets('an out-of-range reducer blocks Apply (mirrors the server cap)',
      (tester) async {
    await _openDialog(tester);
    final container = _container(tester);

    await _enter(tester, 'Reducer / barlow', '12');
    await tester.tap(find.text('Apply'));
    await tester.pumpAndSettle();

    // The dialog stays open with an inline error; nothing was applied.
    expect(find.byType(TonightOverridesDialog), findsOneWidget);
    expect(container.read(tonightSkyOverridesProvider).isActive, isFalse);
  });

  testWidgets('a mosaic count beyond the cap blocks Apply', (tester) async {
    await _openDialog(tester);
    final container = _container(tester);

    await _enter(tester, 'Wide', '21');
    await tester.tap(find.text('Apply'));
    await tester.pumpAndSettle();

    expect(find.byType(TonightOverridesDialog), findsOneWidget);
    expect(container.read(tonightSkyOverridesProvider).isActive, isFalse);
  });

  testWidgets('Reset clears active overrides and closes', (tester) async {
    await _openDialog(tester);
    final container = _container(tester);
    container
        .read(tonightSkyOverridesProvider.notifier)
        .set(const TonightOverrides(reducer: 0.7, mosaicX: 2));

    await tester.tap(find.text('Reset'));
    await tester.pumpAndSettle();

    expect(find.byType(TonightOverridesDialog), findsNothing);
    expect(container.read(tonightSkyOverridesProvider), TonightOverrides.none);
  });

  testWidgets('re-opening pre-fills the applied overrides', (tester) async {
    await tester.pumpWidget(_host());
    await tester.pump();
    _container(tester).read(tonightSkyOverridesProvider.notifier).set(
        const TonightOverrides(focalLengthMm: 530, reducer: 0.7, mosaicX: 2));

    await tester.tap(find.byIcon(Icons.tune));
    await tester.pumpAndSettle();

    // Whole numbers render the way a user would type them ("530", not "530.0").
    expect(find.widgetWithText(TextFormField, '530'), findsOneWidget);
    expect(find.widgetWithText(TextFormField, '0.7'), findsOneWidget);
    // Mosaic fields always carry a value; the untouched axis shows the 1 default.
    expect(
      (tester.widget<TextFormField>(
              find.widgetWithText(TextFormField, 'Wide'))
          .controller)!
          .text,
      '2',
    );
  });

  testWidgets('the tune icon tints once overrides are active', (tester) async {
    await tester.pumpWidget(_host());
    await tester.pump();

    Icon icon() => tester.widget<Icon>(find.byIcon(Icons.tune));
    expect(icon().color, isNull);

    _container(tester)
        .read(tonightSkyOverridesProvider.notifier)
        .set(const TonightOverrides(mosaicX: 2));
    await tester.pump();
    expect(icon().color, isNotNull);
  });
}
