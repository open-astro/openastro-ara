import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/server_internal_state.dart';
import 'package:openastroara/models/stats/usage_history.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/screens/simple/simple_usage_graph.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/services/usage_history_api.dart';
import 'package:openastroara/state/night_mode_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/stats/server_internal_state_state.dart';
import 'package:openastroara/state/stats/usage_history_state.dart';

const _gib = 1073741824;

class _Internal extends ServerInternalStateNotifier {
  @override
  Future<ServerInternalState?> build() async => const ServerInternalState(
    uptimeSeconds: 11520,
    cpuPercent: 23,
    workingSetBytes: 536870912,
  );
  @override
  Future<void> refresh() async {}
}

class _Run extends SequenceRunStateNotifier {
  _Run(this.startedUtc);
  final DateTime? startedUtc;
  @override
  Future<SequenceRunStateInfo?> build() async => startedUtc == null
      ? null
      : SequenceRunStateInfo(sequenceId: 'r', startedUtc: startedUtc);
}

class _Night extends NightModeController {
  @override
  Future<bool> build() async => true;
}

class _Fake implements UsageHistoryClient {
  _Fake(this.history);
  final UsageHistory? history;
  var calls = 0;
  @override
  Future<UsageHistory?> fetch({DateTime? since}) async {
    calls++;
    return history;
  }

  @override
  void close() {}
}

/// 4 samples a minute for [minutes] minutes ending now; [free] shapes the storage series.
UsageHistory _history({
  int minutes = 240,
  double Function(double t)? free,
  double Function(double t)? cpu,
  bool cpuUnread = false,
}) {
  final now = DateTime.now().toUtc();
  final n = minutes * 4;
  return UsageHistory(
    intervalSeconds: 15,
    processorCount: 4,
    samples: [
      for (var i = 0; i < n; i++)
        () {
          final t = i / (n - 1);
          return UsageSample(
            at: now.subtract(Duration(seconds: (n - 1 - i) * 15)),
            storageFreeBytes: ((free?.call(t) ?? 640 - t * 52) * _gib).round(),
            storageTotalBytes: 931 * _gib,
            cpuPercent: cpuUnread ? null : cpu?.call(t) ?? 30,
            memoryBytes: ((1.35 + t * 0.34) * _gib).round(),
          );
        }(),
    ],
  );
}

