import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/storage_panel.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

void main() {
  // #1148: only XISF compresses (lossless Zstandard); FITS is always written
  // uncompressed, so the Compression control is shown for XISF only.
  group('Storage panel file format + compression (#1148)', () {
    Future<ProviderContainer> pumpPanel(WidgetTester tester) async {
      tester.view.physicalSize = const Size(1400, 4000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      // Fixed-width rows overflow under the test font; layout noise is not
      // what this test is about.
      final prior = FlutterError.onError;
      FlutterError.onError = (d) {
        if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
      };
      addTearDown(() => FlutterError.onError = prior);
      final container = ProviderContainer();
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: StoragePanel())),
      ));
      await tester.pump();
      await tester.tap(find.text('Advanced'));
      await tester.pumpAndSettle();
      return container;
    }

    testWidgets('FITS hides the compression control and says FITS is uncompressed',
        (tester) async {
      await pumpPanel(tester);
      expect(find.text('File format'), findsOneWidget);
      expect(find.text('Compression'), findsNothing);
      expect(find.textContaining('FITS is always written uncompressed'),
          findsOneWidget);
    });

    testWidgets('XISF shows lossless Zstandard and the PixInsight note',
        (tester) async {
      final container = await pumpPanel(tester);
      container
          .read(storageSettingsProvider.notifier)
          .setFileFormat(StorageFileFormat.xisf);
      await tester.pumpAndSettle();
      expect(find.text('Compression'), findsOneWidget);
      expect(find.text('Lossless (Zstandard)'), findsOneWidget);
      expect(find.text('PixInsight reads compressed XISF directly.'),
          findsOneWidget);
      // The retired FITS + RICE / gzip choices are gone.
      expect(find.textContaining('RICE'), findsNothing);
      expect(find.textContaining('gzip'), findsNothing);
    });
  });
}
