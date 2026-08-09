import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';

/// The product name as it appears in window titles and about text.
const String kAppName = 'OpenAstro Ara';

/// Release-stage marker appended everywhere the version is shown:
/// `'a'` = alpha, `'b'` = beta, `''` = stable. One constant so the window
/// title, About panel, and bug-report dialog all flip stage together.
const String kReleaseStage = 'a';

/// The user-facing version string, e.g. `0.0.1a` — pubspec.yaml's version
/// (via `package_info_plus`, so pubspec stays the single source of truth)
/// with the release stage appended. Build number deliberately omitted; the
/// About panel adds it where the extra precision is wanted.
final appDisplayVersionProvider = FutureProvider<String>((ref) async {
  final info = await PackageInfo.fromPlatform();
  return '${info.version}$kReleaseStage';
});

/// Build date stamped by CI (`--dart-define=ARA_BUILD_DATE=YYYY-MM-DD`).
/// Empty on local `flutter run`/`flutter build` invocations — shown as
/// "dev" so a hand-built binary is never mistaken for a released alpha.
/// Deliberately NOT in the window title (noise); the About panel and the
/// bug-report dialog carry it, which is where "which 0.0.1a are you on?"
/// actually gets asked.
const String kBuildDate = String.fromEnvironment('ARA_BUILD_DATE');

/// `2026-08-09` for CI builds, `dev` for local ones.
String get buildDateLabel => kBuildDate.isEmpty ? 'dev' : kBuildDate;

/// The full version line the About panel and bug-report dialog show:
/// `<version><stage>+<build> (<build date>)`, e.g. `0.0.1a+1 (2026-08-09)`
/// — or `(dev)` on a local build. One formatter so the two surfaces can't
/// drift apart. An empty build number is skipped so no `+` dangles.
String formatFullVersion(PackageInfo info) {
  final version = '${info.version}$kReleaseStage';
  final withBuild =
      info.buildNumber.isEmpty ? version : '$version+${info.buildNumber}';
  return '$withBuild ($buildDateLabel)';
}
