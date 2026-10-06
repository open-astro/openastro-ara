import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/storage/sky_photo_cache_row.dart';

void main() {
  Widget host({
    required Future<SkyPhotoCacheInfo> Function() measure,
    required Future<SkyPhotoCacheInfo> Function() clear,
  }) => ProviderScope(
    overrides: [
      skyPhotoCacheMeasureProvider.overrideWithValue(measure),
      skyPhotoCacheClearProvider.overrideWithValue(clear),
    ],
    child: const MaterialApp(home: Scaffold(body: SkyPhotoCacheRow())),
  );

  OutlinedButton button(WidgetTester t) =>
      t.widget<OutlinedButton>(find.byType(OutlinedButton));

  testWidgets('measuring, then empty disables the button', (t) async {
    final gate = Completer<SkyPhotoCacheInfo>();
    await t.pumpWidget(
      host(measure: () => gate.future, clear: () async => (files: 0, bytes: 0)),
    );
    expect(find.text('Measuring…'), findsOneWidget);
    expect(button(t).onPressed, isNull);
    gate.complete((files: 0, bytes: 0));
    await t.pump();
    expect(find.text('Empty'), findsOneWidget);
    expect(button(t).onPressed, isNull);
  });

  testWidgets('a measure failure reads "Size unavailable"', (t) async {
    await t.pumpWidget(
      host(
        measure: () async => throw StateError('no support dir'),
        clear: () async => (files: 0, bytes: 0),
      ),
    );
    await t.pump();
    expect(find.text('Size unavailable'), findsOneWidget);
    expect(button(t).onPressed, isNull);
  });

  testWidgets('size text, Clean runs the clear and re-measures', (t) async {
    var info = (files: 3, bytes: 24 * 1000 * 1000);
    var cleared = 0;
    await t.pumpWidget(
      host(
        measure: () async => info,
        clear: () async {
          cleared++;
          final freed = info;
          info = (files: 0, bytes: 0);
          return freed;
        },
      ),
    );
    await t.pump();
    expect(find.text('24 MB · 3 files'), findsOneWidget);
    expect(button(t).onPressed, isNotNull);
    await t.tap(find.byType(OutlinedButton));
    await t.pump();
    await t.pump();
    expect(cleared, 1);
    expect(
      find.text('Cleaned the sky photo cache — freed 24 MB · 3 files.'),
      findsOneWidget,
    );
    expect(find.text('Empty'), findsOneWidget);
    expect(button(t).onPressed, isNull);
  });
}
