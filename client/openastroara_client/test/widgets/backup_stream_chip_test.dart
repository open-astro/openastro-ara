import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/backup/backup_stream_state.dart';
import 'package:openastroara/widgets/backup_stream_chip.dart';

/// Serves a fixed state without the real controller's WS listener or prefs
/// restore — the chip is a pure projection of the state.
class _FixedBackupStream extends BackupStreamController {
  final BackupStreamState fixed;
  _FixedBackupStream(this.fixed);
  @override
  BackupStreamState build() => fixed;
}

void main() {
  Future<void> pumpChip(WidgetTester tester, BackupStreamState state) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          backupStreamProvider.overrideWith(() => _FixedBackupStream(state)),
        ],
        child: const MaterialApp(home: Scaffold(body: BackupStreamChip())),
      ),
    );
  }

  group('BackupStreamChip (§44)', () {
    testWidgets('hidden while the stream is disabled and healthy', (tester) async {
      await pumpChip(tester, const BackupStreamState());
      expect(find.textContaining('Backup'), findsNothing,
          reason: 'a rig that never enabled backups must not carry a dead indicator');
    });

    testWidgets('a problem shows in error state even after an auto-disable',
        (tester) async {
      await pumpChip(tester,
          const BackupStreamState(problem: 'another desktop took the slot'));
      expect(find.text('Backup: attention'), findsOneWidget);
    });

    testWidgets('active with a backlog shows the pending count', (tester) async {
      await pumpChip(
          tester,
          const BackupStreamState(
              enabled: true, active: true, pendingCount: 3));
      expect(find.text('Backup: 3 pending'), findsOneWidget);
    });

    testWidgets('active and drained shows up to date', (tester) async {
      await pumpChip(
          tester,
          const BackupStreamState(
              enabled: true, active: true, syncedThisSession: 12));
      expect(find.text('Backup: up to date'), findsOneWidget);
    });
  });
}
