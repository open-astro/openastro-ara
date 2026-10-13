import 'dart:io';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/widgets/profile/profile_import_flow.dart';

/// §70 / #1150 — every branch of the picked-file size gate, and the import
/// flow's picker seam (cancel, picker failure, oversized pick) without the real
/// FilePicker plugin.
final class _File extends PlatformFile {
  _File({required this.uri, required this.reportedLength});
  final int? reportedLength;
  @override
  final Uri uri;
  @override
  String get name => uri.pathSegments.isEmpty ? 'share' : uri.pathSegments.last;
  // The flow never touches the XFile view; readProfileShare reads by path.
  @override
  Never get xFile => throw UnimplementedError();
  @override
  int? lengthSync() => reportedLength;
  @override
  Future<int?> length() async => reportedLength;
  @override
  Future<Uint8List> readAsBytes() async => Uint8List(0);
  @override
  Stream<Uint8List> readAsByteStream() => const Stream.empty();
}

class _NoopApi extends ProfileApi {
  _NoopApi() : super(const AraServer(hostname: 'test', port: 1));
}

void main() {
  group('readProfileShare', () {
    final small = List<int>.filled(16, 1);

    test('reads a small share from its path', () async {
      final r = await readProfileShare(
        _File(uri: Uri.file('/tmp/share.json'), reportedLength: 16),
        readBytes: (path) async {
          expect(path, '/tmp/share.json');
          return small;
        },
      );
      expect(
        r,
        isA<ProfileShareReadOk>().having((o) => o.bytes, 'bytes', small),
      );
    });

    test('a length the picker could not determine is unreadable, not "small enough"', () async {
      final r = await readProfileShare(
        _File(uri: Uri.file('/tmp/share.json'), reportedLength: null),
        readBytes: (_) async => fail('must not read'),
      );
      expect(r, isA<ProfileShareUnreadable>());
    });

    test('a file over the ceiling is refused before it is read', () async {
      final r = await readProfileShare(
        _File(
          uri: Uri.file('/tmp/big.bin'),
          reportedLength: maxProfileShareBytes + 1,
        ),
        readBytes: (_) async => fail('must not read'),
      );
      expect(r, isA<ProfileShareTooLarge>());
    });

    test('a pick with no file path is unreadable', () async {
      final r = await readProfileShare(
        _File(
          uri: Uri.parse('content://downloads/share.json'),
          reportedLength: 16,
        ),
        readBytes: (_) async => small,
      );
      expect(r, isA<ProfileShareUnreadable>());
    });

    test('a read failure is unreadable', () async {
      final r = await readProfileShare(
        _File(uri: Uri.file('/tmp/share.json'), reportedLength: 16),
        readBytes: (_) async => throw const FileSystemException('gone'),
      );
      expect(r, isA<ProfileShareUnreadable>());
    });

    test(
      'a file that grew past the ceiling after the length check is refused',
      () async {
        final r = await readProfileShare(
          _File(uri: Uri.file('/tmp/share.json'), reportedLength: 16),
          readBytes: (_) async => List<int>.filled(maxProfileShareBytes + 1, 0),
        );
        expect(r, isA<ProfileShareTooLarge>());
      },
    );
  });

  group('runProfileImportFlow picker seam', () {
    Future<void> pump(
      WidgetTester tester,
      Future<PlatformFile?> Function() picker,
    ) async {
      profileSharePicker = picker;
      addTearDown(() => profileSharePicker = () => FilePicker.pickFile());
      await tester.pumpWidget(
        ProviderScope(
          overrides: [profileApiProvider.overrideWithValue(_NoopApi())],
          child: MaterialApp(
            home: Scaffold(
              body: Consumer(
                builder: (context, ref, _) => ElevatedButton(
                  onPressed: () => runProfileImportFlow(context, ref),
                  child: const Text('import'),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('import'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
    }

    testWidgets('cancelling the picker does nothing', (tester) async {
      await pump(tester, () async => null);
      expect(find.byType(SnackBar), findsNothing);
    });

    testWidgets('a picker failure is a snackbar, not an unhandled error', (
      tester,
    ) async {
      await pump(tester, () async => throw Exception('sandbox denied'));
      expect(
        find.textContaining("Couldn't open the file picker"),
        findsOneWidget,
      );
    });

    testWidgets('an oversized pick is refused with the size message', (
      tester,
    ) async {
      await pump(
        tester,
        () async => _File(
          uri: Uri.file('/tmp/big'),
          reportedLength: maxProfileShareBytes + 1,
        ),
      );
      expect(
        find.text('That file is too large to be a profile share.'),
        findsOneWidget,
      );
    });

    testWidgets('an unreadable pick says so', (tester) async {
      await pump(
        tester,
        () async => _File(uri: Uri.file('/tmp/x'), reportedLength: null),
      );
      expect(find.text("Couldn't read the selected file."), findsOneWidget);
    });
  });
}
