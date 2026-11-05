import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/gps_site_fill.dart';
import 'package:openastroara/widgets/sky_atlas/stellarium_view.dart';

/// #1198: the zoom row used to hinge on an inline `Platform.isAndroid ||
/// Platform.isIOS` inside a private widget, so it could not be tested.
void main() {
  Future<void> pump(WidgetTester tester, {void Function(double)? onZoom}) {
    return tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: PlanetariumSearchBar(
            controller: TextEditingController(),
            onSubmit: () {},
            onCoordinates: () {},
            onTonight: () {},
            tonightOpen: false,
            onRotate: () {},
            rotateOpen: false,
            onZoom: onZoom,
          ),
        ),
      ),
    );
  }

  test('touch platforms are Android and iOS only', () {
    expect(isTouchPlatform(ClientPlatform.android), isTrue);
    expect(isTouchPlatform(ClientPlatform.iOS), isTrue);
    for (final p in [
      ClientPlatform.macOS,
      ClientPlatform.windows,
      ClientPlatform.linux,
    ]) {
      expect(isTouchPlatform(p), isFalse, reason: '$p has a wheel/trackpad');
    }
  });

  testWidgets('zoom row appears with an onZoom and sends the factors', (
    tester,
  ) async {
    final factors = <double>[];
    await pump(tester, onZoom: factors.add);
    await tester.tap(find.byTooltip('Zoom in'));
    await tester.tap(find.byTooltip('Zoom out'));
    await tester.tap(find.byTooltip('Reset view'));
    expect(factors, [0.6, 1 / 0.6, 0]);
  });

  testWidgets('no zoom row without an onZoom (desktop)', (tester) async {
    await pump(tester);
    expect(find.byTooltip('Zoom in'), findsNothing);
    expect(find.byTooltip('Reset view'), findsNothing);
    expect(find.byTooltip('Target by coordinates (RA/Dec)'), findsOneWidget);
  });

  // #1554: at 768 pt portrait the two labelled buttons plus the icon buttons
  // left the field ~70 pt wide, so the hint read "Se...".
  Future<double> fieldWidth(WidgetTester tester, Size size) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await pump(tester, onZoom: (_) {});
    expect(tester.takeException(), isNull);
    return tester.getSize(find.byType(TextField)).width;
  }

  testWidgets('768x1024 portrait keeps a usable search field', (tester) async {
    expect(
      await fieldWidth(tester, const Size(768, 1024)),
      greaterThanOrEqualTo(200),
    );
    expect(find.byKey(const Key('planning-rotate-toggle')), findsOneWidget);
    expect(find.byTooltip('Tonight\'s Sky'), findsOneWidget);
  });

  testWidgets('1024x768 landscape keeps the labelled buttons', (tester) async {
    await fieldWidth(tester, const Size(1024, 768));
    expect(find.text('Rotate camera'), findsOneWidget);
    expect(find.text("Tonight's Sky"), findsOneWidget);
  });
}
