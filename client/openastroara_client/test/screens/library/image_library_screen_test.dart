import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/screens/library/image_library_screen.dart';
import 'package:openastroara/services/library_api.dart';
import 'package:openastroara/state/library/live_library_state.dart';

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

  testWidgets('an empty catalog explains itself instead of showing demo data',
      (tester) async {
    await _pump(tester, _FakeLibraryClient());
    expect(find.textContaining('No sessions yet'), findsOneWidget);
    expect(find.textContaining('Orion'), findsNothing,
        reason: 'the 12f.1 demo sessions are gone from this screen');
  });
}
