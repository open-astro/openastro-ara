import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/live_library.dart';

LibraryFrameItem _frame({DateTime? syncedAt, String? syncTarget}) =>
    LibraryFrameItem(
      id: 'f1',
      frameType: 'light',
      filterName: 'Ha',
      exposureSeconds: 120,
      capturedUtc: DateTime.utc(2026, 1, 1),
      hfr: null,
      starCount: null,
      rating: 0,
      syncedAt: syncedAt,
      syncTarget: syncTarget,
    );

void main() {
  group('frameSyncedForThisDesktop (§44)', () {
    test('no stream configured → null (no badge, never "unprotected")', () {
      expect(
          frameSyncedForThisDesktop(
              _frame(syncedAt: DateTime.utc(2026, 1, 2), syncTarget: 'desk-a'),
              backupConfigured: false,
              hostname: 'desk-a'),
          isNull);
    });

    test('mirrored to THIS desktop → protected (case-insensitive)', () {
      expect(
          frameSyncedForThisDesktop(
              _frame(syncedAt: DateTime.utc(2026, 1, 2), syncTarget: 'Desk-A'),
              backupConfigured: true,
              hostname: 'desk-a'),
          isTrue);
    });

    test('mirrored to ANOTHER desktop → still pending here', () {
      expect(
          frameSyncedForThisDesktop(
              _frame(syncedAt: DateTime.utc(2026, 1, 2), syncTarget: 'desk-b'),
              backupConfigured: true,
              hostname: 'desk-a'),
          isFalse,
          reason: 'sync is per-target — another machine\'s mirror does not '
              'cover this one; the daemon re-queues the frame for us');
    });

    test('never mirrored → pending', () {
      expect(
          frameSyncedForThisDesktop(_frame(),
              backupConfigured: true, hostname: 'desk-a'),
          isFalse);
    });
  });
}
