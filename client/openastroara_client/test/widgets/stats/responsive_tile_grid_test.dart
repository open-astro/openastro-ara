import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/stats/responsive_tile_grid.dart';

// §50 dashboard responsive tiles — the width math (pure) plus a real layout
// pass proving every tile gets the shared computed width.

void main() {
  const grid = ResponsiveTileGrid(children: []);

  group('tileWidthFor', () {
    test('fills the row exactly: columns of at least minTileWidth', () {
      // 800 px: (800+12) ~/ (180+12) = 4 columns → (800 − 3·12)/4 = 191.
      expect(grid.tileWidthFor(800), 191);
    });

    test('a narrow window degrades to one full-width column', () {
      expect(grid.tileWidthFor(250), 250);
    });

    test('narrower than one minimum tile floors at minTileWidth', () {
      // The parent scrolls/clips; the tile content never squeezes below min.
      expect(grid.tileWidthFor(120), 180);
    });

    test('the cap stops a wide single-column tile ballooning', () {
      // 370 px fits only one 180-min column, whose raw share (370) would
      // dwarf the content — capped to maxTileWidth (280), row left-aligns.
      expect(grid.tileWidthFor(370), 280);
    });

    test('unbounded width falls back to minTileWidth', () {
      expect(grid.tileWidthFor(double.infinity), 180);
    });
  });

  testWidgets('every tile gets the shared computed width', (tester) async {
    tester.view.physicalSize = const Size(800, 600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: ResponsiveTileGrid(
            children: [
              Text('a'),
              Text('b'),
              Text('c'),
              Text('d'),
              Text('e'),
            ],
          ),
        ),
      ),
    );

    final widths = tester
        .widgetList<SizedBox>(find.descendant(
          of: find.byType(Wrap),
          matching: find.byType(SizedBox),
        ))
        .map((s) => s.width)
        .toSet();
    // One shared width for all five tiles, matching the pure math for 800 px.
    expect(widths, {const ResponsiveTileGrid(children: []).tileWidthFor(800)});
  });
}
