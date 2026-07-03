import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/screens/library/image_library_screen.dart';
import 'package:openastroara/services/library_api.dart';
import 'package:openastroara/widgets/library/bulk_action_bar.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/library/live_library_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

class _FakeLibraryClient implements LibraryClient {
  final List<LibrarySession> sessions;
  final Map<String, List<LibraryFrameItem>> frames;
  _FakeLibraryClient({this.sessions = const [], this.frames = const {}});

  // When set, the first page reports has_more with this cursor and the second
  // page (any cursor) returns [moreSessions].
  List<LibrarySession> moreSessions = const [];
  bool pageTwoExists = false;

  @override
  Future<CursorPage<LibrarySession>> listSessions(
      {int limit = 200, String? cursor}) async {
    if (cursor != null) {
      return CursorPage(items: moreSessions, nextCursor: null, hasMore: false);
    }
    return CursorPage(
        items: sessions,
        nextCursor: pageTwoExists ? 'page-2' : null,
        hasMore: pageTwoExists);
  }

  @override
  Future<List<LibraryFrameItem>> sessionFrames(String sessionId,
          {int limit = 200}) async =>
      frames[sessionId] ?? const [];

  @override
  String thumbnailUrl(String frameId) =>
      'http://test.invalid/api/v1/frames/$frameId/thumbnail';

  String? resumedSessionId;
  (String, String)? previewRequest;

  @override
  Future<LibraryFrameDetail> frameDetail(String frameId) async =>
      LibraryFrameDetail(
        id: frameId,
        gain: 100,
        offset: 10,
        temperatureC: -9.8,
        focuserPosition: 5230,
        width: 4144,
        height: 2822,
        tags: const ['keeper'],
      );

  (double?, double?, double?)? previewKnobs;

  @override
  Future<List<int>> fetchPreview(String frameId,
      {required String stretch,
      int maxDimensionPx = 2048,
      double? blackPoint,
      double? midtonePoint,
      double? whitePoint}) async {
    previewRequest = (frameId, stretch);
    previewKnobs = (blackPoint, midtonePoint, whitePoint);
    // A 1x1 transparent PNG so Image.memory can decode it in tests.
    return const [
      0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
      0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
      0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
      0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
      0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
      0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];
  }

  @override
  Future<String> resumeTarget(String sessionId) async {
    resumedSessionId = sessionId;
    return 'seq-resume-1';
  }

  (List<String>, int)? rated;
  (List<String>, List<String>, List<String>)? tagged;
  (List<String>, bool)? deleted;
  (List<String>, String)? moved;
  List<String>? exported;

  @override
  Future<(List<int>, String)> exportFrames(List<String> frameIds) async {
    exported = frameIds;
    return ([1, 2, 3], 'openastroara-frames-test.tar');
  }

  @override
  Future<void> bulkMove(List<String> frameIds, String targetSessionId) async {
    moved = (frameIds, targetSessionId);
  }

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

  testWidgets('§65: the viewer fetches a stretched preview and repaints on palette change',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    await _pump(tester, fake);

    // Open the viewer from the strip.
    await tester.tap(find.text('Ha'));
    await tester.pumpAndSettle();
    expect(fake.previewRequest, isNotNull);
    expect(fake.previewRequest!.$1, 'f1');
    expect(fake.previewRequest!.$2, 'auto_stf', reason: 'default palette');

    // Switch palettes: a fresh server render is requested.
    await tester.tap(find.text('auto_stf'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('asinh').last);
    await tester.pumpAndSettle();
    expect(fake.previewRequest!.$2, 'asinh');
  });

