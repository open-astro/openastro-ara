import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/adaptive_refresh.dart';

Future<int> _pull(WidgetTester tester, TargetPlatform platform, {bool empty = false}) async {
  var calls = 0;
  await tester.pumpWidget(MaterialApp(
    theme: ThemeData(platform: platform),
    home: Scaffold(
      body: AdaptiveRefresh(
        onRefresh: () async => calls++,
        slivers: [if (!empty) SliverList.list(children: const [Text('row')])],
      ),
    ),
  ));
  await tester.fling(find.byType(CustomScrollView), const Offset(0, 400), 1000);
  await tester.pumpAndSettle();
  return calls;
}

void main() {
  testWidgets('iOS and iPadOS pull with the Cupertino sliver', (tester) async {
    expect(await _pull(tester, TargetPlatform.iOS), 1);
    expect(find.byType(CupertinoSliverRefreshControl, skipOffstage: false), findsOneWidget);
    expect(find.byType(RefreshIndicator), findsNothing);
  });

  testWidgets('other platforms pull with the Material indicator', (tester) async {
    for (final p in [TargetPlatform.android]) {
      expect(await _pull(tester, p), 1, reason: '$p');
      expect(find.byType(RefreshIndicator), findsOneWidget);
      expect(find.byType(CupertinoSliverRefreshControl, skipOffstage: false), findsNothing);
    }
  });

  for (final p in [TargetPlatform.iOS, TargetPlatform.android]) {
    testWidgets('an empty list still pulls on $p', (tester) async {
      expect(await _pull(tester, p, empty: true), 1);
    });
  }
}
