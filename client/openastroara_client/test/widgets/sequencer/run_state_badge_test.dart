import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/sequencer/run_state_badge.dart';

void main() {
  test('state→color mapping is the canonical run palette', () {
    expect(RunStateBadge.colorFor(SequenceRunState.running), AraColors.accentBusy);
    expect(RunStateBadge.colorFor(SequenceRunState.starting), AraColors.accentBusy);
    expect(RunStateBadge.colorFor(SequenceRunState.paused), AraColors.accentInfo);
    expect(RunStateBadge.colorFor(SequenceRunState.pausedAwaitingUser),
        AraColors.accentError);
    expect(RunStateBadge.colorFor(SequenceRunState.failed), AraColors.accentError);
    expect(RunStateBadge.colorFor(SequenceRunState.aborting), AraColors.accentError);
    expect(RunStateBadge.colorFor(SequenceRunState.completed),
        AraColors.textSecondary);
    expect(RunStateBadge.colorFor(SequenceRunState.stopped),
        AraColors.textSecondary);
  });

  test('needs-attention gets product copy, not the enum name', () {
    expect(RunStateBadge.labelFor(SequenceRunState.pausedAwaitingUser),
        'needs attention');
    expect(RunStateBadge.labelFor(SequenceRunState.running), 'running');
  });

  testWidgets('renders nothing for idle/null', (tester) async {
    await tester.pumpWidget(const MaterialApp(
        home: Row(children: [RunStateBadge(null), RunStateBadge(SequenceRunState.idle)])));
    expect(find.byType(Container), findsNothing);
  });
}
