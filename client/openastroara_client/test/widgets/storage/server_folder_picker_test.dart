import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/storage_browse_api.dart';
import 'package:openastroara/widgets/storage/server_folder_picker.dart';

/// Scripted server walk: roots → /media → /media/usb (writable). Records the
/// paths requested so tests can assert the walk order.
class _FakeBrowse implements StorageBrowseApi {
  final List<String?> requests = [];

  static const _roots = StorageBrowseLevel(
    path: '',
    parent: null,
    writable: false,
    dirs: [
      StorageBrowseEntry(name: 'Home', path: '/home/pi'),
      StorageBrowseEntry(
          name: 'USB / removable (media)', path: '/media', removable: true),
    ],
  );

  @override
  Future<StorageBrowseLevel> browse([String? path]) async {
    requests.add(path);
    switch (path) {
      case null:
        return _roots;
      case '/media':
        return const StorageBrowseLevel(
          path: '/media',
          parent: '/',
          writable: false,
          dirs: [
            StorageBrowseEntry(name: 'usb', path: '/media/usb', removable: true),
          ],
        );
      case '/media/usb':
        return const StorageBrowseLevel(
          path: '/media/usb',
          parent: '/media',
          writable: true,
          dirs: [],
        );
      default:
        throw StateError('404: $path');
    }
  }

  final List<(String, String)> created = [];

  @override
  Future<StorageBrowseLevel> createFolder(String parentPath, String name) async {
    created.add((parentPath, name));
    return StorageBrowseLevel(
      path: parentPath,
      parent: '/media',
      writable: true,
      dirs: [StorageBrowseEntry(name: name, path: '$parentPath/$name')],
    );
  }

  @override
  void close() {}

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

void main() {
  const server = AraServer(hostname: 'test', port: 1);

  Future<({_FakeBrowse api, Future<String?> result})> open(
      WidgetTester tester, {String? startPath}) async {
    final api = _FakeBrowse();
    late Future<String?> result;
    await tester.pumpWidget(ProviderScope(
      overrides: [
        storageBrowseApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(builder: (context, ref, _) {
            return ElevatedButton(
              onPressed: () {
                result = showServerFolderPicker(context, ref,
                    server: server, startPath: startPath);
              },
              child: const Text('open'),
            );
          }),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    return (api: api, result: result);
  }

  testWidgets('walks roots → USB mount and returns the picked writable folder',
      (tester) async {
    final o = await open(tester);

    // Roots: USB entry badged, "Use this folder" disabled (roots aren't a dir).
    expect(find.text('USB / removable (media)'), findsOneWidget);
    expect(find.text('Removable / USB'), findsOneWidget);
    expect(
        tester
            .widget<FilledButton>(find.widgetWithText(FilledButton, 'Use this folder'))
            .onPressed,
        isNull);

    await tester.tap(find.text('USB / removable (media)'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('usb'));
    await tester.pumpAndSettle();

    // /media/usb is writable → the pick button enables and returns the path.
    await tester.tap(find.text('Use this folder'));
    await tester.pumpAndSettle();
    expect(await o.result, '/media/usb');
    expect(o.api.requests, [null, '/media', '/media/usb']);
  });

  testWidgets('a non-writable directory keeps the pick button disabled',
      (tester) async {
    await open(tester);
    await tester.tap(find.text('USB / removable (media)'));
    await tester.pumpAndSettle();

    // /media itself is not writable → disabled with the tooltip explanation.
    expect(
        tester
            .widget<FilledButton>(find.widgetWithText(FilledButton, 'Use this folder'))
            .onPressed,
        isNull);
  });

  testWidgets('an unknown start path falls back to the roots, not an error',
      (tester) async {
    final o = await open(tester, startPath: '/gone/rig');
    expect(find.text('USB / removable (media)'), findsOneWidget,
        reason: 'stale path from another rig → roots listing');
    expect(o.api.requests.first, '/gone/rig');
  });

  testWidgets(
      'New folder is disabled at the roots and creates + relists in a writable dir',
      (tester) async {
    final o = await open(tester);

    // Roots: can't create there.
    expect(
        tester
            .widget<TextButton>(find.widgetWithText(TextButton, 'New folder'))
            .onPressed,
        isNull);

    await tester.tap(find.text('USB / removable (media)'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('usb'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('New folder'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField), 'Lights');
    await tester.tap(find.text('Create'));
    await tester.pumpAndSettle();

    expect(o.api.created, [('/media/usb', 'Lights')]);
    expect(find.text('Lights'), findsOneWidget,
        reason: 'the refreshed listing shows the new folder');
  });

  testWidgets('cancel returns null', (tester) async {
    final o = await open(tester);
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(await o.result, isNull);
  });
}
