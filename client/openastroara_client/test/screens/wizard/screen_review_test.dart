import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/models/solver_recommendation.dart';
import 'package:openastroara/state/sky_atlas/data_manager_state.dart';
import 'package:openastroara/screens/wizard/screens/screen_data_and_review.dart';
import 'package:openastroara/state/wizard_state.dart';

import '../../helpers/fake_data_manager_client.dart';

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

  group('formatNumber', () {
    test('rounds away float noise and strips trailing zeros', () {
      expect(formatNumber(45.0), '45');
      expect(formatNumber(31.2), '31.2');
      expect(formatNumber(51.50000000000001), '51.5');
      expect(formatNumber(31.123456, decimals: 4), '31.1235');
      expect(formatNumber(-10.0), '-10');
    });
  });

  group('reviewValue formats doubles without float noise', () {
    test('a noisy double reads cleanly', () {
      expect(reviewValue(51.50000000000001), '51.5');
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
      FakeDataManagerClient? dataManager,
    }) async {
      final container = ProviderContainer(overrides: [
        dataManagerApiProvider.overrideWithValue(dataManager),
      ]);
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

  group('ScreenReview solver database (§36.10)', () {
    const twoPicks = SolverRecommendation(
      mainFovDeg: 1.9,
      guideFovDeg: 0.8,
      narrowestFovDeg: 0.8,
      band: 'sub-degree',
      packages: [
        SolverDatabasePick(role: 'camera', fovDeg: 1.9, band: 'general', packageId: 'astap-d05', packageName: 'ASTAP D05', sizeBytes: 108830225),
        SolverDatabasePick(role: 'guide camera', fovDeg: 0.8, band: 'sub-degree', packageId: 'astap-d50', packageName: 'ASTAP D50', sizeBytes: 951326950, isInstalled: true),
      ],
    );

    Future<(ProviderContainer, FakeDataManagerClient)> pump(
      WidgetTester tester, {
      SolverRecommendation rec = twoPicks,
      bool throwOnRecommend = false,
      void Function(ProfileDraft)? seed,
    }) async {
      final api = FakeDataManagerClient(recommendation: rec, throwOnRecommend: throwOnRecommend);
      final container = ProviderContainer(overrides: [
        dataManagerApiProvider.overrideWithValue(api),
      ]);
      addTearDown(container.dispose);
      seed?.call(container.read(wizardControllerProvider).draft);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: ScreenReview())),
      ));
      await tester.pumpAndSettle();
      return (container, api);
    }

    testWidgets('sends the draft optics (reducer, sensor, guide setup) and lists one row per camera',
        (tester) async {
      final (container, api) = await pump(tester, seed: (d) {
        d.telescope.focalLengthMm = 800;
        d.telescope.reducerFactor = 0.63;
        d.camera.pixelSizeMicrons = 3.76;
        d.camera.sensorWidthPx = 6248;
        d.camera.sensorHeightPx = 4176;
        d.guider.setupType = 'oag';
        d.guider.guidePixelSizeUm = 2.9;
      });
      final req = api.lastRequest!;
      expect(req.focalLengthMm, 800);
      expect(req.reducerFactor, 0.63);
      expect(req.sensorHeightPx, 4176);
      expect(req.pixelSizeUm, 3.76);
      expect(req.guideSetupType, 'oag');
      expect(req.guidePixelSizeUm, 2.9);
      expect(req.toJson()['guide_setup_type'], 'oag');

      expect(find.text('Plate-solver star database'), findsOneWidget);
      expect(find.textContaining('Camera: 1.90° field (general) → ASTAP D05 (103.8 MB)'), findsOneWidget);
      expect(find.textContaining('Guide camera: 0.80° field (sub-degree) → ASTAP D50 (already on the rig)'),
          findsOneWidget);
      // Only the missing one is queued; the installed guide database is not.
      final draft = container.read(wizardControllerProvider).draft;
      expect(draft.plateSolve.solverDatabasePackageIds, ['astap-d05']);
      expect(draft.plateSolve.downloadSolverDatabase, isTrue);
      expect(find.byKey(const ValueKey('review-solver-db-download')), findsOneWidget);
      expect(find.textContaining('Download after setup (103.8 MB'), findsOneWidget);
    });

    testWidgets('unticking the download keeps the ids but clears the flag Save reads', (tester) async {
      final (container, _) = await pump(tester);
      await tester.ensureVisible(find.byKey(const ValueKey('review-solver-db-download')));
      await tester.tap(find.byKey(const ValueKey('review-solver-db-download')));
      await tester.pump();
      final draft = container.read(wizardControllerProvider).draft;
      expect(draft.plateSolve.downloadSolverDatabase, isFalse);
      expect(draft.plateSolve.solverDatabasePackageIds, ['astap-d05']);
    });

    testWidgets('a field below the solver floor warns instead of offering a download', (tester) async {
      const tooNarrow = SolverRecommendation(
        mainFovDeg: 0.05,
        narrowestFovDeg: 0.05,
        band: 'too narrow',
        tooNarrow: true,
        reason: 'below the floor',
        packages: [
          SolverDatabasePick(role: 'camera', fovDeg: 0.05, band: 'too narrow', tooNarrow: true, reason: 'A 0.05° field is below the 0.15° ASTAP can solve.'),
        ],
      );
      final (container, _) = await pump(tester, rec: tooNarrow);
      expect(find.textContaining('cannot plate-solve'), findsOneWidget);
      expect(find.byKey(const ValueKey('review-solver-db-download')), findsNothing);
      expect(container.read(wizardControllerProvider).draft.plateSolve.solverDatabasePackageIds, isEmpty);
    });

    testWidgets('an unreachable rig reads as an error, never a crash, and queues nothing', (tester) async {
      final (container, _) = await pump(tester, throwOnRecommend: true);
      expect(find.text('rig unreachable'), findsOneWidget, reason: 'the error reads in the section, not a crash');
      expect(find.byKey(const ValueKey('review-solver-db-download')), findsNothing);
      expect(container.read(wizardControllerProvider).draft.plateSolve.solverDatabasePackageIds, isEmpty);
    });

    testWidgets('a failed recheck drops the ids of an earlier visit so Save queues nothing stale', (tester) async {
      final (container, api) = await pump(tester);
      final draft = container.read(wizardControllerProvider).draft;
      expect(draft.plateSolve.solverDatabasePackageIds, ['astap-d05']);
      api.throwOnRecommend = true;
      await tester.ensureVisible(find.text('Recheck'));
      await tester.tap(find.text('Recheck'));
      await tester.pumpAndSettle();
      expect(draft.plateSolve.solverDatabasePackageIds, isEmpty);
      expect(find.text('rig unreachable'), findsOneWidget);
    });

    testWidgets('an untouched reducer with no rig reads as the profile\'s value, never 1×', (tester) async {
      await pump(tester);
      expect(find.text("Profile's value (not set here)"), findsOneWidget);
    });

    testWidgets('no data manager (offline draft) explains the choice waits for a rig', (tester) async {
      final container = ProviderContainer(overrides: [
        dataManagerApiProvider.overrideWithValue(null),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: ScreenReview())),
      ));
      await tester.pumpAndSettle();
      expect(find.textContaining('No rig in reach'), findsOneWidget);
    });

    testWidgets('the Telescope section shows the reducer and effective focal length', (tester) async {
      await pump(tester, seed: (d) {
        d.telescope.focalLengthMm = 800;
        d.telescope.reducerFactor = 0.63;
      });
      expect(find.text('0.63× → 504 mm'), findsOneWidget);
    });
  });
}
