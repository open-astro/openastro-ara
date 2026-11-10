import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/storage_panel.dart';
import 'package:openastroara/screens/simple/simple_rig_storage.dart';
import 'package:openastroara/services/storage_devices_api.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/theme/ara_theme.dart';
import 'package:openastroara/widgets/storage/storage_drive_dialog.dart';

class _FakeApi extends StorageDevicesApi {
  _FakeApi() : super(_server);
  final calls = <String>[];
  String checkCode = 'clean';
  bool checkFails = false;
  StorageConfigureOutcome eject_ = const StorageConfigureOutcome(
    success: true,
    code: 'ejected',
  );

  @override
  Future<StorageConfigureOutcome> check({required String uuid}) async {
    calls.add('check $uuid');
    return StorageConfigureOutcome(success: !checkFails, code: checkCode);
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

  Future<StorageContents> Function(String uuid) contents_ =
      (_) async => StorageContents.unknown;

  @override
  Future<StorageContents> contents(String uuid) => contents_(uuid);

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
  Locale? locale,
  ThemeData? theme,
  Future<StorageSpace?> Function()? space,
  List<StorageDevice> devices = const [_store, _ntfs],
  Future<List<StorageDevice>> Function()? readDevices,
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
      storageDevicesProvider.overrideWith(
        (ref) => readDevices?.call() ?? Future.value(devices),
      ),
      storageSpaceProvider.overrideWith(
        (ref) =>
            space?.call() ??
            Future.value(
              const StorageSpace(
                saveDirectory: '/mnt/store',
                isFallback: false,
                freeBytes: 100,
                totalBytes: 200,
              ),
            ),
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        locale: locale,
        // Off, so a button's own minimum is what the 44 px checks measure.
        theme: theme ?? ThemeData(materialTapTargetSize: MaterialTapTargetSize.shrinkWrap),
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

  testWidgets('a check that fails on a kernel without exFAT says so in words, not its code (#1764)', (tester) async {
    final (c, api) = await _pump(tester);
    api
      ..checkFails = true
      ..checkCode = 'exfat_unsupported';
    await _tap(tester, find.byKey(const Key('simple-storage-check')));
    await _tap(tester, find.byKey(const Key('simple-storage-check-ask-confirm')));
    final pill = c.read(simpleMessagesProvider).single;
    expect(pill.title, "Check failed: This server can't read exFAT drives. Erase the drive as ext4 and ARA can use it.");
    expect(pill.title, isNot(contains('exfat_unsupported')));
    expect(pill.kind, SimpleMessageKind.error);
  });

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

  testWidgets('with the disk list still out the heading does not say Internal storage', (tester) async {
    await _pump(tester, readDevices: () => Completer<List<StorageDevice>>().future);
    expect(find.text('Internal storage'), findsNothing);
    expect(find.text('Checking the disk…'), findsOneWidget);
  });

  testWidgets('a failed disk list reads as an unknown disk, not Internal storage', (tester) async {
    await _pump(tester, readDevices: () async => throw StateError('no answer'));
    expect(find.text('Internal storage'), findsNothing);
    expect(find.text('Unknown disk'), findsOneWidget);
  });

  testWidgets('with the disk list read and no store, it is Internal storage', (tester) async {
    await _pump(tester, devices: const [_ntfs]);
    expect(find.text('Internal storage'), findsOneWidget);
  });

  testWidgets('Eject and Check disk results are in Brazilian Portuguese', (tester) async {
    final (c, _) = await _pump(tester, locale: const Locale('pt', 'BR'));
    await _tap(tester, find.byKey(const Key('simple-storage-eject')));
    expect(c.read(simpleMessagesProvider).first.title, 'Ejetado. Já pode desconectar o disco.');
    await _tap(tester, find.byKey(const Key('simple-storage-check')));
    await _tap(tester, find.byKey(const Key('simple-storage-check-ask-confirm')));
    expect(c.read(simpleMessagesProvider).first.title, 'Disco verificado: sem problemas.');
    await tester.pump(kSimpleResultLife);
  });

  testWidgets('a repaired disk in Simple Mode names no full UI button', (tester) async {
    final (c, api) = await _pump(tester);
    api.checkCode = 'repaired';
    await _tap(tester, find.byKey(const Key('simple-storage-check')));
    await _tap(tester, find.byKey(const Key('simple-storage-check-ask-confirm')));
    expect(c.read(simpleMessagesProvider).single.title, 'Disk checked — repairs were made.');
    await tester.pump(kSimpleResultLife);
  });

  testWidgets('the full UI still points a repaired disk at Find frames on disk', (tester) async {
    final (c, api) = await _pump(tester, simple: false, child: StorageCheckDiskButton(label: 'Check', confirm: () async => true));
    c.listen(storageDevicesProvider, (_, _) {}); // the button only reads what the panel keeps loaded
    await tester.pump();
    api.checkCode = 'repaired';
    await tester.tap(find.text('Check'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Find frames on disk'), findsOneWidget);
  });

  double fontSize(WidgetTester tester, Finder f) =>
      tester.widget<RichText>(find.descendant(of: f, matching: find.byType(RichText)).first).text.style!.fontSize!;

  testWidgets('the erase warning and its typed confirmation read at 14 px', (tester) async {
    await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    for (final words in ['ARA saves frames in', 'to confirm:']) {
      final text = find.ancestor(of: find.byWidgetPredicate((w) => w is RichText && w.text.toPlainText().contains(words)), matching: find.byType(Text)).first;
      expect(fontSize(tester, text), 14, reason: words);
    }
  });

  testWidgets('the erase step fits a phone at 2x text', (tester) async {
    await _pump(tester);
    tester.view.physicalSize = const Size(320, 1400);
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    await tester.pumpAndSettle();
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    expect(tester.takeException(), isNull);
    expect(find.text('Erase and Use Disk'), findsOneWidget);
  });

  testWidgets('the hosted bug report reads its options at 14 px', (tester) async {
    await _pump(tester, child: const SimpleLogsBlock());
    expect(fontSize(tester, find.ancestor(of: find.text('Include everything'), matching: find.byType(DropdownButton<String>))), 14);
  });

  testWidgets('the picked disk reads in the light link blue, not the theme blue', (tester) async {
    await _pump(tester, theme: buildAraTheme());
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    expect(tester.widget<RichText>(find.descendant(of: find.text('PHOTOS'), matching: find.byType(RichText))).text.style!.color, AraColors.linkText);
  });

  testWidgets('the picked disk row is filled with the selection tint', (tester) async {
    await _pump(tester, theme: buildAraTheme());
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    final row = tester.widget<ListTile>(find.ancestor(of: find.text('PHOTOS'), matching: find.byType(ListTile)));
    expect(row.selectedTileColor, AraColors.selectionTint);
  });

  testWidgets('before Erase the chooser says how many frames the disk holds, the newest date and what was never copied', (tester) async {
    final newest = DateTime.utc(2026, 10, 3, 12);
    final (_, api) = await _pump(tester);
    api.contents_ = (uuid) async => uuid == 'BBBB'
        ? StorageContents(readable: true, frames: 12, newestFrameUtc: newest, framesNotCopied: 5)
        : StorageContents.unknown;
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    expect(find.text('This disk holds 12 frames. The newest is from ${DateFormat.yMMMd('en').format(newest.toLocal())}.'), findsOneWidget);
    expect(find.text('5 frames have not been copied to another disk by ARA.'), findsOneWidget);
  });

  testWidgets('a disk whose contents cannot be read says so, not zero frames', (tester) async {
    await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    expect(find.textContaining("ARA can't see what is on this disk"), findsOneWidget);
    expect(find.text('There are no frames on this disk.'), findsNothing);
  });

  testWidgets('a contents read that fails says it could not read the disk, not that it is disconnected', (tester) async {
    final (_, api) = await _pump(tester);
    api.contents_ = (_) async => throw StateError('no answer');
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    expect(find.textContaining("ARA couldn't read what is on this disk"), findsOneWidget);
    expect(find.textContaining("while it isn't connected"), findsNothing);
  });

  testWidgets('frames with no date read as a count, never with an empty date', (tester) async {
    final (_, api) = await _pump(tester);
    api.contents_ = (_) async => const StorageContents(readable: true, frames: 3);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    expect(find.text('This disk holds 3 frames.'), findsOneWidget);
  });

  testWidgets('the not-copied warning comes before the format choice', (tester) async {
    final (_, api) = await _pump(tester);
    api.contents_ = (_) async => StorageContents(
        readable: true, frames: 12, newestFrameUtc: DateTime.utc(2026, 10, 3, 12), framesNotCopied: 5);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    final warning = find.text('5 frames have not been copied to another disk by ARA.');
    expect(tester.getTopLeft(warning).dy, lessThan(tester.getTopLeft(find.byType(RadioListTile<String>).first).dy));
    expect(find.descendant(of: find.ancestor(of: warning, matching: find.byType(Row)).first,
        matching: find.byIcon(Icons.warning_amber_rounded)), findsOneWidget);
  });

  testWidgets('Erase stays off until the disk contents are read', (tester) async {
    final read = Completer<StorageContents>();
    final (_, api) = await _pump(tester);
    api.contents_ = (_) => read.future;
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    await tester.enterText(find.byType(TextField), 'PHOTOS');
    await tester.pump();
    FilledButton erase() => tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Erase and Use Disk'));
    expect(find.text('Checking what is on this disk…'), findsOneWidget);
    expect(erase().onPressed, isNull);
    read.complete(const StorageContents(readable: true));
    await tester.pumpAndSettle();
    expect(find.text('There are no frames on this disk.'), findsOneWidget);
    expect(erase().onPressed, isNotNull);
  });

  testWidgets('Erase turns off when a refreshed disk list fails', (tester) async {
    var fail = false;
    final (c, _) = await _pump(tester, theme: buildAraTheme(), readDevices: () async {
      if (fail) throw StateError('no answer');
      return const [_store, _ntfs];
    });
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    await tester.enterText(find.byType(TextField), 'PHOTOS');
    await tester.pumpAndSettle();
    FilledButton erase() => tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Erase and Use Disk'));
    expect(erase().onPressed, isNotNull, reason: 'confirmed against a list it can see');
    fail = true;
    c.invalidate(storageDevicesProvider);
    await tester.pumpAndSettle();
    expect(erase().onPressed, isNull);
  });

  testWidgets('the picked disk shows a check, so colour is not its only cue', (tester) async {
    await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    expect(find.byIcon(Icons.check_circle), findsNothing);
    await _tap(tester, find.text('PHOTOS'));
    final row = find.ancestor(of: find.text('PHOTOS'), matching: find.byType(ListTile));
    expect(find.descendant(of: row, matching: find.byIcon(Icons.check_circle)), findsOneWidget);
  });

  testWidgets('a format option is named at least as large as its detail', (tester) async {
    await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    double size(String words) => tester.widget<RichText>(find.byWidgetPredicate((w) => w is RichText && w.text.toPlainText().startsWith(words))).text.style!.fontSize!;
    expect(size('exFAT'), greaterThanOrEqualTo(size('Plugs straight')));
  });

  testWidgets("a disk's detail line is in Brazilian Portuguese", (tester) async {
    await _pump(tester, locale: const Locale('pt', 'BR'));
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    expect(find.text('precisa ser apagado (ntfs)'), findsOneWidget);
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

  testWidgets('the Full UI still asks in a dialog before checking', (tester) async {
    final (_, api) = await _pump(
      tester,
      simple: false,
      // The panel keeps the device list alive; the button only reads it.
      child: Consumer(
        builder: (_, ref, _) {
          ref.watch(storageDevicesProvider);
          return const StorageCheckDiskButton();
        },
      ),
    );
    await tester.tap(find.byType(OutlinedButton));
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsOneWidget);
    expect(find.text('Check the store drive?'), findsOneWidget);
    expect(api.calls, isEmpty);
    await tester.tap(find.widgetWithText(FilledButton, 'Check disk'));
    await tester.pumpAndSettle();
    expect(api.calls, ['check AAAA']);
    expect(find.byType(SnackBar), findsOneWidget, reason: "the Full UI's report stays a snackbar");
    await tester.pump(const Duration(seconds: 5));
    await tester.pumpAndSettle();
  });

  testWidgets('Erase and Use Disk is the destructive red, past 4.5:1', (tester) async {
    await _pump(tester, theme: buildAraTheme());
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    await _tap(tester, find.text('PHOTOS'));
    await tester.enterText(find.byType(TextField), 'PHOTOS');
    await tester.pumpAndSettle();
    final erase = find.widgetWithText(FilledButton, 'Erase and Use Disk');
    final label = tester.widget<RichText>(find.descendant(of: erase, matching: find.byType(RichText)).first);
    final a = label.text.style!.color!.computeLuminance();
    final style = tester.widget<FilledButton>(erase).style!;
    expect(style.backgroundColor!.resolve({}), AraColors.emergencyStop);
    final b = AraColors.emergencyStop.computeLuminance();
    final ratio = (math.max(a, b) + .05) / (math.min(a, b) + .05);
    expect(ratio, greaterThanOrEqualTo(4.5));
  });

  testWidgets('while the first read is out the block does not claim there is no rig', (tester) async {
    await _pump(tester, space: () => Completer<StorageSpace?>().future);
    expect(find.text('Rig not connected'), findsNothing);
    expect(find.text('Checking the disk…'), findsOneWidget);
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-storage-choose'))).onPressed, isNull);
  });

  testWidgets('a failed read says so, not that the rig is gone, and the disk actions stay on', (tester) async {
    await _pump(tester, space: () async => throw StateError('no answer'), devices: const [_ntfs]);
    expect(find.text('Rig not connected'), findsNothing);
    expect(find.text('Internal storage'), findsNothing, reason: 'it does not know which disk');
    expect(find.byKey(const Key('simple-storage-problem')), findsOneWidget);
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-storage-choose'))).onPressed, isNotNull);
    expect(
      tester.widget<OutlinedButton>(find.descendant(of: find.byKey(const Key('simple-storage-check')), matching: find.byWidgetPredicate((w) => w is OutlinedButton))).onPressed,
      isNotNull,
    );
  });

  testWidgets('the free-space bar turns amber under the profile warning, with saving still working', (tester) async {
    const gb = 1000 * 1000 * 1000;
    await _pump(tester, space: () async => const StorageSpace(saveDirectory: '/mnt/store', isFallback: false, freeBytes: 9 * gb, totalBytes: 900 * gb));
    expect(tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).color, AraColors.accentWarning);
    expect(find.byKey(const Key('simple-storage-problem')), findsNothing);
  });

  testWidgets('a disk with room keeps the theme blue bar', (tester) async {
    const gb = 1000 * 1000 * 1000;
    await _pump(tester, space: () async => const StorageSpace(saveDirectory: '/mnt/store', isFallback: false, freeBytes: 11 * gb, totalBytes: 900 * gb));
    expect(tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).color, isNull);
  });

  for (final (key, words) in [('eject', 'No store drive to eject.'), ('check', 'No store drive to check — connect one first.')]) {
    testWidgets('$key with no store is a refused pill that fades, not a green result', (tester) async {
      final (c, _) = await _pump(tester, devices: const [_ntfs]);
      await _tap(tester, find.byKey(Key('simple-storage-$key')));
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.title, words);
      expect(pill.kind, SimpleMessageKind.refused);
      await tester.pump(kSimpleResultLife);
      expect(c.read(simpleMessagesProvider), isEmpty, reason: 'it fades');
    });
  }

  testWidgets('the inline disk chooser takes the focus and says its title', (tester) async {
    await _pump(tester);
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    expect(tester.takeAnnouncements().map((a) => a.message), contains('Choose a disk for your frames'));
    expect(FocusManager.instance.primaryFocus?.context?.findAncestorWidgetOfExactType<StorageDriveChooser>(), isNotNull);
  });

  testWidgets('the disk chooser and Check disk question are in Brazilian Portuguese', (tester) async {
    await _pump(tester, locale: const Locale('pt', 'BR'));
    await _tap(tester, find.byKey(const Key('simple-storage-choose')));
    expect(find.text('Escolha um disco para os seus quadros'), findsOneWidget);
    expect(find.text('Usar disco'), findsOneWidget);
    await _tap(tester, find.byKey(const Key('simple-storage-check')));
    expect(find.text('Verificar o disco de armazenamento?'), findsOneWidget);
  });
}
