import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/guider_calibration_view.dart';
import '../models/server.dart';
import '../services/simple_mode_prefs_service.dart';
import 'guider/guider_live_state.dart' show guiderLiveApiProvider;
import 'guider/guider_state.dart';
import 'profile_management_state.dart';
import 'saved_server_state.dart';
import 'sequencer/rig_capabilities_state.dart' show guiderConfigured;
import 'settings/optics_settings_state.dart';
import 'settings/phd2_settings_state.dart';
import 'settings/site_settings_state.dart';
import 'ws/ws_providers.dart' show serverLinkUpProvider;

/// #1562 — what Simple Mode's first-time Rig setup sheet can ask for, in the
/// order it lists them.
enum RigSetupGap { site, mainOptics, guideScope, guiderCalibration }

/// The rig facts [rigSetupGaps] reads: a small value, so the rules are tested
/// without a rig.
class RigSetupFacts {
  const RigSetupFacts({
    required this.latitudeDeg,
    required this.longitudeDeg,
    required this.opticsConfigured,
    this.hasGuider = false,
    this.guideFocalLengthMm = 0,
    this.guidePixelSizeUm = 0,
    this.guiderConnected = false,
    this.calibrationHasData = false,
    this.calibrationVerdict = 'redo',
  });

  final double latitudeDeg;
  final double longitudeDeg;

  /// The optics settings' `isConfigured`: focal length, reducer, sensor
  /// width, sensor height and pixel size.
  final bool opticsConfigured;

  /// The rig has a Guider, connected or not ([guiderConfigured]): the daemon
  /// reports one, or the profile names its guide camera.
  final bool hasGuider;
  final int guideFocalLengthMm;
  final double guidePixelSizeUm;
  final bool guiderConnected;

  /// The Guider's calibration view holds stored data, and Ara's verdict on it
  /// (good | check | redo). Read only while the Guider is connected.
  final bool calibrationHasData;
  final String calibrationVerdict;

  /// The Guider calibration line cannot be judged: the Guider is not connected.
  bool get calibrationUnchecked => hasGuider && !guiderConnected;
}

/// #1562 — the missing items, in list order. The site is missing at exactly
/// 0,0 (the site settings' defaults). Guider items apply only with a Guider in
/// the profile. A calibration is missing when the Guider is connected and has
/// no stored data or Ara says `redo`; with the Guider not connected it stays on
/// the list as "connect to check" ([RigSetupFacts.calibrationUnchecked]), so
/// the sheet still shows and does not close.
List<RigSetupGap> rigSetupGaps(RigSetupFacts f) => [
  if (f.latitudeDeg == 0 && f.longitudeDeg == 0) RigSetupGap.site,
  if (!f.opticsConfigured) RigSetupGap.mainOptics,
  if (f.hasGuider && (f.guideFocalLengthMm <= 0 || f.guidePixelSizeUm <= 0))
    RigSetupGap.guideScope,
  if (f.hasGuider &&
      (!f.guiderConnected ||
          !f.calibrationHasData ||
          f.calibrationVerdict == 'redo'))
    RigSetupGap.guiderCalibration,
];

/// The profile's site, optics and guide values are read from the rig once the
/// link is up, so the defaults they start with are never mistaken for a gap.
/// False while that read has not finished or failed; a failed read is tried
/// again the next time the link comes up.
final rigSetupHydratedProvider = FutureProvider.autoDispose<bool>((ref) async {
  final api = ref.watch(profileApiProvider);
  if (api == null) return false;
  try {
    // Read here, not through the planner's bootstrap: it swallows a failed read.
    await Future.wait([
      ref.read(siteSettingsProvider.notifier).hydrateFromServer(api),
      ref.read(opticsSettingsProvider.notifier).hydrateFromServer(api),
      ref.read(phd2SettingsProvider.notifier).hydrateFromServer(api),
    ]);
    // Kept until the rig changes: a read when a dropped link comes back would
    // undo a Settings edit not saved yet.
    if (ref.mounted) ref.keepAlive();
    return true;
  } catch (_) {
    return false;
  }
});

