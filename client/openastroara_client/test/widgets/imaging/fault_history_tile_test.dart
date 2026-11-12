import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/fault_row.dart';
import 'package:openastroara/widgets/imaging/fault_panel.dart';

FaultRow _row({String? actionTaken, DateTime? resolvedUtc}) => FaultRow(
      id: 'f-1',
      sessionId: null,
      detectedUtc: DateTime.utc(2026, 10, 8, 23, 10),
      equipmentType: 'telescope',
      equipmentId: 'dev-1',
      equipmentName: 'EQM-35 Pro',
      faultType: 'disconnected',
      details: null,
      actionTaken: actionTaken,
      resolvedUtc: resolvedUtc,
    );

Future<void> _pump(WidgetTester tester, FaultRow row) async {
  await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: FaultHistoryTile(row: row))));
  await tester.pumpAndSettle();
}

void main() {
  // #1307 — a row the server resolved because the user disconnected the device
  // mid-recovery is not "recovered"; the tile says what happened.
  testWidgets('a released row reads as disconnected by hand', (tester) async {
    await _pump(
        tester,
        _row(
            actionTaken: 'released:user_disconnect',
            resolvedUtc: DateTime.utc(2026, 10, 8, 23, 13)));
    expect(find.text('released — disconnected by hand'), findsOneWidget);
    expect(find.text('recovered'), findsNothing);
  });

  testWidgets('a recovered row still reads recovered', (tester) async {
    await _pump(
        tester,
        _row(
            actionTaken: 'recovered',
            resolvedUtc: DateTime.utc(2026, 10, 8, 23, 13)));
    expect(find.text('recovered'), findsOneWidget);
  });

  testWidgets('an unresolved row shows its last action', (tester) async {
    await _pump(tester, _row(actionTaken: 'gave_up:abort_and_park'));
    expect(find.text('gave_up:abort_and_park'), findsOneWidget);
  });

  // #1767 — one device dropping again and again reads as one row; every row stays behind it.
  FaultRow at(int minute,
          {String id = 'f', String device = 'dev-1', String? session}) =>
      FaultRow(
        id: '$id$minute',
        sessionId: session,
        detectedUtc: DateTime.utc(2026, 10, 8, 23).add(Duration(minutes: minute)),
        equipmentType: 'guider',
        equipmentId: device,
        equipmentName: 'Guide cam',
        faultType: 'disconnected',
        details: null,
        actionTaken: 'recovered',
        resolvedUtc: DateTime.utc(2026, 10, 8, 23, 0, 5)
            .add(Duration(minutes: minute)),
      );

  test('groupFaultRows folds consecutive rows of one device and keeps others apart', () {
    final groups = groupFaultRows([
      at(30),
      at(20),
      at(10, device: 'dev-2'),
      at(5),
    ]);
    expect(groups.map((g) => g.count), [2, 1, 1]);
    expect(groups.first.rows.map((r) => r.id), ['f30', 'f20']);
  });

  test('two episodes of one device stay apart: a quiet gap or another session splits them', () {
    final gap = faultEpisodeGap.inMinutes;
    final byGap = groupFaultRows([at(300), at(300 - gap), at(60), at(60 - gap)]);
    expect(byGap.map((g) => g.count), [2, 2]);
    final tooFar = groupFaultRows([at(300), at(300 - gap - 1)]);
    expect(tooFar.map((g) => g.count), [1, 1]);
    final bySession = groupFaultRows([at(30, session: 's2'), at(25, session: 's1')]);
    expect(bySession.map((g) => g.count), [1, 1]);
  });

  testWidgets(
      'a collapsed group shows the count and first / last time, rows behind a tap',
      (tester) async {
    final group = groupFaultRows([at(30), at(20), at(10)]).single;
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      locale: const Locale('en'),
      home: Scaffold(
          body: SingleChildScrollView(child: FaultGroupTile(group: group))),
    ));
    expect(find.textContaining('3 times'), findsOneWidget);
    expect(find.textContaining('First '), findsOneWidget);
    expect(find.byType(FaultHistoryTile), findsNothing);
    await tester.tap(find.textContaining('3 times'));
    await tester.pumpAndSettle();
    expect(find.byType(FaultHistoryTile), findsNWidgets(3));
  });
}