Future<_Fake> _pump(
  WidgetTester tester, {
  UsageHistory? history,
  bool expand = true,
  DateTime? runStart,
  bool night = false,
  double textScale = 1,
  Size size = const Size(520, 2400),
}) async {
  final fake = _Fake(history);
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'test', port: 1),
        ),
        usageHistoryApiFactoryProvider.overrideWithValue((_) => fake),
        serverInternalStateProvider.overrideWith(_Internal.new),
        storageSpaceProvider.overrideWith(
          (ref) async => const StorageSpace(
            saveDirectory: '/d',
            isFallback: false,
            freeBytes: 412 * _gib,
          ),
        ),
        sequenceRunStateProvider.overrideWith(() => _Run(runStart)),
        nightModeProvider.overrideWith(
          night ? _Night.new : () => NightModeController(),
        ),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: MediaQuery(
            data: MediaQueryData(
              size: size,
              textScaler: TextScaler.linear(textScale),
            ),
            child: SingleChildScrollView(
              child: SimpleSystemPanel(expand: expand),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return fake;
}

void main() {
  group('pure helpers', () {
    final l10n = lookupAppLocalizations(const Locale('en'));
    final t0 = DateTime.utc(2026, 11, 13, 20);

    test(
      'the run is usable only when it began inside the 12 h the rig keeps',
      () {
        expect(
          usageRunStart(t0.subtract(const Duration(hours: 3)), t0),
          isNotNull,
        );
        expect(
          usageRunStart(t0.subtract(const Duration(hours: 13)), t0),
          isNull,
        );
        expect(usageRunStart(null, t0), isNull);
      },
    );

    test('the window cuts to the hour or to the run start', () {
      final all = [
        for (var m = 0; m <= 240; m++)
          UsageSample(at: t0.subtract(Duration(minutes: 240 - m))),
      ];
      final hour = usageWindowSamples(all, now: t0, window: UsageWindow.hour);
      expect(hour.first.at, t0.subtract(const Duration(hours: 1)));
      final run = usageWindowSamples(
        all,
        now: t0,
        window: UsageWindow.run,
        runStart: t0.subtract(const Duration(hours: 3)),
      );
      expect(run.first.at, t0.subtract(const Duration(hours: 3)));
      expect(run.last.at, t0);
    });

    test('trend in words: falling storage, steady memory, rising CPU, too few readings', () {
      List<(DateTime, double)> pts(double a, double b) => [
        (t0, a),
        (t0.add(const Duration(minutes: 30)), (a + b) / 2),
        (t0.add(const Duration(hours: 1)), b),
      ];
      expect(
        usageTrend(l10n, UsageKind.storage, pts(640, 624)),
        'Falling about 16 GB per hour',
      );
      expect(usageTrend(l10n, UsageKind.storage, pts(640, 640.4)), 'Steady');
      expect(
        usageTrend(l10n, UsageKind.memory, pts(1.3, 1.6)),
        'Rising about 0.30 GB per hour',
      );
      expect(
        usageTrend(l10n, UsageKind.cpu, pts(20, 60)),
        'Rising 40 points over the window',
      );
      expect(usageTrend(l10n, UsageKind.cpu, pts(20, 22)), 'Steady');
      expect(
        usageTrend(l10n, UsageKind.storage, pts(1, 2).take(2).toList()),
        'Collecting…',
      );
    });

    test('scales: CPU 0-100, memory 0-4 GB, storage scaled to its window', () {
      expect(usageRange(UsageKind.cpu, [3]), (0.0, 100.0));
      expect(usageRange(UsageKind.memory, [1.2]), (0.0, 4.0));
      expect(usageRange(UsageKind.storage, [600, 640]), (596.0, 644.0));
    });

    test('warning levels: storage under 10 % free, CPU over 85 %, memory over 3.4 GB', () {
      expect(usageWarns(UsageKind.storage, 90, totalGb: 931), isTrue);
      expect(usageWarns(UsageKind.storage, 94, totalGb: 931), isFalse);
      expect(usageWarns(UsageKind.cpu, 86), isTrue);
      expect(usageWarns(UsageKind.cpu, 85), isFalse);
      expect(usageWarns(UsageKind.memory, 3.5), isTrue);
      expect(usageWarns(UsageKind.memory, 3.4), isFalse);
    });

    test('an unread sample is null for every series, never 0', () {
      final s = UsageSample(at: t0);
      for (final k in UsageKind.values) {
        expect(usageValue(k, s), isNull);
      }
    });

    test('Night draws a 3 px line and a 30 % fill; day 2 px and 16 %', () {
      expect(usageLineWidth(true), 3);
      expect(usageFillAlpha(true), 0.30);
      expect(usageLineWidth(false), 2);
      expect(usageFillAlpha(false), 0.16);
    });
  });

  group('Settings › Rig panel', () {
    testWidgets(
      'expand mode draws Storage, CPU and Memory as graphs and keeps Uptime a figure',
      (tester) async {
        await _pump(tester, history: _history());
        for (final k in ['storage', 'cpu', 'memory']) {
          expect(find.byKey(Key('usage-graph-$k')), findsOneWidget, reason: k);
        }
        expect(find.text('Up'), findsOneWidget);
        expect(find.text('3 h 12 min'), findsOneWidget);
        expect(
          find.text('412 GB free'),
          findsNothing,
          reason: 'the Storage tile is replaced by its graph',
        );
        expect(find.text('Last hour'), findsOneWidget);
      },
    );

    testWidgets(
      'the compact tiles over the frame stay figures and never ask for history',
      (tester) async {
        final fake = await _pump(tester, history: _history(), expand: false);
        expect(find.byKey(const Key('simple-usage-graphs')), findsNothing);
        for (final t in ['412 GB free', '23 %', '512 MB', '3 h 12 min']) {
          expect(find.text(t), findsOneWidget, reason: t);
        }
        expect(fake.calls, 0);
      },
    );

    testWidgets('a rig without the route (404) keeps today\'s four figures', (
      tester,
    ) async {
      await _pump(tester, history: null);
      expect(find.byKey(const Key('simple-usage-graphs')), findsNothing);
      for (final t in ['412 GB free', '23 %', '512 MB', '3 h 12 min']) {
        expect(find.text(t), findsOneWidget, reason: t);
      }
      expect(tester.takeException(), isNull);
    });

    testWidgets(
      'Run so far is disabled with its reason when no run started in the last 12 h',
      (tester) async {
        await _pump(tester, history: _history());
        expect(find.byKey(const Key('usage-run-unavailable')), findsOneWidget);
        expect(
          find.text('No run started in the last 12 hours.'),
          findsOneWidget,
        );
        expect(
          tester
              .widget<InkWell>(find.byKey(const Key('usage-window-run')))
              .onTap,
          isNull,
        );
        await _pump(
          tester,
          history: _history(),
          runStart: DateTime.now().subtract(const Duration(hours: 20)),
        );
        expect(
          find.byKey(const Key('usage-run-unavailable')),
          findsOneWidget,
          reason: 'a run older than the 12 h kept is not drawn',
        );
      },
    );

    testWidgets('Run so far draws from the run start and the axis says so', (
      tester,
    ) async {
      await _pump(
        tester,
        history: _history(),
        runStart: DateTime.now().subtract(const Duration(hours: 3)),
      );
      expect(find.byKey(const Key('usage-run-unavailable')), findsNothing);
      expect(find.text('60 min ago'), findsNWidgets(3));
      await tester.tap(find.byKey(const Key('usage-window-run')));
      await tester.pump();
      expect(find.text('3 h ago'), findsNWidgets(3));
      expect(find.text('60 min ago'), findsNothing);
    });

    testWidgets(
      'Show table lists readings with their age and Hide table closes it',
      (tester) async {
        await _pump(tester, history: _history());
        expect(find.byKey(const Key('usage-table-body-storage')), findsNothing);
        await tester.tap(find.byKey(const Key('usage-table-storage')));
        await tester.pump();
        final table = find.byKey(const Key('usage-table-body-storage'));
        expect(table, findsOneWidget);
        expect(
          find.descendant(of: table, matching: find.text('When')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: table, matching: find.text('Now')),
          findsOneWidget,
        );
        expect(find.text('Hide table'), findsOneWidget);
        await tester.tap(find.byKey(const Key('usage-table-storage')));
        await tester.pump();
        expect(table, findsNothing);
      },
    );

    testWidgets(
      'the figure, the trend in words and a spoken alternative with both',
      (tester) async {
        await _pump(tester, history: _history());
        expect(find.text('588 GB free of 931 GB'), findsOneWidget);
        expect(find.text('Falling about 13 GB per hour'), findsOneWidget);
        final label = tester
            .getSemantics(find.byKey(const Key('usage-graph-storage')))
            .label;
        expect(label, contains('Storage: 588 GB free of 931 GB'));
        expect(
          label,
          contains('Trend over the last hour: Falling about 13 GB per hour'),
        );
        expect(find.text('30 % of all 4 cores'), findsOneWidget);
      },
    );

    testWidgets(
      'a nearly full disk shows a triangle with words and the dashed-line key',
      (tester) async {
        await _pump(tester, history: _history(free: (t) => 120 - t * 40));
        expect(find.byKey(const Key('usage-warn-storage')), findsOneWidget);
        expect(
          find.text('Under 10 % free. Free some space or choose another disk.'),
          findsOneWidget,
        );
        expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);
        expect(find.text('dashed: warning level'), findsWidgets);
        expect(find.byKey(const Key('usage-warn-cpu')), findsNothing);
      },
    );

    testWidgets('a series the rig could not read shows a dash, not 0 %', (
      tester,
    ) async {
      await _pump(tester, history: _history(cpuUnread: true));
      expect(
        find.byKey(const Key('usage-big-cpu')).evaluate().isNotEmpty,
        isTrue,
      );
      expect(find.text('—'), findsOneWidget);
      expect(find.text('0 % of all 4 cores'), findsNothing);
    });

    testWidgets('an empty history says why the graph is short', (tester) async {
      await _pump(
        tester,
        history: const UsageHistory(
          intervalSeconds: 15,
          processorCount: 4,
          samples: [],
        ),
      );
      expect(find.byKey(const Key('usage-empty')), findsOneWidget);
      expect(find.text('Collecting…'), findsWidgets);
    });

    testWidgets(
      'arrow keys move the crosshair and the tooltip reads the point; Escape clears it',
      (tester) async {
        await _pump(tester, history: _history());
        final graph = find.byKey(const Key('usage-graph-storage'));
        await tester.tapAt(tester.getTopLeft(graph) + const Offset(2, 2));
        await tester.pump();
        expect(find.byKey(const Key('usage-tip-storage')), findsOneWidget);
        await tester.sendKeyEvent(LogicalKeyboardKey.escape);
        await tester.pump();
        expect(find.byKey(const Key('usage-tip-storage')), findsNothing);
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
        await tester.pump();
        expect(find.byKey(const Key('usage-tip-storage')), findsOneWidget);
        expect(
          tester.widget<Text>(find.byKey(const Key('usage-tip-storage'))).data,
          startsWith('1 min ago: '),
        );
      },
    );

    testWidgets('Night renders the graphs too', (tester) async {
      await _pump(tester, history: _history(), night: true);
      expect(find.byKey(const Key('usage-graph-cpu')), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('at 200 % text nothing clips and the controls stay 44 px', (
      tester,
    ) async {
      await _pump(
        tester,
        history: _history(),
        runStart: DateTime.now().subtract(const Duration(hours: 3)),
        textScale: 2,
        size: const Size(420, 4000),
      );
      expect(tester.takeException(), isNull);
      for (final k in [
        'usage-window-hour',
        'usage-window-run',
        'usage-table-storage',
      ]) {
        expect(
          tester.getSize(find.byKey(Key(k))).height,
          greaterThanOrEqualTo(44),
          reason: k,
        );
      }
    });
  });
}
