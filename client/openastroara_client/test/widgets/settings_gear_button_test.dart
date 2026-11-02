import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/widgets/settings_gear_button.dart';

void main() {
  test('the left rail has no Settings entry: it highlights nothing while Settings is open', () {
    expect(railIndexFor(kOptionsTabIndex), isNull);
    expect(railIndexFor(kLiveTabIndex), kLiveTabIndex);
    expect(railIndexFor(kPlanningTabIndex), kPlanningTabIndex);
  });

  testWidgets('the top-bar gear opens Settings', (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SettingsGearButton())),
    ));
    expect(find.byTooltip('Settings'), findsOneWidget);
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    expect(container.read(selectedTabIndexProvider), kOptionsTabIndex);
  });
}
