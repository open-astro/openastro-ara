import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/sequencer/run_state_badge.dart';

void main() {
  test('state→color mapping is the canonical run palette', () {
    expect(RunStateBadge.colorFor(SequenceRunState.running), AraColors.accentConnected); // working: green (DESIGN.md status rule)
    expect(RunStateBadge.colorFor(SequenceRunState.starting), AraColors.accentInfo); // on its way: blue
    expect(RunStateBadge.colorFor(SequenceRunState.paused), AraColors.accentInfo);
    // DESIGN.md status rule: a plan paused waiting for you needs a look, nothing
    // failed: amber, as Simple Mode's plan pill.
    expect(RunStateBadge.colorFor(SequenceRunState.pausedAwaitingUser),
        AraColors.accentBusy);
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
