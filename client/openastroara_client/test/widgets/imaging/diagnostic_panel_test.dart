import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/diagnostics/diagnostics_state.dart';
import 'package:openastroara/widgets/imaging/diagnostic_panel.dart';
import 'package:openastroara/widgets/status_indicator.dart';

/// Serves a fixed snapshot instead of folding the WS stream.
class _FixedDiagnostics extends DiagnosticsNotifier {
  _FixedDiagnostics(this.snapshot);
  final DiagnosticsSnapshot snapshot;
  @override
  DiagnosticsSnapshot build() => snapshot;
}

/// The Live tab's right rail is 320 px wide (less its 1 px left border).
Future<void> _pumpInRail(WidgetTester tester, DiagnosticsSnapshot snap) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        diagnosticsStateProvider.overrideWith(() => _FixedDiagnostics(snap)),
      ],
      child: const MaterialApp(
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(width: 319, child: DiagnosticPanel()),
          ),
        ),
      ),
    ),
  );
}

void main() {
  testWidgets('a critical issue label plus the event count fits the 320 px '
      'rail: the label ellipsizes instead of overflowing', (tester) async {
    await _pumpInRail(
      tester,
      DiagnosticsSnapshot(
        level: StatusLevel.error,
        label: 'Diagnostics: 1 issue — critical',
        events: [
          DiagnosticEvent(
            timestamp: DateTime(2026, 10, 4, 21),
            level: StatusLevel.error,
            source: 'guider',
            message: 'Star lost',
          ),
        ],
      ),
    );
    expect(
      tester.takeException(),
      isNull,
      reason: 'the header row must not overflow the rail',
    );
    expect(find.text('Diagnostics: 1 issue — critical'), findsOneWidget);
    expect(find.text('1 event'), findsOneWidget);
    final label = tester.widget<Text>(
      find.text('Diagnostics: 1 issue — critical'),
    );
    expect(label.overflow, TextOverflow.ellipsis);
  });

  testWidgets('no "No recent events" filler when the log is empty', (
    tester,
  ) async {
    await _pumpInRail(
      tester,
      const DiagnosticsSnapshot(
        level: StatusLevel.connected,
        label: 'Diagnostics: nominal',
      ),
    );
    expect(tester.takeException(), isNull);
    expect(find.text('Diagnostics: nominal'), findsOneWidget);
    expect(find.text('No recent events'), findsNothing);
    expect(find.textContaining('event'), findsNothing);
  });
}
