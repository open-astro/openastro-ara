import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/app_shell.dart';

/// #1111 PR C — the tab stack's keep-alive and lazy-build contract, pinned
/// without the shell's providers.
class _Probe extends StatefulWidget {
  const _Probe(this.name);
  final String name;
  static final initCounts = <String, int>{};
  @override
  State<_Probe> createState() => _ProbeState();
}

class _ProbeState extends State<_Probe> {
  @override
  void initState() {
    super.initState();
    _Probe.initCounts.update(widget.name, (n) => n + 1, ifAbsent: () => 1);
  }

  @override
  Widget build(BuildContext context) => Text(widget.name);
}

void main() {
  const bodies = <Widget>[_Probe('a'), _Probe('b'), _Probe('c')];

  Widget host({required int selected, required Set<int> visited}) =>
      MaterialApp(
        home: buildTabStack(
          selected: selected,
          visited: visited,
          bodies: bodies,
        ),
      );

  setUp(_Probe.initCounts.clear);

  // IndexedStack keeps non-selected children offstage, so every finder here
  // must look past the default skipOffstage filter.
  Finder probe(String name) => find.byWidgetPredicate(
      (w) => w is _Probe && w.name == name, skipOffstage: false);

  testWidgets('unvisited slots are placeholders, visited ones real bodies',
      (tester) async {
    await tester.pumpWidget(host(selected: 0, visited: {0}));
    expect(find.text('a'), findsOneWidget);
    expect(find.byType(_Probe, skipOffstage: false), findsOneWidget);
    expect(
      find.descendant(
        of: find.byType(IndexedStack),
        matching: find.byType(SizedBox, skipOffstage: false),
        skipOffstage: false,
      ),
      findsNWidgets(2),
    );
    expect(_Probe.initCounts, {'a': 1});
  });

  testWidgets('a tab keeps its State across a switch away and back',
      (tester) async {
    await tester.pumpWidget(host(selected: 0, visited: {0}));
    final a = tester.state(probe('a'));

    await tester.pumpWidget(host(selected: 2, visited: {0, 2}));
    expect(_Probe.initCounts, {'a': 1, 'c': 1});
    expect(tester.state(probe('a')), same(a), reason: 'hidden, not torn down');

    await tester.pumpWidget(host(selected: 0, visited: {0, 2}));
    expect(tester.state(probe('a')), same(a));
    expect(_Probe.initCounts, {'a': 1, 'c': 1},
        reason: 'initState exactly once per tab');
  });

  testWidgets('a selected index outside visited is a placeholder',
      (tester) async {
    // buildTabStack is a pure function of its inputs: it does NOT add the
    // selected index to visited. The caller (AppShell.build) unions them,
    // so this pins where that invariant lives.
    await tester.pumpWidget(host(selected: 1, visited: {0}));
    final stack = tester.widget<IndexedStack>(find.byType(IndexedStack));
    expect(stack.index, 1);
    expect(find.text('b', skipOffstage: false), findsNothing);
    expect(_Probe.initCounts, {'a': 1});

    await tester.pumpWidget(host(selected: 1, visited: {0, 1}));
    expect(find.text('b'), findsOneWidget);
  });
}
