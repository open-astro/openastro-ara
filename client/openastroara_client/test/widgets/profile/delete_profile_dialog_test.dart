import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_twin.dart';
import 'package:openastroara/widgets/profile/delete_profile_dialog.dart';

Future<DeleteProfileChoice?> _open(
  WidgetTester tester, {
  required Future<GuiderTwinPreview?> Function() preview,
  bool isActive = false,
  bool isLast = false,
}) async {
  DeleteProfileChoice? result;
  var opened = false;
  await tester.pumpWidget(MaterialApp(
    home: Scaffold(
      body: Builder(builder: (context) {
        return TextButton(
          onPressed: () async {
            opened = true;
            result = await showDeleteProfileDialog(context,
                name: 'Travel Rig',
                isActive: isActive,
                isLast: isLast,
                preview: preview);
          },
          child: const Text('open'),
        );
      }),
    ),
  ));
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
  expect(opened, isTrue);
  return Future.value(result);
}

const _twin = GuiderTwinPreview(
    twinNames: ['Travel Rig', 'ara-travel-rig-deadbeef'],
    exists: true,
    shared: false,
    canDeleteNow: true);

Checkbox _checkbox(WidgetTester tester) => tester.widget<Checkbox>(
    find.descendant(
        of: find.byKey(const ValueKey('delete-guider-profile')),
        matching: find.byType(Checkbox)));

void main() {
  testWidgets('offers the guider profile, checked by default, and returns the choice',
      (tester) async {
    DeleteProfileChoice? choice;
    // Open, inspect, then confirm.
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(
        body: Builder(builder: (context) {
          return TextButton(
            onPressed: () async {
              choice = await showDeleteProfileDialog(context,
                  name: 'Travel Rig',
                  isActive: true,
                  isLast: false,
                  preview: () async => _twin);
            },
            child: const Text('open'),
          );
        }),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.text('Delete profile?'), findsOneWidget);
    expect(find.textContaining('most recent remaining profile becomes active'),
        findsOneWidget);
    expect(find.textContaining('Also delete its OpenAstro Guider profile "Travel Rig"'),
        findsOneWidget);
    expect(find.textContaining('guide calibration and dark library are deleted too'),
        findsOneWidget);
    expect(_checkbox(tester).value, isTrue);
    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pumpAndSettle();
    expect(choice?.deleteGuiderProfile, isTrue);
  });

  testWidgets('unticking the box deletes the Ara profile only', (tester) async {
    DeleteProfileChoice? choice;
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(
        body: Builder(builder: (context) {
          return TextButton(
            onPressed: () async {
              choice = await showDeleteProfileDialog(context,
                  name: 'Travel Rig',
                  isActive: false,
                  isLast: false,
                  preview: () async => _twin);
            },
            child: const Text('open'),
          );
        }),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('delete-guider-profile')));
    await tester.pumpAndSettle();
    expect(_checkbox(tester).value, isFalse);
    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pumpAndSettle();
    expect(choice?.deleteGuiderProfile, isFalse);
  });

  testWidgets('a shared twin defaults off and names the other profile',
      (tester) async {
    await _open(tester,
        preview: () async => const GuiderTwinPreview(
            twinNames: ['Travel Rig'],
            exists: true,
            shared: true,
            sharedWith: 'Travel Rig',
            canDeleteNow: true));
    expect(_checkbox(tester).value, isFalse);
    expect(
        find.textContaining(
            'Another Ara profile, "Travel Rig", uses this guider profile'),
        findsOneWidget);
  });

  testWidgets('no twin on the guider: the box is hidden', (tester) async {
    await _open(tester,
        preview: () async => const GuiderTwinPreview(
            twinNames: ['Travel Rig'],
            exists: false,
            shared: false,
            canDeleteNow: false));
    expect(find.byKey(const ValueKey('delete-guider-profile')), findsNothing);
    expect(find.textContaining('OpenAstro Guider'), findsNothing);
  });

  testWidgets('no guider connected: the box stays, with the next-connect note',
      (tester) async {
    await _open(tester,
        preview: () async => const GuiderTwinPreview(
            twinNames: ['Travel Rig'],
            exists: null,
            shared: false,
            canDeleteNow: false,
            reason: 'No guider is connected.'));
    expect(_checkbox(tester).value, isTrue);
    expect(find.textContaining('deleted the next time the guider connects'),
        findsOneWidget);
  });

  testWidgets('a busy guider: the box stays, with the daemon\'s reason',
      (tester) async {
    await _open(tester,
        preview: () async => const GuiderTwinPreview(
            twinNames: ['Travel Rig'],
            exists: true,
            shared: false,
            canDeleteNow: false,
            reason: 'The guider is guiding. It will be deleted once guiding stops.'));
    expect(_checkbox(tester).value, isTrue);
    expect(find.textContaining('It will be deleted once guiding stops'),
        findsOneWidget);
  });

  testWidgets('no preview at all (old daemon): nothing about the guider',
      (tester) async {
    await _open(tester, preview: () async => null);
    expect(find.byKey(const ValueKey('delete-guider-profile')), findsNothing);
    expect(find.text('Checking the guider…'), findsNothing);
  });

  test('the outcome lines', () {
    expect(const ProfileDeleteResult(guiderProfile: GuiderTwinOutcome.deleted).message,
        'Guider profile deleted');
    expect(const ProfileDeleteResult(guiderProfile: GuiderTwinOutcome.queued).message,
        contains('when the guider is free'));
    expect(const ProfileDeleteResult(guiderProfile: GuiderTwinOutcome.kept).message,
        isNull);
    expect(
        const ProfileDeleteResult(
                guiderProfile: GuiderTwinOutcome.failed, reason: 'locked')
            .message,
        "Couldn't delete the guider profile: locked");
    expect(ProfileDeleteResult.fromJson({'guider_profile': 'not_found'}).guiderProfile,
        GuiderTwinOutcome.notFound);
  });
}
