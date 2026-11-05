import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_wallpaper.dart';
import 'package:openastroara/widgets/ara_mark.dart';

void main() {
  Future<StreamController<Gravity>> pump(WidgetTester tester, {Size size = const Size(768, 1024)}) async {
    final gravity = StreamController<Gravity>.broadcast();
    addTearDown(gravity.close);
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(home: SimpleWallpaper(gravity: gravity.stream)));
    return gravity;
  }

  List<Offset> shifts(WidgetTester tester) => [
    for (var i = 0; i < 3; i++)
      () {
        final t = tester.widget<Transform>(find.byKey(Key('simple-wallpaper-layer-$i'))).transform.getTranslation();
        return Offset(t.x, t.y);
      }(),
  ];

  Future<void> feed(WidgetTester tester, StreamController<Gravity> g, Gravity v) async {
    g.add(v);
    await tester.pumpAndSettle();
  }

  testWidgets('stars only: no mark on the wallpaper (user, 2026-11-06)', (tester) async {
    await pump(tester);
    expect(find.byType(AraMark), findsNothing);
  });

  testWidgets('upright in portrait, a roll to the right moves the stars left, nearer ones further; the first '
      'reading is centre (#1602)', (tester) async {
    final g = await pump(tester);
    await feed(tester, g, (x: 0, y: .95, z: .3));
    expect(shifts(tester), everyElement(Offset.zero), reason: 'the pose it starts in');
    await feed(tester, g, (x: .3, y: .9, z: .3));
    final s = shifts(tester);
    expect(s[0].dx, lessThan(0));
    expect(s[1].dx, lessThan(s[0].dx));
    expect(s[2].dx, lessThan(s[1].dx));
    expect(s[2].dy.abs(), lessThan(s[2].dx.abs() / 4), reason: 'a roll is sideways');
  });

  testWidgets('upright in landscape the roll still moves the stars sideways, not up and down (user, 2026-11-06)',
      (tester) async {
    // Turned a quarter clockwise: the device's right edge is down, so gravity
    // reads along -x, and the screen's right is the device's top (+y).
    final g = await pump(tester, size: const Size(1024, 768));
    await feed(tester, g, (x: -.95, y: 0, z: .3));
    await feed(tester, g, (x: -.9, y: .3, z: .3));
    final s = shifts(tester)[2];
    expect(s.dx, lessThan(0), reason: 'the same way as in portrait');
    expect(s.dy.abs(), lessThan(s.dx.abs() / 4));
  });

  testWidgets('upright in landscape turned the other way, gravity picks the bottom, not the window shape (#1602)',
      (tester) async {
    // Turned a quarter counter-clockwise: the device's left edge is down, so
    // gravity reads along +x; the window-shape guess would take the right edge.
    final g = await pump(tester, size: const Size(1024, 768));
    await feed(tester, g, (x: .95, y: 0, z: .3));
    await feed(tester, g, (x: .9, y: -.3, z: .3));
    final s = shifts(tester)[2];
    expect(s.dx, lessThan(0), reason: 'the same way as in portrait');
    expect(s.dy.abs(), lessThan(s.dx.abs() / 4));
  });

  testWidgets('a tilt back moves the stars vertically even standing upright (#1602)', (tester) async {
    final g = await pump(tester);
    await feed(tester, g, (x: 0, y: .98, z: .2));
    // About 15° back: gravity moves from y into z.
    await feed(tester, g, (x: 0, y: .9, z: .44));
    final s = shifts(tester)[2];
    expect(s.dy.abs(), greaterThan(10), reason: 'not near-dead when upright');
  });

  testWidgets('a reading within the dead band leaves the stars where they are (#1602)', (tester) async {
    final g = await pump(tester);
    await feed(tester, g, (x: 0, y: .95, z: .3));
    await feed(tester, g, (x: .3, y: .9, z: .3));
    final before = shifts(tester);
    await feed(tester, g, (x: .302, y: .9, z: .3));
    expect(shifts(tester), before);
  });

  testWidgets("iOS Reduce Motion: still, and the sensor isn't read (#1602)", (tester) async {
    tester.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(reduceMotion: true);
    addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
    final g = await pump(tester);
    expect(g.hasListener, isFalse);
    expect(shifts(tester), everyElement(Offset.zero));
  });

  testWidgets("Android's Remove animations: still, and the sensor isn't read (#1602)", (tester) async {
    tester.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(disableAnimations: true);
    addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
    final g = await pump(tester);
    expect(g.hasListener, isFalse);
  });
}
