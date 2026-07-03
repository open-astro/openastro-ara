import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/screens/library/image_library_screen.dart';
import 'package:openastroara/services/library_api.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/library/live_library_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

class _FakeLibraryClient implements LibraryClient {
  final List<LibrarySession> sessions;
  final Map<String, List<LibraryFrameItem>> frames;
  _FakeLibraryClient({this.sessions = const [], this.frames = const {}});

  @override
  Future<List<LibrarySession>> listSessions({int limit = 200}) async =>
      sessions;

  @override
  Future<List<LibraryFrameItem>> sessionFrames(String sessionId,
          {int limit = 200}) async =>
      frames[sessionId] ?? const [];

  @override
  String thumbnailUrl(String frameId) =>
      'http://test.invalid/api/v1/frames/$frameId/thumbnail';

  String? resumedSessionId;

  @override
  Future<String> resumeTarget(String sessionId) async {
    resumedSessionId = sessionId;
    return 'seq-resume-1';
  }

  (List<String>, int)? rated;
  (List<String>, List<String>, List<String>)? tagged;
  (List<String>, bool)? deleted;

  @override
  Future<void> bulkRate(List<String> frameIds, int rating) async {
    rated = (frameIds, rating);
  }

  @override
  Future<void> bulkTag(List<String> frameIds,
      {List<String> addTags = const [], List<String> removeTags = const []}) async {
    tagged = (frameIds, addTags, removeTags);
  }

  @override
  Future<void> bulkDelete(List<String> frameIds,
      {bool deleteFromDisk = false}) async {
    deleted = (frameIds, deleteFromDisk);
  }

  @override
  void close() {}
}

LibrarySession _session() => LibrarySession(
      id: 'sess-1',
      targetName: 'M42',
      sessionStartUtc: DateTime.utc(2026, 6, 30, 22),
      sessionEndUtc: DateTime.utc(2026, 7, 1, 3),
      totalFrames: 3,
      lightFrames: 2,
      calibrationFrames: 1,
      filtersUsed: const ['Ha', 'OIII'],
    );

LibraryFrameItem _frame(String id, {double exposure = 300, int rating = 0}) =>
    LibraryFrameItem(
      id: id,
      frameType: 'light',
      filterName: 'Ha',
      exposureSeconds: exposure,
      capturedUtc: DateTime.utc(2026, 6, 30, 23),
      hfr: 2.31,
      starCount: 412,
      rating: rating,
    );

Future<void> _pump(WidgetTester tester, _FakeLibraryClient fake) async {
  await tester.pumpWidget(ProviderScope(
    overrides: [libraryApiProvider.overrideWithValue(fake)],
    child: const MaterialApp(home: ImageLibraryScreen()),
  ));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('12f.2: sessions and frame strips render from the live API',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1'), _frame('f2', exposure: 0.5, rating: 4)],
    });
    await _pump(tester, fake);

    expect(find.textContaining('M42'), findsWidgets);
    expect(find.textContaining('2 lights'), findsOneWidget);
    expect(find.textContaining('1 calibration'), findsOneWidget);
    expect(find.textContaining('Ha · OIII'), findsOneWidget);
    // Both frames' thumbnails rendered (filter label overlay).
    expect(find.text('Ha'), findsNWidgets(2));
  });

  testWidgets('the session card opens the §39.5 matching-flats dialog',
      (tester) async {
    await _pump(tester, _FakeLibraryClient(sessions: [_session()]));

    await tester.tap(find.text('Capture Matching Flats'));
    await tester.pumpAndSettle();
    // The shared dialog from the Calibration screen, fed this card's session.
    expect(find.text('Generate & open'), findsOneWidget);
    expect(find.textContaining('Ha, OIII'), findsOneWidget);
  });

  testWidgets('12f.3b: bulk rate flows through the API and clears selection',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1'), _frame('f2')],
    });
    await _pump(tester, fake);

    // Long-press a thumbnail to enter selection mode, then select the second.
    await tester.longPress(find.text('Ha').first);
    await tester.pumpAndSettle();
    expect(find.text('1 selected'), findsOneWidget);
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();
    expect(find.text('2 selected'), findsOneWidget);

    await tester.tap(find.text('Rate'));
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('4 stars'));
    await tester.pumpAndSettle();

    expect(fake.rated, isNotNull);
    expect(fake.rated!.$1.toSet(), {'f1', 'f2'});
    expect(fake.rated!.$2, 4);
    expect(find.text('2 selected'), findsNothing,
        reason: 'selection clears after a successful bulk call');
  });

  testWidgets('12f.3b: bulk delete confirms and sends the disk flag',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    await _pump(tester, fake);

    await tester.longPress(find.text('Ha').first);
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Delete'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Delete'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Delete 1 frame'), findsOneWidget);

    // Default: catalog-only (delete_from_disk false).
    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pumpAndSettle();

    expect(fake.deleted, isNotNull);
    expect(fake.deleted!.$1, ['f1']);
    expect(fake.deleted!.$2, isFalse);
  });

  testWidgets('§40.6: Resume Target lands on the generated sequence in Run',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [_session()]);
    late ProviderContainer container;
    final c = ProviderContainer(overrides: [
      libraryApiProvider.overrideWithValue(fake),
    ]);
    container = c;
    addTearDown(c.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: ImageLibraryScreen()),
    ));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Resume Target'));
    await tester.pumpAndSettle();

    expect(fake.resumedSessionId, 'sess-1');
    expect(container.read(selectedSequenceIdProvider), 'seq-resume-1');
    expect(container.read(selectedTabIndexProvider), 1);
  });

  testWidgets('12f.3: the filter pill narrows frame strips; search hides sessions',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [
        _frame('f1'),
        LibraryFrameItem(
          id: 'f2',
          frameType: 'light',
          filterName: 'OIII',
          exposureSeconds: 180,
          capturedUtc: DateTime.utc(2026, 6, 30, 23, 30),
          hfr: 2.1,
          starCount: 300,
          rating: 5,
        ),
      ],
    });
    await _pump(tester, fake);
    expect(find.text('Ha'), findsOneWidget);
    expect(find.text('OIII'), findsOneWidget);

    // Filter to OIII: the Ha thumbnail disappears from the strip.
    await tester.tap(find.text('All filters'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('OIII').last);
    await tester.pumpAndSettle();
    expect(find.text('Ha'), findsNothing);

    // Search for a target that doesn't exist: the session hides, with a
    // clear-filters escape hatch.
    await tester.tap(find.text('Search'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField), 'NGC 9999');
    await tester.tap(find.widgetWithText(FilledButton, 'Search'));
    await tester.pumpAndSettle();
    expect(find.textContaining('No sessions match'), findsOneWidget);

    await tester.tap(find.text('Clear filters'));
    await tester.pumpAndSettle();
    expect(find.textContaining('M42'), findsWidgets);
  });

  testWidgets('an empty catalog explains itself instead of showing demo data',
      (tester) async {
    await _pump(tester, _FakeLibraryClient());
    expect(find.textContaining('No sessions yet'), findsOneWidget);
    expect(find.textContaining('Orion'), findsNothing,
        reason: 'the 12f.1 demo sessions are gone from this screen');
  });
}
