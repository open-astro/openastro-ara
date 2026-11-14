import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/stats/responsive_tile_grid.dart';
import 'package:openastroara/widgets/stats/stat_tile.dart';

void main() {
  testWidgets('tiles with and without a subtitle, or a long value, are one size', (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: Scaffold(
        body: SizedBox(
          width: 400,
          child: ResponsiveTileGrid(children: [
            StatTile(key: Key('a'), icon: Icons.whatshot, value: '12', label: 'Longest streak', subtitle: 'nights'),
            StatTile(key: Key('b'), icon: Icons.gps_fixed, value: '4', label: 'Unique targets'),
            StatTile(key: Key('c'), icon: Icons.flag_outlined, value: '14 Sep 2026 at 23:40', label: 'First light'),
          ]),
        ),
      ),
    ));
    final a = tester.getSize(find.byKey(const Key('a')));
    expect(tester.getSize(find.byKey(const Key('b'))), a);
    expect(tester.getSize(find.byKey(const Key('c'))), a);
  });
}
