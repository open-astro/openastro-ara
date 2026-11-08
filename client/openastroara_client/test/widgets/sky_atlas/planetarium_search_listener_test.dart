import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/widgets/sky_atlas/planetarium_search_listener.dart';

void main() {
  /// The sky view appears in the same step as the search: Simple Mode's search hit sets
  /// `_sky` and sends the query together (#1713).
  Future<(ProviderContainer, List<String>)> pump(WidgetTester tester) async {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    final ran = <String>[];
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const SizedBox(),
    ));
    c.read(planetariumSearchProvider.notifier).search('M 31');
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: PlanetariumSearchListener(onSearch: ran.add, child: const SizedBox()),
    ));
    await tester.pump();
    return (c, ran);
  }

  testWidgets('a search sent before the view was listening runs once when it is', (tester) async {
    final (c, ran) = await pump(tester);
    expect(ran, ['M 31']);
    expect(c.read(planetariumSearchProvider), isNull, reason: 'consumed');
    await tester.pump();
    expect(ran, ['M 31'], reason: 'not run again');
  });

  testWidgets('a search sent while it listens runs, and the same text twice runs twice', (tester) async {
    final (c, ran) = await pump(tester);
    c.read(planetariumSearchProvider.notifier).search('M 42');
    await tester.pump();
    c.read(planetariumSearchProvider.notifier).search('M 42');
    await tester.pump();
    expect(ran, ['M 31', 'M 42', 'M 42']);
  });
}
