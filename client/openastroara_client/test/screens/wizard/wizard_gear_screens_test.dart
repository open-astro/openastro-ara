import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_capture_setup.dart';
import 'package:openastroara/screens/wizard/screens/screen_device_setup.dart';
import 'package:openastroara/screens/wizard/screens/screen_profile_basics.dart';
import 'package:openastroara/screens/wizard/wizard_screens.dart';
import 'package:openastroara/state/wizard_state.dart';

void main() {
  // Pumps a single wizard screen in its own ProviderScope and returns the
  // container so the test can read back the draft the screen mutated.
  Future<ProviderContainer> pump(WidgetTester tester, Widget screen) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(home: Scaffold(body: screen)),
    ));
    return container;
  }

  testWidgets('Screen 1 writes the profile name into the draft', (tester) async {
    final container = await pump(tester, const ScreenProfileBasics());
    // First text field on the screen is "Profile name".
    await tester.enterText(find.byType(TextField).first, 'Backyard Texas');
    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.profileName, 'Backyard Texas');
  });

  testWidgets('Screen 1 normalizes a blank name back to null', (tester) async {
    final container = await pump(tester, const ScreenProfileBasics());
    await tester.enterText(find.byType(TextField).first, '   ');
    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.profileName, isNull);
  });

  testWidgets('Screen 4 derives focal ratio and stores telescope values',
      (tester) async {
    final container = await pump(tester, const ScreenTelescope());
    final fields = find.byType(TextField);
    // Order: name, focal length, aperture.
    await tester.enterText(fields.at(1), '714');
    await tester.enterText(fields.at(2), '102');
    await tester.pump();

    expect(find.text('f/7.0'), findsOneWidget);
    final t = container.read(wizardControllerProvider).draft.telescope;
    expect(t.focalLengthMm, 714);
    expect(t.apertureMm, 102);
  });

  testWidgets('Screen 5 computes image scale from pixel size + focal length',
      (tester) async {
    final container = await pump(tester, const ScreenCamera());
    // Pre-seed the focal length the camera screen reads from the shared draft.
    container.read(wizardControllerProvider).draft.telescope.focalLengthMm = 714;

    // Pixel size is the last field on the camera screen.
    await tester.enterText(find.byType(TextField).last, '3.76');
    await tester.pump();

    // 206.265 * 3.76 / 714 ≈ 1.09 arcsec/pixel.
    expect(find.textContaining('arcsec/pixel'), findsOneWidget);
    expect(find.textContaining('1.09'), findsOneWidget);
  });

  testWidgets('Screen 11 rejects an out-of-range radius and shows the error',
      (tester) async {
    final container = await pump(tester, const ScreenPlateSolve());
    final ps = container.read(wizardControllerProvider).draft.plateSolve;
    // Fields: ASTAP path (0), star DB (1), search radius (2).
    final radius = find.byType(TextField).at(2);

    await tester.enterText(radius, '999'); // out of range
    await tester.pump();
    expect(ps.searchRadiusDeg, isNull, reason: 'invalid value is not written');
    expect(find.textContaining('Must be greater than'), findsOneWidget);

    await tester.enterText(radius, '45'); // back in range
    await tester.pump();
    expect(ps.searchRadiusDeg, 45);
    expect(find.textContaining('Must be greater than'), findsNothing);
  });

  testWidgets('Screen 12 rejects a non-positive autofocus exposure',
      (tester) async {
    final container = await pump(tester, const ScreenAutofocus());
    final af = container.read(wizardControllerProvider).draft.autofocus;
    final exposure = find.byType(TextField).first; // exposure is the first field

    await tester.enterText(exposure, '0'); // must be >= 1
    await tester.pump();
    expect(af.exposureSeconds, isNull, reason: 'invalid value is not written');
    expect(find.textContaining('at least 1'), findsOneWidget);

    await tester.enterText(exposure, '8');
    await tester.pump();
    expect(af.exposureSeconds, 8);
    expect(find.textContaining('at least 1'), findsNothing);
  });

  testWidgets('Screen 12 enforces the steps V-curve bounds (3–31)',
      (tester) async {
    final container = await pump(tester, const ScreenAutofocus());
    final af = container.read(wizardControllerProvider).draft.autofocus;
    final steps = find.byType(TextField).at(1); // steps is the second field

    await tester.enterText(steps, '2'); // below the 3-point minimum
    await tester.pump();
    expect(af.steps, isNull);
    expect(find.textContaining('between 3 and 31'), findsOneWidget);

    await tester.enterText(steps, '32'); // above the cap
    await tester.pump();
    expect(af.steps, isNull);

    await tester.enterText(steps, '9'); // in range
    await tester.pump();
    expect(af.steps, 9);
    expect(find.textContaining('between 3 and 31'), findsNothing);
  });

  testWidgets('signed field keeps prior value on a partial "-" keystroke',
      (tester) async {
    final container = await pump(tester, const ScreenRotator());
    final minAngle = find.byType(TextField).first; // Min angle is first field.
    await tester.enterText(minAngle, '5');
    await tester.enterText(minAngle, '-'); // mid-typing a negative number
    final r = container.read(wizardControllerProvider).draft.rotator;
    // The bare "-" must not wipe the field back to null.
    expect(r.minAngleDeg, 5);
  });

  testWidgets('builders map wires the real gear screens for steps 1-10',
      (tester) async {
    late BuildContext ctx;
    await tester.pumpWidget(MaterialApp(
      home: Builder(builder: (c) {
        ctx = c;
        return const SizedBox();
      }),
    ));

    expect(wizardScreenBuilders[1]!(ctx), isA<ScreenProfileBasics>());
    expect(wizardScreenBuilders[4]!(ctx), isA<ScreenTelescope>());
    expect(wizardScreenBuilders[8]!(ctx), isA<ScreenMount>());
    expect(wizardScreenBuilders[10]!(ctx), isA<ScreenGuider>());
    // Steps 11+ are still placeholders, not gear screens.
    expect(wizardScreenBuilders[11]!(ctx), isNot(isA<ScreenGuider>()));
  });
}