  testWidgets('§40.5: the viewer star row rates the single frame optimistically',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    await _pump(tester, fake);

    await tester.tap(find.text('Ha'));
    await tester.pumpAndSettle();

    // Four filled stars after tapping the fourth star.
    await tester.tap(find.byIcon(Icons.star_border).at(3));
    await tester.pumpAndSettle();
    expect(fake.rated, isNotNull);
    expect(fake.rated!.$1, ['f1']);
    expect(fake.rated!.$2, 4);
    expect(find.byIcon(Icons.star), findsNWidgets(4));

    // Tapping the current rating clears it (0).
    await tester.tap(find.byIcon(Icons.star).at(3));
    await tester.pumpAndSettle();
    expect(fake.rated!.$2, 0);
    expect(find.byIcon(Icons.star), findsNothing);
  });

  testWidgets('§40.5: the viewer shows detail metadata and edits tags',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    await _pump(tester, fake);
    await tester.tap(find.text('Ha'));
    await tester.pumpAndSettle();

    // Detail-backed metadata rows render.
    expect(find.textContaining('Gain: 100'), findsOneWidget);
    expect(find.textContaining('Sensor: -9.8°C'), findsOneWidget);
    expect(find.text('keeper'), findsOneWidget);

    // Add a tag through the + chip.
    await tester.tap(find.text('tag'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField).last, 'lucky');
    await tester.tap(find.widgetWithText(FilledButton, 'Add'));
    await tester.pumpAndSettle();
    expect(fake.tagged, isNotNull);
    expect(fake.tagged!.$1, ['f1']);
    expect(fake.tagged!.$2, ['lucky']);
    expect(find.text('lucky'), findsOneWidget, reason: 'optimistic chip');

    // Delete the original tag via its chip.
    // Two chips now (keeper, lucky) — keeper renders first.
    await tester.tap(find.byTooltip('Delete').first);
    await tester.pumpAndSettle();
    expect(fake.tagged!.$3, ['keeper']);
    expect(find.text('keeper'), findsNothing);
  });

  testWidgets('cursor paging: Load more appends the next server page',
      (tester) async {
    final more = LibrarySession(
      id: 'sess-2',
      targetName: 'NGC 6888',
      sessionStartUtc: DateTime.utc(2026, 6, 20, 22),
      sessionEndUtc: null,
      totalFrames: 1,
      lightFrames: 1,
      calibrationFrames: 0,
      filtersUsed: const ['Ha'],
    );
    final fake = _FakeLibraryClient(sessions: [_session()])
      ..pageTwoExists = true
      ..moreSessions = [more];
    await _pump(tester, fake);

    expect(find.textContaining('M42'), findsWidgets);
    expect(find.text('Load more sessions'), findsOneWidget);

    await tester.tap(find.text('Load more sessions'));
    await tester.pumpAndSettle();

    expect(find.textContaining('NGC 6888'), findsWidgets,
        reason: 'the second page appends');
    expect(find.text('Load more sessions'), findsNothing,
        reason: 'no further pages');
  });

  testWidgets('12f.3b: bulk move picks a session and posts the reassignment',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    await _pump(tester, fake);

    await tester.longPress(find.text('Ha').first);
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Move to session'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Move to session'));
    await tester.pumpAndSettle();

    // The picker lists the loaded sessions; choose M42's.
    await tester.tap(find.textContaining('M42').last);
    await tester.pumpAndSettle();

    expect(fake.moved, isNotNull);
    expect(fake.moved!.$1, ['f1']);
    expect(fake.moved!.$2, 'sess-1');
    expect(find.text('1 selected'), findsNothing, reason: 'selection clears');
  });

  testWidgets('§65.9: manual palette shows sliders and debounces a re-render',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    await _pump(tester, fake);
    await tester.tap(find.text('Ha'));
    await tester.pumpAndSettle();

    // Non-manual palettes send no knobs.
    expect(fake.previewKnobs, (null, null, null));

    await tester.tap(find.text('auto_stf'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('manual').last);
    await tester.pumpAndSettle();
    expect(find.text('Black'), findsOneWidget);
    expect(find.text('Midtone'), findsOneWidget);
    expect(find.text('White'), findsOneWidget);
    // Switching to manual rendered with the seed values.
    expect(fake.previewRequest!.$2, 'manual');
    expect(fake.previewKnobs, (0.02, 0.5, 0.98));

    // Drag the midtone slider; the debounce coalesces into one request.
    await tester.drag(find.byType(Slider).at(1), const Offset(80, 0));
    await tester.pump(const Duration(milliseconds: 100));
    final requestsBeforeQuiet = fake.previewKnobs;
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pumpAndSettle();
    expect(fake.previewKnobs!.$2, isNot(0.5),
        reason: 'the debounced render carried the dragged midtone');
    expect(requestsBeforeQuiet!.$2, 0.5,
        reason: 'no render fired inside the 200 ms quiet window');
  });

  testWidgets('§39.10: Export fetches the tar and saves via the injected saver',
      (tester) async {
    final fake = _FakeLibraryClient(sessions: [
      _session()
    ], frames: {
      'sess-1': [_frame('f1')],
    });
    (String, int)? savedCall;
    final previousSaver = frameExportSaver;
    frameExportSaver = (fileName, bytes) async {
      savedCall = (fileName, bytes.length);
      return '/tmp/$fileName';
    };
    addTearDown(() => frameExportSaver = previousSaver);
    await _pump(tester, fake);

    await tester.longPress(find.text('Ha').first);
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Export'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Export'));
    await tester.pumpAndSettle();

    expect(fake.exported, ['f1']);
    expect(savedCall, ('openastroara-frames-test.tar', 3));
    expect(find.text('1 selected'), findsNothing, reason: 'selection clears');
  });

  testWidgets('an empty catalog explains itself instead of showing demo data',
      (tester) async {
    await _pump(tester, _FakeLibraryClient());
    expect(find.textContaining('No sessions yet'), findsOneWidget);
    expect(find.textContaining('Orion'), findsNothing,
        reason: 'the 12f.1 demo sessions are gone from this screen');
  });
}
