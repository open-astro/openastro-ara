import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/about_panel.dart';
import 'package:package_info_plus/package_info_plus.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    PackageInfo.setMockInitialValues(
      appName: 'openastroara',
      packageName: 'net.openastro.ara',
      version: '0.0.1',
      buildNumber: '1',
      buildSignature: '',
      installerStore: null,
    );
  });

  Future<void> pumpPanel(WidgetTester tester) async {
    await tester.pumpWidget(const ProviderScope(
      child: MaterialApp(home: Scaffold(body: AboutPanel())),
    ));
    await tester.pump(); // resolve the version future
  }

  testWidgets('renders the app identity, version, and license posture',
      (tester) async {
    await pumpPanel(tester);
    expect(find.textContaining('OpenAstro Ara — WILMA'), findsOneWidget);
    expect(find.textContaining('Version 0.0.1+1'), findsOneWidget,
        reason: 'pubspec is the single source of truth via package_info_plus');
    expect(find.textContaining('AGPL-3.0'), findsOneWidget);
    expect(find.textContaining('MPL-2.0'), findsOneWidget);
    expect(find.textContaining('N.I.N.A.'), findsOneWidget,
        reason: 'the fork provenance is part of the notice');
  });

  testWidgets('the licenses button opens Flutter\'s LicensePage over the registry',
      (tester) async {
    await pumpPanel(tester);
    await tester.tap(find.text('Open-source licenses'));
    // The LicensePage collects LicenseRegistry entries asynchronously —
    // pump a few frames rather than settle (its spinner would never settle
    // in a tight loop on some channels).
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));

    expect(find.byType(LicensePage), findsOneWidget);
    expect(find.text('OpenAstro Ara (WILMA)'), findsWidgets,
        reason: 'the application name heads the licenses page');
  });
}
