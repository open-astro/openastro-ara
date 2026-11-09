import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_rig_storage.dart';
import 'package:openastroara/services/storage_devices_api.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/storage/storage_drive_dialog.dart';

class _FakeApi extends StorageDevicesApi {
  _FakeApi() : super(_server);
  final calls = <String>[];
  StorageConfigureOutcome eject_ = const StorageConfigureOutcome(
    success: true,
    code: 'ejected',
  );

  @override
  Future<StorageConfigureOutcome> check({required String uuid}) async {
    calls.add('check $uuid');
    return const StorageConfigureOutcome(success: true, code: 'clean');
  }

  @override
  Future<StorageConfigureOutcome> eject({required String uuid}) async {
    calls.add('eject $uuid');
    return eject_;
  }

  @override
  Future<StorageConfigureOutcome> configure({
    required String uuid,
    bool format = false,
    String? confirmLabel,
    String? filesystem,
  }) async {
    calls.add(
      'configure $uuid format=$format label=$confirmLabel fs=$filesystem',
    );
    return const StorageConfigureOutcome(success: true, code: 'ok');
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'rig', port: 1);

const _store = StorageDevice(
  path: '/dev/sda1',
  uuid: 'AAAA',
  label: 'STORE',
  fileSystem: 'ext4',
  removable: true,
  isSystemDisk: false,
  isAraStore: true,
);
const _ntfs = StorageDevice(
  path: '/dev/sdb1',
  uuid: 'BBBB',
  label: 'PHOTOS',
  fileSystem: 'ntfs',
  removable: true,
  isSystemDisk: false,
  isAraStore: false,
);

Future<(ProviderContainer, _FakeApi)> _pump(
  WidgetTester tester, {
  bool simple = true,
  Widget? child,
}) async {
  tester.view.physicalSize = const Size(800, 1400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = _FakeApi();
  final container = ProviderContainer(
    overrides: [
      simpleModeActiveProvider.overrideWithValue(simple),
      activeServerProvider.overrideWithValue(_server),
      storageDevicesApiFactoryProvider.overrideWithValue((_) => api),
      storageDevicesProvider.overrideWith((ref) async => const [_store, _ntfs]),
      storageSpaceProvider.overrideWith(
        (ref) async => const StorageSpace(
          saveDirectory: '/mnt/store',
          isFallback: false,
          freeBytes: 100,
          totalBytes: 200,
        ),
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: SingleChildScrollView(
            child: child ?? const SimpleStorageBlock(),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (container, api);
}

void _expectNoPopup(WidgetTester tester) {
  expect(
    tester.state<NavigatorState>(find.byType(Navigator)).canPop(),
    isFalse,
    reason: 'no dialog, sheet or menu route',
  );
  expect(find.byType(SnackBar), findsNothing);
}

Future<void> _tap(WidgetTester tester, Finder f) async {
  await tester.ensureVisible(f);
  await tester.pumpAndSettle();
  await tester.tap(f);
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    'Check disk asks in the Storage block, then checks the store and says so in a pill',
    (tester) async {
      final (c, api) = await _pump(tester);
      await _tap(tester, find.byKey(const Key('simple-storage-check')));
      _expectNoPopup(tester);
      expect(find.text('Check the store drive?'), findsOneWidget);
      expect(api.calls, isEmpty, reason: 'nothing until confirmed');
      await _tap(
        tester,
        find.byKey(const Key('simple-storage-check-ask-confirm')),
      );
      _expectNoPopup(tester);
      expect(api.calls, ['check AAAA']);
      expect(find.text('Check the store drive?'), findsNothing);
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.title, 'Disk checked — clean.');
      expect(pill.kind, SimpleMessageKind.result);
      await tester.pump(kSimpleResultLife); // the pill's timer
    },
  );

  testWidgets('Cancelling the inline Check disk sends nothing', (tester) async {
    final (c, api) = await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-check')));
    await _tap(
      tester,
      find.byKey(const Key('simple-storage-check-ask-cancel')),
    );
    expect(find.text('Check the store drive?'), findsNothing);
    expect(api.calls, isEmpty);
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('Check disk confirm and cancel are at least 44 px', (
    tester,
  ) async {
    await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-check')));
    for (final k in ['confirm', 'cancel']) {
      expect(
        tester.getSize(find.byKey(Key('simple-storage-check-ask-$k'))).height,
        greaterThanOrEqualTo(44),
      );
    }
  });

  testWidgets('Eject says what happened in a pill', (tester) async {
    final (c, api) = await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-eject')));
    _expectNoPopup(tester);
    expect(api.calls, ['eject AAAA']);
    expect(
      c.read(simpleMessagesProvider).single.title,
      'Ejected — safe to unplug the drive.',
    );
    await tester.pump(kSimpleResultLife); // the pill's timer
  });

  testWidgets('a refused Eject is an error pill', (tester) async {
    final (c, api) = await _pump(tester);
    api.eject_ = const StorageConfigureOutcome(
      success: false,
      code: 'device_busy',
    );
    await _tap(tester, find.byKey(const Key('simple-storage-eject')));
    expect(c.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
  });

  testWidgets(
    'Choose disk lists the disks in the Storage block and uses the pick',
    (tester) async {
      final (c, api) = await _pump(tester);
      await _tap(tester, find.byKey(const Key('simple-storage-choose')));
      _expectNoPopup(tester);
      expect(find.byType(AlertDialog), findsNothing);
      expect(find.text('Choose a disk for your frames'), findsOneWidget);
      await _tap(tester, find.byIcon(Icons.usb).first);
      await _tap(tester, find.widgetWithText(FilledButton, 'Use Disk'));
      _expectNoPopup(tester);
      expect(
        api.calls.single,
        'configure AAAA format=false label=null fs=null',
      );
      expect(
        find.text('Choose a disk for your frames'),
        findsNothing,
        reason: 'closes on success',
      );
      expect(
        c.read(simpleMessagesProvider).single.title,
        contains('is now used for frames.'),
      );
      await tester.pump(kSimpleResultLife); // the pill's timer
    },
  );

  testWidgets('erasing a disk stays inline and needs its name typed', (
    tester,
  ) async {
    final (_, api) = await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.byIcon(Icons.usb).last);
    _expectNoPopup(tester);
    expect(find.text('This disk must be erased'), findsOneWidget);
    final erase = find.widgetWithText(FilledButton, 'Erase and Use Disk');
    expect(
      tester.widget<FilledButton>(erase).onPressed,
      isNull,
      reason: 'the name is not typed yet',
    );
    await tester.enterText(find.byType(TextField), 'PHOTOS');
    await tester.pump();
    await _tap(tester, erase);
    expect(
      api.calls.single,
      'configure BBBB format=true label=PHOTOS fs=exfat',
    );
    await tester.pump(kSimpleResultLife); // the pill's timer
  });

  testWidgets('the Full UI still opens the disk dialog', (tester) async {
    await _pump(
      tester,
      simple: false,
      child: Consumer(
        builder: (context, ref, _) => TextButton(
          onPressed: () => showStorageDriveDialog(context, ref),
          child: const Text('open'),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsOneWidget);
    expect(find.text('Choose a disk for your frames'), findsOneWidget);
  });
}
