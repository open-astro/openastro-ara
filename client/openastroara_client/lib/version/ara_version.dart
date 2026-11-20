/// Ara's version numbers (#2093), with no Flutter dependency.
///
/// The root `VERSION` file says `1.0.0~beta1`; the app, the API and mDNS show
/// the SemVer spelling `1.0.0-beta.1`, and `pubspec.yaml` carries
/// `1.0.0-beta.1+<build>`. A stable release is `1.0.0` everywhere.
library;

/// This app's own version in the display (SemVer) spelling: `0.0.1`,
/// `1.0.0-beta.1`. The app never reads its version from the platform: on
/// Apple platforms CFBundleShortVersionString can drop the `-beta.N`, which
/// would make a beta look stable. A release PR sets it with `VERSION`, the
/// pubspec version and the README badge; `scripts/version.py --check` fails
/// unless it is `VERSION`'s display spelling.
const String kAraAppVersion = '0.0.1';

/// How finished a release is, read from its version.
enum ReleaseStage {
  /// Before 1.0.0 (major 0) and not a beta.
  alpha,

  /// A `-beta.N` prerelease.
  beta,

  /// 1.0.0 or later, not a beta.
  stable,
}

/// One Ara version: `X.Y.Z` or `X.Y.Z-beta.N`.
class AraVersion {
  const AraVersion(this.major, this.minor, this.patch, {this.beta});

  final int major;
  final int minor;
  final int patch;

  /// N of a `-beta.N` prerelease (1 or more); null for a release.
  final int? beta;

  // X.Y.Z, then an optional -beta.N (or the VERSION / .deb ~betaN), then
  // optional build metadata (+37, or the git sha .NET adds).
  static final _pattern = RegExp(
    r'^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)'
    r'(?:(?:-beta\.|~beta)([1-9]\d*))?'
    r'(?:\+[0-9A-Za-z.-]+)?$',
  );

  /// The version in [text], or null when it is not an Ara version (an old
  /// daemon's `1.0.0.0`, a dev string, a blank).
  static AraVersion? tryParse(String? text) {
    final m = _pattern.firstMatch(text?.trim() ?? '');
    if (m == null) {
      return null;
    }
    final beta = m.group(4);
    return AraVersion(
      int.parse(m.group(1)!),
      int.parse(m.group(2)!),
      int.parse(m.group(3)!),
      beta: beta == null ? null : int.parse(beta),
    );
  }

  ReleaseStage get stage => beta != null
      ? ReleaseStage.beta
      : major == 0
      ? ReleaseStage.alpha
      : ReleaseStage.stable;

  /// The SemVer spelling people see: `1.0.0` or `1.0.0-beta.1`.
  String get display =>
      '$major.$minor.$patch${beta == null ? '' : '-beta.$beta'}';

  /// Whether [other] comes from the same X.Y release line.
  bool sameLine(AraVersion other) =>
      major == other.major && minor == other.minor;

  @override
  bool operator ==(Object other) =>
      other is AraVersion &&
      other.major == major &&
      other.minor == minor &&
      other.patch == patch &&
      other.beta == beta;

  @override
  int get hashCode => Object.hash(major, minor, patch, beta);

  @override
  String toString() => display;
}

/// The stage of the app whose version is [version]. A version that
/// does not parse counts as alpha: nothing released can carry one.
ReleaseStage releaseStageOf(String version) =>
    AraVersion.tryParse(version)?.stage ?? ReleaseStage.alpha;

/// The letter shown after the version: `a` for an alpha (`0.0.1a`), nothing
/// otherwise. A beta's `-beta.N` already says it is a beta, so it gets no `b`.
String releaseStageMarker(ReleaseStage stage) =>
    stage == ReleaseStage.alpha ? 'a' : '';

/// The version as the app shows it: `0.0.1a`, `1.0.0-beta.1`, `1.0.0`.
/// [version] has no build number (usually [kAraAppVersion]); one that does
/// not parse is shown as is.
String displayVersion(String version) {
  final v = AraVersion.tryParse(version);
  if (v == null) {
    return version;
  }
  return '${v.display}${releaseStageMarker(v.stage)}';
}

/// True when the app and the server come from different X.Y release lines
/// and should be updated together. False when either version is missing or
/// does not parse: an old daemon says `1.0.0.0`, and a guess is worse than
/// saying nothing.
bool versionsMismatch(String? appVersion, String? serverVersion) =>
    mismatchedVersions(appVersion, serverVersion) != null;

/// Both versions, parsed, when they come from different X.Y release lines;
/// null otherwise (same line, missing, or does not parse). The one copy of the
/// rule: [versionsMismatch] and the warning widget both use it.
({AraVersion app, AraVersion server})? mismatchedVersions(
  String? appVersion,
  String? serverVersion,
) {
  final app = AraVersion.tryParse(appVersion);
  final server = AraVersion.tryParse(serverVersion);
  if (app == null || server == null || app.sameLine(server)) {
    return null;
  }
  return (app: app, server: server);
}
