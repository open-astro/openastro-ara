import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/screens/wizard/screens/screen_data_and_review.dart';
import 'package:openastroara/state/wizard_state.dart';

void main() {
  group('reviewValue', () {
    test('null and blank strings read as "Not set"', () {
      expect(reviewValue(null), 'Not set');
      expect(reviewValue(''), 'Not set');
      expect(reviewValue('   '), 'Not set');
    });

    test('bools render as Yes / No', () {
      expect(reviewValue(true), 'Yes');
      expect(reviewValue(false), 'No');
    });

    test('strings are trimmed; numbers stringify', () {
      expect(reviewValue('  Backyard  '), 'Backyard');
      expect(reviewValue(42), '42');
    });
  });

  group('formatDuration', () {
    test('whole minutes collapse; otherwise seconds; null is "Not set"', () {
      expect(formatDuration(null), 'Not set');
      expect(formatDuration(const Duration(seconds: 30)), '30 s');
      expect(formatDuration(const Duration(seconds: 90)), '90 s');
      expect(formatDuration(const Duration(minutes: 5)), '5 min');
      expect(formatDuration(const Duration(seconds: 60)), '1 min');
    });
  });

  group('assignedEquipment', () {
    test('lists assigned slots, else "None assigned"', () {
      expect(assignedEquipment(EquipmentSlots()), 'None assigned');
      final e = EquipmentSlots()
        ..cameraDeviceId = 'cam-1'
        ..mountDeviceId = 'mnt-1';
      expect(assignedEquipment(e), 'Camera, Mount');
    });
  });

  group('ScreenReview', () {
    Future<ProviderContainer> pump(
      WidgetTester tester, {
      void Function(ProfileDraft)? seed,
    }) async {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      // Intentional: mutate the live (mutable) ProfileDraft in place before the
      // first pump so seeded values appear on the initial build. ProfileDraft is
      // a mutable bag by design (see profile_draft.dart) — if it ever becomes
      // immutable this seeding must switch to overriding the controller.
      seed?.call(container.read(wizardControllerProvider).draft);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: ScreenReview())),
      ));
      return container;
    }

    testWidgets('renders section titles and seeded values', (tester) async {
      await pump(tester, seed: (d) {
        d.profileName = 'Backyard Texas';
        d.telescope.focalLengthMm = 714;
        d.telescope.apertureMm = 102;
      });
      await tester.pumpAndSettle();
      expect(find.text('Profile basics'), findsOneWidget);
      expect(find.text('Telescope'), findsOneWidget);
      expect(find.text('Backyard Texas'), findsOneWidget);
      // Derived focal ratio 714 / 102 = f/7.0.
      expect(find.text('f/7.0'), findsOneWidget);
    });

    testWidgets('Edit on the first section jumps to its step', (tester) async {
      final container = await pump(tester);
      // Move off step 1 so the Profile-basics Edit (→ step 1) is observable.
      container.read(wizardControllerProvider.notifier).jumpTo(8);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Edit').first); // Profile basics → step 1
      await tester.pump();
      expect(container.read(wizardControllerProvider).step, 1);
    });

    testWidgets('AlpacaBridge Edit jumps to step 2, not the assign step',
        (tester) async {
      final container = await pump(tester, seed: (d) {
        d.alpacaBridgeAddress = '192.168.1.50:11111';
      });
      await tester.pumpAndSettle();
      // Section order: Profile basics(1), Connection(2), Equipment(3)… — the
      // Connection section owns the AlpacaBridge row and must edit step 2.
      expect(find.text('192.168.1.50:11111'), findsOneWidget);
      await tester.tap(find.text('Edit').at(1));
      await tester.pump();
      expect(container.read(wizardControllerProvider).step, 2);
    });

    testWidgets('a half-set rotator range reads as "Not set"', (tester) async {
      await pump(tester, seed: (d) {
        d.rotator.minAngleDeg = 0; // max left unset
      });
      await tester.pumpAndSettle();
      // Range needs both ends — a partial entry must not render "Not set – …".
      expect(find.textContaining('–'), findsNothing);
    });

    testWidgets('unset fields read as "Not set"', (tester) async {
      await pump(tester);
      await tester.pumpAndSettle();
      // A fresh draft leaves the profile name unset.
      expect(find.text('Not set'), findsWidgets);
    });
  });
}
