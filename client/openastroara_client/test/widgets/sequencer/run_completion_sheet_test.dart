import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/library/live_library_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/sequencer/run_completion_sheet.dart';

class _NoSessions extends LiveLibrarySessionsNotifier {
  @override
  Future<List<LibrarySession>?> build() async => const [];
}

Future<void> _pump(WidgetTester tester, SequenceRunStateInfo run) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [liveLibrarySessionsProvider.overrideWith(_NoSessions.new)],
      child: MaterialApp(
        home: Scaffold(body: RunCompletionSheet(run: run)),
      ),
    ),
  );
  await tester.pump();
}

Icon _headIcon(WidgetTester tester) =>
    tester.widget<Icon>(find.byType(Icon).first);

void main() {
  testWidgets('#1596 a run that imaged nothing is amber, not green', (
    tester,
  ) async {
    await _pump(
      tester,
      const SequenceRunStateInfo(
        sequenceId: 'seq-1',
        state: SequenceRunState.completed,
        reasonCode: 'target_skipped',
        reasonText: 'Skipped, nothing was imaged: M31 did not run',
      ),
    );
    expect(find.text('Run skipped'), findsOneWidget);
    expect(_headIcon(tester).color, AraColors.accentBusy);
    expect(_headIcon(tester).icon, Icons.warning_amber_rounded);
  });

  testWidgets('a run that completed normally stays green', (tester) async {
    await _pump(
      tester,
      const SequenceRunStateInfo(
        sequenceId: 'seq-1',
        state: SequenceRunState.completed,
      ),
    );
    expect(find.text('Run complete'), findsOneWidget);
    expect(_headIcon(tester).color, AraColors.accentConnected);
  });
}
