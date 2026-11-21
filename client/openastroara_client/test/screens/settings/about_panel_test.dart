import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/about_panel.dart';
import 'package:openastroara/services/server_maintenance_api.dart';
import 'package:openastroara/version/ara_version.dart';
import 'package:package_info_plus/package_info_plus.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  // Expectations come from the app constant so a release bump keeps them true.
  final app = AraVersion.tryParse(kAraAppVersion)!;

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

  Future<void> pumpPanel(WidgetTester tester, {String? serverVersion}) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        if (serverVersion != null)
          daemonVersionsProvider.overrideWith(
            (ref) async => DaemonVersions(
              daemonVersion: serverVersion,
              daemonGitSha: '',
              dotnetVersion: '',
              osRelease: '',
              osArch: '',
            ),
          ),
      ],
      child: const MaterialApp(home: Scaffold(body: AboutPanel())),
    ));
    await tester.pump(); // resolve the version future
    await tester.pump();
  }

  testWidgets('renders the app identity, version, and license posture',
      (tester) async {
    await pumpPanel(tester);
    expect(find.textContaining('OpenAstro Ara — the desktop client'), findsOneWidget);
    expect(find.textContaining('Version ${displayVersion(kAraAppVersion)}+1'),
        findsOneWidget,
        reason: 'the version is the app constant (kAraAppVersion) with its '
            'stage marker; the build number comes from the platform');
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
    expect(find.text('OpenAstro Ara'), findsWidgets,
        reason: 'the application name heads the licenses page');
  });

  testWidgets('the version shown is the app constant, not the platform version',
      (tester) async {
    // #2093: Apple platforms can report a beta's CFBundleShortVersionString
    // without its -beta.N; the app must not then call itself stable.
    PackageInfo.setMockInitialValues(
      appName: 'openastroara',
      packageName: 'net.openastro.ara',
      version: '9.9.9',
      buildNumber: '41',
      buildSignature: '',
      installerStore: null,
    );
    await pumpPanel(tester);
    expect(
      find.textContaining('Version ${displayVersion(kAraAppVersion)}+41'),
      findsOneWidget,
      reason: 'the build number still comes from the platform',
    );
    expect(find.textContaining('9.9.9'), findsNothing);
  });

  testWidgets('a server from another release line shows the update-both warning',
      (tester) async {
    final server = '${app.major}.${app.minor + 1}.0';
    await pumpPanel(tester, serverVersion: server);
    expect(find.textContaining('Server: $server'), findsOneWidget);
    expect(
      find.text('Server $server and app ${app.display} differ; '
          'update both from the same release.'),
      findsOneWidget,
    );
  });

  testWidgets('a server on the same release line shows no warning',
      (tester) async {
    final server = '${app.major}.${app.minor}.${app.patch + 3}';
    await pumpPanel(tester, serverVersion: server);
    expect(find.textContaining('Server: $server'), findsOneWidget);
    expect(find.byKey(const Key('version-mismatch-warning')), findsNothing);
  });

  testWidgets('an old daemon with a four-part version shows no warning and no error',
      (tester) async {
    await pumpPanel(tester, serverVersion: '1.0.0.0');
    expect(find.textContaining('Server: 1.0.0.0'), findsOneWidget);
    expect(find.byKey(const Key('version-mismatch-warning')), findsNothing);
    expect(tester.takeException(), isNull);
  });
}
