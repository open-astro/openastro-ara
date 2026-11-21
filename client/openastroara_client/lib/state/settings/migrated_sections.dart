import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;

import '../profile_management_state.dart';
import '../saved_server_state.dart';
import 'autofocus_settings_state.dart';
import 'camera_electronics_state.dart';
import 'cooling_fan_state.dart';
import 'custom_horizon_state.dart';
import 'diagnostics_mode_state.dart';
import 'dome_settings_state.dart';
import 'equipment_connection_state.dart';
import 'filenames_settings_state.dart';
import 'filter_set_state.dart';
import 'filter_wheel_labels_state.dart';
import 'filter_wheel_policy_state.dart';
import 'imaging_defaults_state.dart';
import 'loaded_section.dart';
import 'notifications_settings_state.dart';
import 'optics_settings_state.dart';
import 'phd2_settings_state.dart';
import 'plate_solve_settings_state.dart';
import 'polar_align_settings_state.dart';
import 'safety_policies_state.dart';
import 'site_settings_state.dart';
import 'storage_settings_state.dart';
import 'unguided_settings_state.dart';

/// ADR 0015 — the one reset point: a different rig or a different active
/// profile sends every migrated section back to not loaded, so no value from
/// the old rig or profile can be saved over the new one. Add each newly
/// migrated section to [migratedSections]; panels do not reset on their own.
///
/// [read] is a `ref.read`, so a widget's `WidgetRef` can reset after a wizard
/// save and a replay: they write sections from this device without a version
/// and outside the section notifier, so a section loaded before would keep a
/// stale tag until live sync lands (a false 412 on the next Save).
void resetMigratedSections(T Function<T>(ProviderListenable<T>) read) {
  for (final section in migratedSections) {
    read(section.provider.notifier).reset();
  }
}

typedef MigratedSection = ({
  /// The first segment after `/api/v1/profile/` that `profile.updated` names.
  String route,
  NotifierProvider<SectionNotifier<Object?>, LoadedSection<Object?>> provider,
});

/// Every section on the section module (loaded_section.dart): the one list
/// that [resetMigratedSections] and the live-sync table (profile_live_sync.dart)
/// read, and a test checks both against.
final List<MigratedSection> migratedSections = [
  (route: 'autofocus', provider: autofocusSettingsProvider),
  (route: 'camera-electronics', provider: cameraElectronicsProvider),
  (route: 'cooling-fan', provider: coolingFanProvider),
  (route: 'custom-horizon', provider: customHorizonProvider),
  (route: 'diagnostics-mode', provider: diagnosticsModeProvider),
  (route: 'dome', provider: domeSettingsProvider),
  (route: 'equipment-connection', provider: equipmentConnectionProvider),
  (route: 'filenames', provider: filenamesSettingsProvider),
  (route: 'filter-set', provider: filterSetProvider),
  (route: 'filter-wheel', provider: filterWheelLabelsProvider),
  (route: 'filter-wheel', provider: filterWheelPolicyProvider),
  (route: 'imaging-defaults', provider: imagingDefaultsProvider),
  (route: 'notifications', provider: notificationsSettingsProvider),
  (route: 'optics', provider: opticsSettingsProvider),
  (route: 'phd2', provider: phd2SettingsProvider),
  (route: 'plate-solve', provider: plateSolveSettingsProvider),
  (route: 'polar-align', provider: polarAlignSettingsProvider),
  (route: 'safety', provider: safetyPoliciesProvider),
  (route: 'site', provider: siteSettingsProvider),
  (route: 'storage', provider: storageSettingsProvider),
  (route: 'unguided', provider: unguidedSettingsProvider),
];

/// Root-mounted (main.dart) next to `launchGateRearmProvider`.
final migratedSectionsResetProvider = Provider<void>((ref) {
  // The saved list, not activeServerProvider, whose == hides same-rig updates.
  ref.listen(savedServersProvider, (before, after) {
    final previous = activeServerIn(before), next = activeServerIn(after);
    if (previous != null &&
        (previous.hostname != next?.hostname || previous.port != next?.port)) {
      resetMigratedSections(ref.read);
    }
  });
  ref.listen<String?>(
    profileManagementProvider.select((a) => a.asData?.value.activeId),
    (previous, next) {
      if (previous != null && previous != next) resetMigratedSections(ref.read);
    },
  );
});
