import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/data_package.dart';
import 'package:openastroara/screens/wizard/screens/screen_data_and_review.dart';
import 'package:openastroara/state/sky_atlas/data_manager_state.dart';
import 'package:openastroara/state/wizard_state.dart';

/// Drop-in fake for the packages provider so widget tests can pin the screen to
/// a loading / error / null-server / data state without a live daemon.
class _FakePackages extends DataManagerPackagesNotifier {
  _FakePackages(this._build);
  final Future<List<DataPackage>?> Function() _build;
  @override
  Future<List<DataPackage>?> build() => _build();
}

void main() {
  group('formatBytes', () {
    test('floors zero and negative sizes to 0 B', () {
      expect(formatBytes(0), '0 B');
      expect(formatBytes(-1), '0 B');
    });

    test('scales to B / KB / MB / GB with one decimal on scaled units', () {
      expect(formatBytes(512), '512 B');
      expect(formatBytes(2 * 1024), '2.0 KB');
      expect(formatBytes((1.5 * 1024 * 1024).round()), '1.5 MB');
      expect(formatBytes(5 * 1024 * 1024), '5.0 MB');
      expect(formatBytes((1.5 * 1024 * 1024 * 1024).round()), '1.5 GB');
    });
  });

  group('ScreenSkyData', () {
    const pkgA = DataPackage(
        id: 'a',
        name: 'Tycho-2 catalog',
        description: 'Bright stars',
        sizeBytes: 1 << 20);
    const pkgB = DataPackage(
        id: 'b',
        name: 'Messier targets',
        description: 'DSO list',
        sizeBytes: 2 << 20);
    const installed = DataPackage(
        id: 'c', name: 'Already here', isInstalled: true, sizeBytes: 1 << 10);

    // Pumps the screen with the packages provider overridden to [build], and
    // returns the container so a test can read back the draft it mutated.
    Future<ProviderContainer> pump(
      WidgetTester tester, {
      required Future<List<DataPackage>?> Function() build,
    }) async {
      final container = ProviderContainer(overrides: [
        dataManagerPackagesProvider.overrideWith(() => _FakePackages(build)),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: ScreenSkyData())),
      ));
      return container;
    }

    testWidgets('shows a spinner while the catalog is loading', (tester) async {
      await pump(tester, build: () => Completer<List<DataPackage>?>().future);
      await tester.pump(); // let the loading frame settle
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows an error message when the catalog fails', (tester) async {
      await pump(tester, build: () async => throw Exception('boom'));
      await tester.pumpAndSettle();
      expect(find.textContaining('Couldn\'t load the sky-data catalog'),
          findsOneWidget);
    });

    testWidgets('prompts to connect when no server is bound', (tester) async {
      await pump(tester, build: () async => null);
      await tester.pumpAndSettle();
      expect(find.textContaining('Connect to a daemon'), findsOneWidget);
    });

    testWidgets('reports when everything is already installed', (tester) async {
      await pump(tester, build: () async => const [installed]);
      await tester.pumpAndSettle();
      expect(find.textContaining('already installed'), findsOneWidget);
      expect(find.byType(CheckboxListTile), findsNothing);
    });

    testWidgets('lists only not-installed packages', (tester) async {
      await pump(tester, build: () async => const [pkgA, pkgB, installed]);
      await tester.pumpAndSettle();
      expect(find.byType(CheckboxListTile), findsNWidgets(2));
      expect(find.text('Tycho-2 catalog'), findsOneWidget);
      expect(find.text('Messier targets'), findsOneWidget);
      expect(find.text('Already here'), findsNothing);
    });

    testWidgets('a blank description renders no dangling separator',
        (tester) async {
      const noDesc = DataPackage(id: 'd', name: 'No-desc pack', sizeBytes: 1 << 20);
      await pump(tester, build: () async => const [noDesc]);
      await tester.pumpAndSettle();
      expect(find.text('1.0 MB'), findsOneWidget); // size only, no leading "·"
      expect(find.textContaining('·'), findsNothing);
    });

    testWidgets('ticking a package adds its id to the draft', (tester) async {
      final container =
          await pump(tester, build: () async => const [pkgA, pkgB]);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Tycho-2 catalog'));
      await tester.pump();
      expect(container.read(wizardControllerProvider).draft.skyDataDownloadIds,
          contains('a'));
    });

    testWidgets('Select all ticks every package, then disables itself',
        (tester) async {
      final container =
          await pump(tester, build: () async => const [pkgA, pkgB]);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Select all'));
      await tester.pump();
      expect(container.read(wizardControllerProvider).draft.skyDataDownloadIds,
          containsAll(<String>['a', 'b']));
      // Everything selected → Select all is now disabled, Clear is enabled.
      expect(tester.widget<TextButton>(find.ancestor(
              of: find.text('Select all'),
              matching: find.byType(TextButton)))
          .onPressed,
          isNull);
    });

    testWidgets('Clear is disabled until selected, then empties the draft',
        (tester) async {
      final container =
          await pump(tester, build: () async => const [pkgA, pkgB]);
      await tester.pumpAndSettle();
      TextButton clearButton() => tester.widget<TextButton>(find.ancestor(
          of: find.text('Clear'), matching: find.byType(TextButton)));
      expect(clearButton().onPressed, isNull);
      await tester.tap(find.text('Select all'));
      await tester.pump();
      expect(clearButton().onPressed, isNotNull);
      await tester.tap(find.text('Clear'));
      await tester.pump();
      expect(container.read(wizardControllerProvider).draft.skyDataDownloadIds,
          isEmpty);
      expect(clearButton().onPressed, isNull);
    });

    testWidgets('unticking a package removes its id from the draft',
        (tester) async {
      final container =
          await pump(tester, build: () async => const [pkgA, pkgB]);
      await tester.pumpAndSettle();
      final ids = container.read(wizardControllerProvider).draft.skyDataDownloadIds;
      await tester.tap(find.text('Tycho-2 catalog'));
      await tester.pump();
      expect(ids, contains('a'));
      await tester.tap(find.text('Tycho-2 catalog'));
      await tester.pump();
      expect(ids, isNot(contains('a')));
    });
  });
}
