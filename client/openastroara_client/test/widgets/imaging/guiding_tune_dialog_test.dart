import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/widgets/imaging/guiding_tune_dialog.dart';

import '../../support/fake_section_rig.dart';
import '../../support/phd2_section.dart';
import '../../support/settings_values.dart';

/// No guider status read (no HTTP): the guider is not connected.
class _NoGuider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => null;
}

Future<void> _pump(WidgetTester tester, FakePhd2Section phd2) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
        phd2SettingsProvider.overrideWith(() => phd2),
        guiderStatusProvider.overrideWith(_NoGuider.new),
      ],
      child: const MaterialApp(home: Scaffold(body: GuidingTuneDialog())),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
      'the tune dialog waits for the guider settings from the rig: Not loaded with Retry, and Retry loads them (ADR 0015)',
      (tester) async {
    final phd2 = FakePhd2Section(initial: null); // the rig does not answer the read
    await _pump(tester, phd2);
    expect(find.textContaining('Not loaded'), findsOneWidget);
    expect(find.text('RA minimum move (px)'), findsNothing, reason: 'no default shows');
    phd2.initial = const TestPhd2Settings(minimumMove: 0.37);
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pumpAndSettle();
    expect(find.textContaining('Not loaded'), findsNothing);
    expect(find.text('RA minimum move (px)'), findsOneWidget, reason: 'Retry loads them');
    expect(find.text('0.37'), findsOneWidget, reason: "the rig's value");
    expect(phd2.puts, isEmpty, reason: 'nothing saved');
  });
}
