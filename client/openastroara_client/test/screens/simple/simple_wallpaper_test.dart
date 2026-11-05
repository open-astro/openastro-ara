import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_wallpaper.dart';

void main() {
  Future<StreamController<Offset>> pump(WidgetTester tester, {bool reduceMotion = false}) async {
    final tilt = StreamController<Offset>();
    addTearDown(tilt.close);
    await tester.pumpWidget(MaterialApp(
      home: MediaQuery(
        data: MediaQueryData(size: const Size(1024, 768), disableAnimations: reduceMotion),
        child: SimpleWallpaper(tilt: tilt.stream),
      ),
    ));
    return tilt;
  }

  double shiftOf(WidgetTester tester, int layer) =>
      tester.widget<Transform>(find.byKey(Key('simple-wallpaper-layer-$layer'))).transform.getTranslation().x;

  testWidgets('a tilt shifts the layers by depth, the constellation most; the first reading is centre (#1602)',
      (tester) async {
    final tilt = await pump(tester);
    tilt.add(const Offset(.3, .1));
    await tester.pumpAndSettle();
    expect([for (var i = 0; i < 4; i++) shiftOf(tester, i)], everyElement(0), reason: 'the pose it starts in');
    tilt.add(const Offset(.8, .1));
    await tester.pumpAndSettle();
    final shifts = [for (var i = 0; i < 4; i++) shiftOf(tester, i)];
    expect(shifts.first, lessThan(0), reason: 'away from the tilt');
    for (var i = 1; i < 4; i++) {
      expect(shifts[i], lessThan(shifts[i - 1]), reason: 'layer $i is nearer, so it moves further');
    }
  });

  testWidgets('with Reduce Motion the wallpaper stays still (#1602)', (tester) async {
    final tilt = await pump(tester, reduceMotion: true);
    tilt
      ..add(Offset.zero)
      ..add(const Offset(1, 1));
    await tester.pumpAndSettle();
    expect([for (var i = 0; i < 4; i++) shiftOf(tester, i)], everyElement(0));
  });
}
