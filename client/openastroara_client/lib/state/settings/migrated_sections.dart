import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../profile_management_state.dart';
import '../saved_server_state.dart';
import '../../models/server.dart';
import 'storage_settings_state.dart';

/// ADR 0015 — the one reset point: a different rig or a different active
/// profile sends every migrated section back to not loaded, so no value from
/// the old rig or profile can be saved over the new one. Add each newly
/// migrated section to [resetMigratedSections]; panels do not reset on their own.
void resetMigratedSections(Ref ref) {
  ref.read(storageSettingsProvider.notifier).reset();
}

/// Root-mounted (main.dart) next to `launchGateRearmProvider`.
final migratedSectionsResetProvider = Provider<void>((ref) {
  ref.listen<AraServer?>(activeServerProvider, (previous, next) {
    if (previous != null &&
        (previous.hostname != next?.hostname || previous.port != next?.port)) {
      resetMigratedSections(ref);
    }
  });
  ref.listen<String?>(
    profileManagementProvider.select((a) => a.asData?.value.activeId),
    (previous, next) {
      if (previous != null && previous != next) resetMigratedSections(ref);
    },
  );
});