/// The Guider's stored calibration, read when the Guider connects and again on
/// each runtime change (a calibration ends in one). Null: not connected or no
/// way to read it.
final rigSetupCalibrationProvider =
    FutureProvider.autoDispose<GuiderCalibrationView?>((ref) async {
      final connected = ref.watch(
        guiderStatusProvider.select((s) => s.value?.isConnected ?? false),
      );
      if (!connected) return null;
      ref.watch(guiderStatusProvider.select((s) => s.value?.runtimeState));
      return ref.watch(guiderLiveApiProvider)?.getCalibration();
    });

/// The facts, or null while they cannot be trusted: the link is down, the
/// profile has not hydrated, or the connected Guider's calibration is still
/// being read. A failed calibration read is not held against the rig.
final rigSetupFactsProvider = Provider.autoDispose<RigSetupFacts?>((ref) {
  if (!ref.watch(serverLinkUpProvider)) return null;
  if (!(ref.watch(rigSetupHydratedProvider).value ?? false)) return null;
  final site = ref.watch(siteSettingsProvider);
  final optics = ref.watch(opticsSettingsProvider);
  final guide = ref.watch(phd2SettingsProvider);
  final guider = ref.watch(guiderStatusProvider).value;
  final connected = guider?.isConnected ?? false;
  GuiderCalibrationView? calibration;
  if (connected) {
    final read = ref.watch(rigSetupCalibrationProvider);
    if (read.isLoading && !read.hasValue) return null;
    calibration = read.value;
  }
  return RigSetupFacts(
    latitudeDeg: site.latitudeDeg,
    longitudeDeg: site.longitudeDeg,
    opticsConfigured: optics.isConfigured,
    hasGuider: guiderConfigured(guider, guide),
    guideFocalLengthMm: guide.guideFocalLength,
    guidePixelSizeUm: guide.guidePixelSize,
    guiderConnected: connected,
    // A failed read counts as fine: never nag about what could not be checked.
    calibrationHasData: calibration == null || calibration.data != null,
    calibrationVerdict: calibration?.verdict ?? 'good',
  );
});

/// What the sheet lists and the Settings row watches; empty while the facts
/// are not trusted.
final rigSetupGapsProvider = Provider.autoDispose<List<RigSetupGap>>((ref) {
  final facts = ref.watch(rigSetupFactsProvider);
  return facts == null ? const [] : rigSetupGaps(facts);
});

/// Whether the sheet shows. The Settings row sets it, the shell's auto-open
/// sets it, Not now and the auto-close clear it.
class RigSetupSheetController extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool open) => state = open;
}

final rigSetupSheetProvider = NotifierProvider<RigSetupSheetController, bool>(
  RigSetupSheetController.new,
);

/// The rig's identity for the dismissed flag: the daemon's uuid when the path
/// that found it could say, else its address. Null without a server.
String? rigSetupRigKey(AraServer? server) {
  if (server == null) return null;
  final raw = server.uuid ?? '${server.hostname}-${server.port}';
  return raw.replaceAll(RegExp(r'[^A-Za-z0-9_-]'), '_');
}

/// One prefs file per rig (the prefs service holds one flag), on this device.
// shortcut: device-local, not synced across devices; move to the synced
// preferences once #1644 lands.
final rigSetupDismissedPrefsProvider =
    Provider.family<SimpleModePrefsService, String>(
      (ref, rigKey) =>
          SimpleModePrefsService(fileName: 'rig_setup_dismissed_$rigKey.json'),
    );

/// "Not now" on this rig, on this device.
class RigSetupDismissedController extends AsyncNotifier<bool> {
  @override
  Future<bool> build() async {
    final key = rigSetupRigKey(ref.watch(activeServerProvider));
    if (key == null) return false;
    return ref.read(rigSetupDismissedPrefsProvider(key)).load();
  }

  Future<void> dismiss() async {
    final key = rigSetupRigKey(ref.read(activeServerProvider));
    state = const AsyncData(true);
    if (key != null) {
      await ref.read(rigSetupDismissedPrefsProvider(key)).save(true);
    }
  }
}

final rigSetupDismissedProvider =
    AsyncNotifierProvider<RigSetupDismissedController, bool>(
      RigSetupDismissedController.new,
    );
