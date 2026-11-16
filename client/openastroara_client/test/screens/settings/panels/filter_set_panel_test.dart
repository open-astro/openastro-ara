import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/filter_set_panel.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/widgets/settings/section_load_state.dart';

import '../../../support/fake_section_rig.dart';

// ADR 0015: the Filter set panel shows fields only for a copy from the rig.

const _ha = PlanningFilter(name: 'Ha 3nm', kind: FilterKind.ha);

Future<ProviderContainer> _pump(
  WidgetTester tester,
  ProfileApi? api, {
  Size size = const Size(1280, 800),
}) async {
  await tester.binding.setSurfaceSize(size);
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      overrides: [profileApiProvider.overrideWithValue(api)],
      child: const MaterialApp(home: Scaffold(body: FilterSetPanel())),
    ),
  );
  await tester.pumpAndSettle();
  return ProviderScope.containerOf(tester.element(find.byType(FilterSetPanel)));
}

void main() {
  testWidgets('Filter set panel: no rig shows not loaded, no fields', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.byType(SectionNotLoaded), findsOneWidget);
    expect(find.text('Add filter'), findsNothing);
  });

  testWidgets('Filter set panel: an offline cache copy with no rig is not '
      'editable', (tester) async {
    final c = await _pump(tester, null);
    c
        .read(filterSetProvider.notifier)
        .seed(const FilterSetSettings(filters: [_ha]));
    await tester.pumpAndSettle();
    expect(find.byType(SectionNotLoaded), findsOneWidget);
    expect(find.text('Add filter'), findsNothing);
    expect(find.text('Ha 3nm'), findsNothing);
  });

  testWidgets('Filter set panel: a copy from the rig shows its filters', (
    tester,
  ) async {
    final rig = FakeSectionRig({
      'filter-set': ProfileApi.filterSetToJson(
        const FilterSetSettings(filters: [_ha]),
      ),
    });
    await _pump(tester, rig.api());
    expect(find.byType(SectionNotLoaded), findsNothing);
    expect(find.text('Add filter'), findsOneWidget);
    expect(find.text('Ha 3nm'), findsOneWidget);
  });

  testWidgets('Filter set panel: a Save the rig refuses as stale shows the '
      'conflict and Reload brings the rig copy back', (tester) async {
    final rig = FakeSectionRig({
      'filter-set': ProfileApi.filterSetToJson(
        const FilterSetSettings(filters: [_ha]),
      ),
    });
    final c = await _pump(tester, rig.api());
    await tester.tap(find.text('Add filter'));
    await tester.pumpAndSettle();
    rig.savedElsewhere('filter-set');
    await tester.runAsync(() async {
      await expectLater(
        c.read(filterSetProvider.notifier).save(rig.api()),
        throwsA(anything),
      );
    });
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsOneWidget);
    expect(rig.puts, isEmpty);
    await tester.tap(find.byKey(const Key('section-reload')));
    await tester.runAsync(() => Future<void>.delayed(Duration.zero));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsNothing);
    expect(c.read(filterSetProvider).value!.filters.map((f) => f.name), [
      'Ha 3nm',
    ]);
  });
}
