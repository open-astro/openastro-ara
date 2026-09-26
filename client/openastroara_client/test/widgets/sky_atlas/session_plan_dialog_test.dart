import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';
import 'package:openastroara/state/sky_atlas/target_preview_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/widgets/sky_atlas/session_plan_dialog.dart';
import 'package:openastroara/widgets/sky_atlas/target_preview.dart';

TonightSkyObject _allNight(String id, String name, double score) =>
    TonightSkyObject(
      id: id,
      name: name,
      type: 'HII',
      magnitude: 7,
      raDeg: 0,
      decDeg: 0,
      altitudeDeg: 50,
      maxAltitudeDeg: 80,
      // A dark window that covers any plannable night window.
      windowStartUtc:
          DateTime.now().toUtc().subtract(const Duration(hours: 12)),
      windowEndUtc: DateTime.now().toUtc().add(const Duration(hours: 36)),
      score: score,
      hoursFreeScore: score - 20,
      optimalSubS: 120,
    );

void main() {
  testWidgets('Plan it ranks at the window midpoint and shows a plan card',
      (tester) async {
    DateTime? rankedAt;
    await tester.pumpWidget(ProviderScope(
      overrides: [
        targetPreviewProvider.overrideWith((ref, key) async => null),
        tonightSkyAtProvider.overrideWith((ref, at) async {
          rankedAt = at;
          return [_allNight('X', 'Test Nebula', 80)];
        }),
      ],
      child: const MaterialApp(home: Scaffold(body: SessionPlanDialog())),
    ));

    await tester.tap(find.text('Plan it'));
    await tester.pumpAndSettle();

    expect(rankedAt, isNotNull,
        reason: 'planning must rank around the window midpoint');
    expect(find.text('Test Nebula'), findsOneWidget);
    // Sub counts render (overhead-adjusted, so just assert the shape).
    expect(find.textContaining('subs ×'), findsOneWidget);
    // Every planned target is actionable, and shows what it looks like
    // (here: the offline placeholder, since the preview is stubbed null).
    expect(find.byTooltip('Show on the planetarium'), findsOneWidget);
    expect(find.byIcon(Icons.image_not_supported_outlined), findsOneWidget);
    expect(find.byTooltip('Add to a run (3.0 h)'), findsOneWidget);
  });

  testWidgets('Swap offers the other candidates and replaces the target',
      (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        targetPreviewProvider.overrideWith((ref, key) async => null),
        tonightSkyAtProvider.overrideWith((ref, at) async => [
              _allNight('X', 'Test Nebula', 80),
              _allNight('Y', 'Runner Up', 70),
              _allNight('Z', 'Third Place', 60),
            ]),
      ],
      child: const MaterialApp(home: Scaffold(body: SessionPlanDialog())),
    ));
    await tester.tap(find.text('Plan it'));
    await tester.pumpAndSettle();
    expect(find.text('Test Nebula'), findsOneWidget);

    await tester.ensureVisible(find.text('Swap'));
    await tester.tap(find.text('Swap'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Runner Up'), findsOneWidget);
    expect(find.textContaining('Third Place'), findsOneWidget);
    expect(find.textContaining('Test Nebula'), findsOneWidget,
        reason: 'the planned object is not offered as its own swap');

    await tester.tap(find.textContaining('Third Place'));
    await tester.pumpAndSettle();
    expect(find.text('Third Place'), findsOneWidget);
    expect(find.text('Test Nebula'), findsNothing);
    // The slot's hours carry over to the replacement.
    expect(find.byTooltip('Add to a run (3.0 h)'), findsOneWidget);
  });

  testWidgets('Show on atlas closes the dialog and the plan survives reopen',
      (tester) async {
    final container = ProviderContainer(overrides: [
      targetPreviewProvider.overrideWith((ref, key) async => null),
      // A configured train so the rotation control is offered.
      opticsSettingsProvider.overrideWith(() => _FixedOptics()),
      tonightSkyAtProvider
          .overrideWith((ref, at) async => [_allNight('X', 'Test Nebula', 80)]),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => showDialog<void>(
                  context: context, builder: (_) => const SessionPlanDialog()),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Plan it'));
    await tester.pumpAndSettle();
    expect(find.text('Test Nebula'), findsOneWidget);

    // Dial a rotation for the slot; it rides along to the atlas.
    await tester.ensureVisible(find.byType(Slider));
    expect(find.text('0°'), findsOneWidget);
    final slider = tester.widget<Slider>(find.byType(Slider));
    slider.onChanged!(135);
    await tester.pumpAndSettle();
    expect(find.text('135°'), findsOneWidget);

    // Drag the preview: the frame moves off the catalogue centre. Screen-down
    // is south, so dragging DOWN aims south (−north), and the goto follows.
    await tester.ensureVisible(find.byType(TargetPreview));
    await tester.drag(find.byType(TargetPreview), const Offset(0, 40));
    await tester.pumpAndSettle();
    expect(find.textContaining('Aimed'), findsOneWidget);
    expect(find.textContaining('′ S'), findsOneWidget);
    expect(find.text('Recentre'), findsOneWidget);

    // Make it a 2×1 mosaic with the steppers; the subs line goes per-panel.
    await tester.ensureVisible(find.byTooltip('More cols'));
    await tester.tap(find.byTooltip('More cols'));
    await tester.pumpAndSettle();
    expect(find.textContaining('per panel (2 panels)'), findsOneWidget);
    expect(find.text('Single frame'), findsOneWidget);

    await tester.ensureVisible(find.byTooltip('Show on the planetarium'));
    await tester.tap(find.byTooltip('Show on the planetarium'));
    await tester.pumpAndSettle();
    expect(find.text('Test Nebula'), findsNothing, reason: 'dialog closed');
    final cmd = container.read(planetariumCommandProvider);
    expect(cmd?['type'], 'goto');
    expect(cmd?['frame'], true);
    expect(cmd?['name'], 'Test Nebula');
    expect(cmd?['ra'], closeTo(0.0, 1e-6), reason: 'a pure N/S drag keeps RA');
    expect(cmd?['dec'], lessThan(-0.01), reason: 'aimed south of the object');
    expect(cmd?['dss'], true, reason: 'show = see the real field');
    expect(cmd?['rot'], 135.0, reason: 'the dialled rotation presets the box');
    expect(cmd?['cols'], 2, reason: 'the planned grid presets the Frame panel');
    expect(cmd?['rows'], 1);
    expect(cmd?['overlap'], 10);

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.text('Test Nebula'), findsOneWidget,
        reason: 'the plan is kept across close/reopen');
    expect(find.text('Plan it again'), findsOneWidget);
    expect(find.text('135°'), findsOneWidget, reason: 'rotation kept too');
    expect(find.textContaining('(2 panels)'), findsOneWidget, reason: 'grid kept too');
  });
}

class _FixedOptics extends OpticsSettingsNotifier {
  @override
  OpticsSettings build() => const OpticsSettings(
      focalLengthMm: 250,
      reducerFactor: 1,
      sensorWidthPx: 6248,
      sensorHeightPx: 4176,
      pixelSizeUm: 3.76,
      apertureMm: 51);
}
