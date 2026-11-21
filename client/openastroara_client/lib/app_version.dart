import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';

import 'version/ara_version.dart';

/// The product name as it appears in window titles and about text.
const String kAppName = 'OpenAstro Ara';

/// The user-facing version string, e.g. `0.0.1a` or `1.0.0-beta.1`: this
/// app's own version ([kAraAppVersion], #2093) with the release stage worked
/// out from the version itself (major 0 is alpha, `-beta.N` is a beta), so the
/// window title, About panel and bug-report dialog all flip stage together.
/// Build number deliberately omitted; the About panel adds it where the extra
/// precision is wanted. Async so the window title's listener sees it resolve.
final appDisplayVersionProvider = FutureProvider<String>((ref) async {
  return displayVersion(ref.watch(appVersionProvider));
});

/// This app's version with no stage letter or build number (`0.0.1`,
/// `1.0.0-beta.1`): what the server's version is compared with. The constant,
/// not the platform's version, which can drop `-beta.N` on Apple platforms.
final appVersionProvider = Provider<String>((ref) => kAraAppVersion);

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
/// — or `(dev)` on a local build. The version is [kAraAppVersion]; only the
/// build number comes from the platform ([info]). One formatter so the two
/// surfaces can't drift apart. An empty build number is skipped so no `+`
/// dangles.
String formatFullVersion(PackageInfo info) {
  final version = displayVersion(kAraAppVersion);
  final withBuild =
      info.buildNumber.isEmpty ? version : '$version+${info.buildNumber}';
  return '$withBuild ($buildDateLabel)';
}
