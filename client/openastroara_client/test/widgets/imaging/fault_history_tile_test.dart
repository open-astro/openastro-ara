import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
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
  FaultRow at(
    int minute, {
    String id = 'f',
    String device = 'dev-1',
    String? session,
    bool resolved = true,
  }) => FaultRow(
    id: '$id$minute',
    sessionId: session,
    detectedUtc: DateTime.utc(2026, 10, 8, 23).add(Duration(minutes: minute)),
    equipmentType: 'guider',
    equipmentId: device,
    equipmentName: 'Guide cam',
    faultType: 'disconnected',
    details: null,
    actionTaken: 'recovered',
    resolvedUtc: resolved
        ? DateTime.utc(2026, 10, 8, 23, 0, 5).add(Duration(minutes: minute))
        : null,
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
    final byGap = groupFaultRows([
      at(300),
      at(300 - gap),
      at(60),
      at(60 - gap),
    ]);
    expect(byGap.map((g) => g.count), [2, 2]);
    final tooFar = groupFaultRows([at(300), at(300 - gap - 1)]);
    expect(tooFar.map((g) => g.count), [1, 1]);
    final bySession = groupFaultRows([
      at(30, session: 's2'),
      at(25, session: 's1'),
    ]);
    expect(bySession.map((g) => g.count), [1, 1]);
  });

  testWidgets(
    'a collapsed group shows the count and first / last time, rows behind a tap',
    (tester) async {
      final group = groupFaultRows([at(30), at(20), at(10)]).single;
      await tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Scaffold(
            body: SingleChildScrollView(child: FaultGroupTile(group: group)),
          ),
        ),
      );
      expect(find.textContaining('3 times'), findsOneWidget);
      expect(find.textContaining('First '), findsOneWidget);
      expect(find.byType(FaultHistoryTile), findsNothing);
      await tester.tap(find.textContaining('3 times'));
      await tester.pumpAndSettle();
      expect(find.byType(FaultHistoryTile), findsNWidgets(3));
    },
  );

  Widget localized(Widget child) => MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: child),
  );

  testWidgets(
    'a group says in words whether a drop is still open or all recovered',
    (tester) async {
      // Night mode's red filter makes the dot colours look alike.
      await tester.pumpWidget(
        localized(
          Column(
            children: [
              FaultGroupTile(
                group: groupFaultRows([at(40, resolved: false), at(35), at(30)])
                    .single,
              ),
              FaultGroupTile(group: groupFaultRows([at(10), at(5)]).single),
            ],
          ),
        ),
      );
      expect(find.textContaining('1 unresolved'), findsOneWidget);
      expect(find.textContaining('recovered'), findsOneWidget);
    },
  );

  testWidgets(
    'an opened group stays open when a newer row from another device arrives',
    (tester) async {
      // The feed refreshes after each fault event; the list shifts by one row.
      final rows = ValueNotifier([at(30), at(20), at(10)]);
      await tester.pumpWidget(
        localized(
          ValueListenableBuilder<List<FaultRow>>(
            valueListenable: rows,
            builder: (context, list, _) {
              final groups = groupFaultRows(list);
              return ListView.builder(
                itemCount: groups.length,
                itemBuilder: (context, i) => FaultGroupTile(group: groups[i]),
              );
            },
          ),
        ),
      );
      await tester.tap(find.textContaining('3 times'));
      await tester.pumpAndSettle();
      expect(find.byType(FaultHistoryTile), findsNWidgets(3));

      rows.value = [at(40, device: 'dev-2'), ...rows.value];
      await tester.pumpAndSettle();
      // The other device's single row, plus the three rows of the group still open.
      expect(find.byType(FaultHistoryTile), findsNWidgets(4));
    },
  );

  testWidgets('in the narrow iOS alert a long camera name still shows the whole fault type', (tester) async {
    // The test font's glyphs are square, about twice as wide as SF's: 400 px
    // here is as tight as the 270 pt iOS alert on a phone.
    await tester.pumpWidget(MaterialApp(
        home: Scaffold(
            body: Center(
                child: SizedBox(
                    width: 400,
                    child: FaultHistoryTile(
                        row: FaultRow(
                      id: 'f-2',
                      sessionId: null,
                      detectedUtc: DateTime.utc(2026, 10, 8, 23, 10),
                      equipmentType: 'camera',
                      equipmentId: 'dev-2',
                      equipmentName: 'ZWO ASI2600MM Pro',
                      faultType: 'cooler_power_saturated',
                      details: null,
                      actionTaken: null,
                      resolvedUtc: null,
                    )))))));
    await tester.pumpAndSettle();
    final title = find.text('ZWO ASI2600MM Pro — cooler power saturated');
    final paragraph = tester.renderObject<RenderParagraph>(title);
    expect(paragraph.didExceedMaxLines, isFalse, reason: 'the fault type is not cut off');
    expect(tester.getSize(title).height, greaterThan(paragraph.preferredLineHeight * 1.5),
        reason: 'it wraps instead');
  });
}
