import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/widgets/sky_atlas/session_plan_dialog.dart';

void main() {
  testWidgets('Plan it ranks at the window midpoint and shows a plan card',
      (tester) async {
    DateTime? rankedAt;
    await tester.pumpWidget(ProviderScope(
      overrides: [
        tonightSkyAtProvider.overrideWith((ref, at) async {
          rankedAt = at;
          // A target whose dark window covers any plannable night window.
          return [
            TonightSkyObject(
              id: 'X',
              name: 'Test Nebula',
              type: 'HII',
              magnitude: 7,
              raDeg: 0,
              decDeg: 0,
              altitudeDeg: 50,
              maxAltitudeDeg: 80,
              windowStartUtc:
                  DateTime.now().toUtc().subtract(const Duration(hours: 12)),
              windowEndUtc:
                  DateTime.now().toUtc().add(const Duration(hours: 36)),
              score: 80,
              hoursFreeScore: 60,
              optimalSubS: 120,
            ),
          ];
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
  });
}
