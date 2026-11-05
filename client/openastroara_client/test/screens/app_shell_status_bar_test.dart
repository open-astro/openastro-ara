import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderFlex;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/backup/backup_stream_state.dart';
import 'package:openastroara/widgets/backup_stream_chip.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';
import 'package:openastroara/widgets/ws_connection_indicator.dart';

class _StartOnSequence extends SelectedTabIndexNotifier {
  @override
  int build() => 2;
}

class _FixedBackup extends BackupStreamController {
  @override
  BackupStreamState build() =>
      const BackupStreamState(enabled: true, active: true);
}

const _launchers = [
  'Image Library',
  'Calibration',
  'Stats',
  'Launchpad',
  'Run profile wizard',
  'Notifications',
  'Help & report a bug',
];

/// The test font is about twice as wide as the real one, so 0.5 matches the
/// real glyph width (the board's report at 1100 pt). The status bar is the
/// only thing asserted; tab bodies may still overflow at narrow widths.
Future<List<String>> _pumpAt(
  WidgetTester tester,
  double width, {
  double scale = 0.5,
}) async {
  tester.view.physicalSize = Size(width, 800);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  final barOverflows = <String>[];
  final onError = FlutterError.onError;
  FlutterError.onError = (details) {
    final text = details.toString();
    if (text.contains('overflowed')) {
      if (text.contains('_BottomStatusBar')) barOverflows.add(text);
    } else {
      onError?.call(details);
    }
  };
  addTearDown(() => FlutterError.onError = onError);
  final container = ProviderContainer(
    overrides: [
      selectedTabIndexProvider.overrideWith(_StartOnSequence.new),
      backupStreamProvider.overrideWith(_FixedBackup.new),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: AppShell()),
    ),
  );
  await tester.pump();
  return barOverflows;
}

void main() {
  for (final width in [1100.0, 1024.0, 768.0]) {
    testWidgets(
      'status bar at ${width.toInt()} pt: every entry visible and reachable',
      (tester) async {
        final overflows = await _pumpAt(tester, width);
        final bar = find
            .ancestor(
              of: find.byType(WsConnectionIndicator),
              matching: find.byType(Container),
            )
            .first;
        final barRect = tester.getRect(bar);
        final leftEdge = tester.getRect(find.byType(BackupStreamChip)).right;

        expect(find.byType(WsConnectionIndicator), findsOneWidget);
        expect(find.byType(EmergencyStopButton), findsOneWidget);
        expect(
          find.text('Backup: up to date'),
          findsOneWidget,
          reason: 'Backup chip stays',
        );
        expect(
          tester.getRect(find.text('Backup: up to date')).right,
          lessThanOrEqualTo(barRect.right),
        );
        expect(overflows, isEmpty, reason: 'no RenderFlex overflow in the bar');

        Finder entry(String name) => find
            .descendant(
              of: bar,
              matching: find.byWidgetPredicate(
                (w) =>
                    w is Text && w.data == name ||
                    w is Tooltip && w.message == name,
              ),
            )
            .hitTestable();
        final hidden = <String>[];
        for (final name in _launchers) {
          final f = entry(name);
          final inBar =
              f.evaluate().isNotEmpty &&
              tester.getRect(f.first).left >= leftEdge &&
              tester.getRect(f.first).right <= barRect.right;
          if (!inBar) hidden.add(name);
        }
        if (hidden.isNotEmpty) {
          final more = find.byTooltip('More');
          expect(
            more,
            findsOneWidget,
            reason: 'cut off with no More menu: $hidden',
          );
          await tester.tap(more);
          await tester.pumpAndSettle();
          for (final name in hidden) {
            expect(
              find.descendant(
                of: find.byType(PopupMenuItem<String>),
                matching: find.text(name),
              ),
              findsOneWidget,
              reason: name,
            );
          }
        }
      },
    );
  }

  testWidgets(
    'status bar at 1470 pt keeps the five labelled launchers and the two icon buttons',
    (tester) async {
      await _pumpAt(tester, 1470);
      for (final label in [
        'Image Library',
        'Calibration',
        'Stats',
        'Launchpad',
        'Run profile wizard',
      ]) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.byTooltip('Notifications'), findsOneWidget);
      expect(find.byTooltip('Help & report a bug'), findsOneWidget);
      expect(find.byTooltip('More'), findsNothing);
    },
  );

  for (final scale in [0.5, 1.0]) {
    testWidgets(
      'launcher row never overflows, 1500 down to 700, scale $scale',
      (tester) async {
        await _pumpAt(tester, 1500, scale: scale);
        final bad = <String>[];
        for (var w = 1500; w >= 700; w--) {
          tester.view.physicalSize = Size(w.toDouble(), 800);
          await tester.pump();
          final row = find
              .ancestor(
                of: find.byTooltip('Help & report a bug'),
                matching: find.byType(Row),
              )
              .first;
          final flex = tester.renderObject<RenderFlex>(row);
          var sum = 0.0;
          flex.visitChildren((c) => sum += (c as RenderBox).size.width);
          // Below 148 px not even More + bell + help fit (the left group took
          // the width), which no launcher form can fix.
          if (flex.constraints.maxWidth >= 148 &&
              sum > flex.constraints.maxWidth + 0.01) {
            bad.add(
              '$w (+${(sum - flex.constraints.maxWidth).toStringAsFixed(1)})',
            );
          }
        }
        expect(bad, isEmpty, reason: 'row wider than its space at: $bad');
      },
    );
  }
}
