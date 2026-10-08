import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/fit_pane.dart';

const _bandKey = Key('band');

/// A 100-tall header over a band that fills, never under [floor].
Widget _pane(double height, {double floor = 50}) => MaterialApp(
      home: Scaffold(
        body: SizedBox(
          width: 400,
          height: height,
          child: FitPane(
            padding: const EdgeInsets.all(10),
            child: FitColumn(children: [
              const SizedBox(height: 100),
              FitFill(
                child: FitBand(
                  builder: (context, width, h) =>
                      SizedBox(key: _bandKey, height: math.max(h, floor)),
                ),
              ),
            ]),
          ),
        ),
      ),
    );

double _maxScroll(WidgetTester t) =>
    t.state<ScrollableState>(find.byType(Scrollable).first).position.maxScrollExtent;

void main() {
  testWidgets('the band takes the height the header leaves, with no scroll', (t) async {
    await t.pumpWidget(_pane(400));
    expect(t.getSize(find.byKey(_bandKey)).height, 400 - 20 - 100);
    expect(t.getSize(find.byKey(_bandKey)).width, 380);
    expect(_maxScroll(t), 0);
  });

  testWidgets('a taller pane grows the band', (t) async {
    t.view.physicalSize = const Size(800, 1200);
    t.view.devicePixelRatio = 1;
    addTearDown(t.view.reset);
    await t.pumpWidget(_pane(900));
    expect(t.getSize(find.byKey(_bandKey)).height, 900 - 20 - 100);
    expect(_maxScroll(t), 0);
  });

  testWidgets('below the floor the pane scrolls instead of overflowing', (t) async {
    await t.pumpWidget(_pane(140));
    expect(t.getSize(find.byKey(_bandKey)).height, 50);
    expect(_maxScroll(t), 100 + 50 + 20 - 140);
    expect(t.takeException(), isNull);
  });

  testWidgets('a FitRow shares the pane height and splits the width by flex', (t) async {
    await t.pumpWidget(_row(500));
    expect(t.getSize(find.byKey(_leftKey)), const Size(190, 500));
    expect(t.getSize(find.byKey(_rightKey)), const Size(190, 460));
    expect(_maxScroll(t), 0);
  });

  testWidgets('a FitRow grows to its tallest column and lines the others up', (t) async {
    await t.pumpWidget(_row(200));
    expect(t.getSize(find.byKey(_leftKey)).height, 300);
    expect(t.getSize(find.byKey(_rightKey)).height, 260, reason: 'stretched to the shared 300');
    expect(_maxScroll(t), 100);
  });

  testWidgets('nested columns pass the height down to the innermost fill', (t) async {
    await t.pumpWidget(MaterialApp(
      home: Scaffold(
        body: SizedBox(
          height: 500,
          child: FitPane(
            child: FitColumn(children: [
              const SizedBox(height: 60),
              FitFill(
                child: Padding(
                  padding: const EdgeInsets.all(20),
                  child: FitColumn(children: [
                    const SizedBox(height: 40),
                    FitFill(
                      child: FitBand(
                        builder: (context, width, h) => SizedBox(key: _bandKey, height: h),
                      ),
                    ),
                    const SizedBox(height: 30),
                  ]),
                ),
              ),
            ]),
          ),
        ),
      ),
    ));
    expect(t.getSize(find.byKey(_bandKey)).height, 500 - 60 - 40 - 40 - 30);
    expect(_maxScroll(t), 0);
  });
}

const _leftKey = Key('left');
const _rightKey = Key('right');

/// A row of a 300-tall column beside a column whose band fills.
Widget _row(double height) => MaterialApp(
      home: Scaffold(
        body: SizedBox(
          width: 400,
          height: height,
          child: FitPane(
            child: FitRow(flex: const [1, 1], spacing: 20, children: [
              const SizedBox(key: _leftKey, height: 300),
              FitColumn(children: [
                const SizedBox(height: 40),
                FitFill(child: FitBand(builder: (context, width, h) => SizedBox(key: _rightKey, height: math.max(h, 60)))),
              ]),
            ]),
          ),
        ),
      ),
    );

