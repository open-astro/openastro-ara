import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/widgets/imaging/capture_progress_card.dart';

void main() {
  testWidgets('hidden while idle', (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CaptureProgressCard())),
    ));
    expect(find.textContaining('Exposing'), findsNothing);
  });

  testWidgets('exposing shows the progress bar, percent and remaining time',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    n.beginExposing(const Duration(seconds: 10));
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CaptureProgressCard())),
    ));
    // The daemon then reports 25% — driving the bar and the remaining time.
    n.updateExposureProgress(25);
    await tester.pump();

    expect(find.text('Exposing 10s… 25%'), findsOneWidget);
    // 7.5s left + the 2s default download estimate → ready in ~9.5s.
    expect(find.text('~7.5 s left · ready in ~9.5 s'), findsOneWidget);
    final bar = tester.widget<LinearProgressIndicator>(
        find.byType(LinearProgressIndicator));
    expect(bar.value, closeTo(0.25, 1e-9));
  });

  testWidgets('downloading shows the spinner and elapsed', (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    n.beginExposing(const Duration(seconds: 3));
    n.updateExposureProgress(100);
    await tester.pump(kExposingMinVisible); // min-visible hold → downloading

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CaptureProgressCard())),
    ));

    expect(find.text('Downloading frame…'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
  });

  testWidgets('shows the ready-in estimate once a download time is known',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    // First capture seeds the rolling download estimate.
    n.beginExposing(const Duration(seconds: 3));
    n.updateExposureProgress(100);
    await tester.pump(kExposingMinVisible); // → downloading
    n.complete('abc', generation: n.currentGeneration);
    await tester.pump(kDownloadingMinVisible); // → done (rolling measured)
    n.reset();
    // Second capture: 10s exposure, 50% done → 5s left + download estimate.
    n.updateRollingForTest(2000);
    n.beginExposing(const Duration(seconds: 10));
    n.updateExposureProgress(50);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CaptureProgressCard())),
    ));

    expect(find.textContaining('~5.0 s left'), findsOneWidget);
    expect(find.textContaining('ready in ~7.0 s'), findsOneWidget);
  });

  testWidgets('Cancel shows while exposing and fires the callback',
      (tester) async {
    var cancelled = 0;
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    n.beginExposing(const Duration(seconds: 5));

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
          home: Scaffold(
              body: CaptureProgressCard(onCancel: () => cancelled++))),
    ));
    await tester.pump(const Duration(milliseconds: 1000)); // bar animates

    expect(find.text('Cancel'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    expect(cancelled, 1);

    // Let the in-flight tick + any timers settle.
    await tester.pump(const Duration(seconds: 1));
    n.reset();
    await tester.pump();
  });

  testWidgets('Cancel also shows while downloading', (tester) async {
    var cancelled = 0;
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    n.beginExposing(const Duration(seconds: 3));
    n.updateExposureProgress(100);
    await tester.pump(kExposingMinVisible); // → downloading

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
          home: Scaffold(
              body: CaptureProgressCard(onCancel: () => cancelled++))),
    ));

    expect(find.text('Cancel'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    expect(cancelled, 1);
    // Let the 6s terminal auto-clear finish (no timers left behind).
    await tester.pump(const Duration(seconds: 7));
  });

  testWidgets('failed shows Retry which fires the callback', (tester) async {
    var retried = 0;
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    n.beginExposing(const Duration(seconds: 3));
    n.fail('boom');

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
          home: Scaffold(
              body: CaptureProgressCard(onRetry: () => retried++))),
    ));

    expect(find.text('Capture failed'), findsOneWidget);
    await tester.tap(find.text('Retry'));
    expect(retried, 1);
    // Let the 6s auto-clear timer finish so nothing outlives the test.
    await tester.pump(const Duration(seconds: 7));
  });

  testWidgets('done and failed states render', (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final n = container.read(captureProgressProvider.notifier);
    n.beginExposing(const Duration(seconds: 3));
    n.complete('abc', generation: n.currentGeneration);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CaptureProgressCard())),
    ));
    expect(find.text('Frame ready'), findsOneWidget);

    n.fail('boom');
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CaptureProgressCard())),
    ));
    expect(find.text('Capture failed'), findsOneWidget);
    expect(find.text('boom'), findsOneWidget);
    // Let the terminal auto-clear timers finish so nothing outlives the test.
    await tester.pump(const Duration(seconds: 7));
  });
}

// Test helper to set a deterministic rolling estimate.
extension on CaptureProgressNotifier {
  void updateRollingForTest(int ms) {
    state = state.copyWith(rollingDownloadMs: ms);
  }
}
