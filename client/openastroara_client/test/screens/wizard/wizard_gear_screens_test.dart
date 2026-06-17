import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
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
