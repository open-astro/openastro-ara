import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/services/library_api.dart';
import 'package:openastroara/state/library/live_library_state.dart';

class _PagedFake implements LibraryClient {
  int pageTwoCalls = 0;
  final Completer<void> gate = Completer<void>();
  // When set, first-page (refresh) responses wait on this gate too.
  Completer<void>? refreshGate;

  LibrarySession _s(String id) => LibrarySession(
        id: id,
        targetName: id,
        sessionStartUtc: DateTime.utc(2026, 7, 1),
        sessionEndUtc: null,
        totalFrames: 0,
        lightFrames: 0,
        calibrationFrames: 0,
        filtersUsed: const [],
      );

  @override
  Future<CursorPage<LibrarySession>> listSessions(
      {int limit = 200, String? cursor}) async {
    if (cursor == null) {
      final g = refreshGate;
      if (g != null) await g.future;
      return CursorPage(
          items: [_s('page1')], nextCursor: 'c2', hasMore: true);
    }
    pageTwoCalls++;
    await gate.future; // hold the request in flight
    return CursorPage(items: [_s('page2')], nextCursor: null, hasMore: false);
  }

  @override
  Future<List<LibraryFrameItem>> sessionFrames(String sessionId,
          {int limit = 200}) async =>
      const [];

  @override
  String thumbnailUrl(String frameId) => 'http://x/$frameId';

  @override
  Future<LibraryFrameDetail> frameDetail(String frameId) async =>
      throw UnimplementedError();

  @override
  Future<List<int>> fetchPreview(String frameId,
          {required String stretch, int maxDimensionPx = 2048}) async =>
      throw UnimplementedError();

  @override
  Future<void> bulkRate(List<String> frameIds, int rating) async {}

  @override
  Future<void> bulkTag(List<String> frameIds,
      {List<String> addTags = const [],
      List<String> removeTags = const []}) async {}

  @override
  Future<void> bulkDelete(List<String> frameIds,
      {bool deleteFromDisk = false}) async {}

  @override
  Future<String> resumeTarget(String sessionId) async => 'x';

  @override
  void close() {}
}

void main() {
  test('r1: concurrent loadMore calls collapse to one page fetch', () async {
    final fake = _PagedFake();
    final container = ProviderContainer(
        overrides: [libraryApiProvider.overrideWithValue(fake)]);
    addTearDown(container.dispose);

    await container.read(liveLibrarySessionsProvider.future);
    final notifier = container.read(liveLibrarySessionsProvider.notifier);
    expect(notifier.hasMore, isTrue);

    // Double-tap: both calls issued before the first response lands.
    final first = notifier.loadMore();
    final second = notifier.loadMore();
    fake.gate.complete();
    await Future.wait([first, second]);

    expect(fake.pageTwoCalls, 1, reason: 'the in-flight guard absorbs the second tap');
    final items = container.read(liveLibrarySessionsProvider).value!;
    expect(items.length, 2, reason: 'page 2 appended exactly once');
    expect(notifier.hasMore, isFalse);
  });

  test('r5: an append issued during an in-flight refresh supersedes it coherently',
      () async {
    final fake = _PagedFake();
    final container = ProviderContainer(
        overrides: [libraryApiProvider.overrideWithValue(fake)]);
    addTearDown(container.dispose);

    await container.read(liveLibrarySessionsProvider.future);
    final notifier = container.read(liveLibrarySessionsProvider.notifier);

    // Refresh goes in flight (held by the gate)…
    fake.refreshGate = Completer<void>();
    final refresh = notifier.refresh();
    // …then the user taps Load more. The append mints a newer generation.
    final append = notifier.loadMore();

    // Refresh resolves FIRST — but its generation is stale, so its write drops.
    fake.refreshGate!.complete();
    await refresh;
    // Then the append's page resolves.
    fake.gate.complete();
    await append;

    final items = container.read(liveLibrarySessionsProvider).value!;
    expect(items.map((s) => s.id), ['page1', 'page2'],
        reason: 'the append wins coherently — no refresh result was clobbered '
            'mid-chain, and the list matches the cursor chain that produced it');
    expect(notifier.hasMore, isFalse);
  });
}
