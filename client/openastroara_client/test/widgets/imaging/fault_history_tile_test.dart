import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_test/flutter_test.dart';
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
