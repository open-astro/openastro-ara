import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';
import 'package:openastroara/widgets/imaging/frame_viewer.dart';

class _LastFrame extends LastCapturedFrameId {
  @override
  String? build() => 'frame-1';
}

/// An 800×600 PNG, the size of the simulator camera's frames.
Future<Uint8List> _png(WidgetTester t) async => (await t.runAsync(() async {
      final recorder = ui.PictureRecorder();
      Canvas(recorder).drawRect(const Rect.fromLTWH(0, 0, 800, 600), Paint()..color = Colors.white);
      final image = await recorder.endRecording().toImage(800, 600);
      final data = await image.toByteData(format: ui.ImageByteFormat.png);
      return data!.buffer.asUint8List();
    }))!;

double _scale(WidgetTester t) =>
    t.widget<InteractiveViewer>(find.byType(InteractiveViewer)).transformationController!.value.entry(0, 0);

void main() {
  testWidgets('a frame at fit re-fits when the window grows or shrinks', (t) async {
    final bytes = await _png(t);
    final size = ValueNotifier(const Size(400, 300));
    await t.pumpWidget(ProviderScope(
      overrides: [
        lastCapturedFrameIdProvider.overrideWith(_LastFrame.new),
        framePreviewProvider('frame-1').overrideWith((ref) async => bytes),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: ValueListenableBuilder<Size>(
              valueListenable: size,
              builder: (context, s, _) => SizedBox.fromSize(size: s, child: const FrameViewer()),
            ),
          ),
        ),
      ),
    ));
    // The preview future, then the image decode (real async), then a frame.
    for (var i = 0; i < 5; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pump();
    }
    expect(_scale(t), closeTo(0.5, 1e-6), reason: 'opens at fit');

    size.value = const Size(800, 600);
    await t.pump();
    expect(_scale(t), closeTo(1.0, 1e-6), reason: 'a bigger window re-fits');

    size.value = const Size(200, 150);
    await t.pump();
    expect(_scale(t), closeTo(0.25, 1e-6), reason: 'a smaller window re-fits');
  });
}
