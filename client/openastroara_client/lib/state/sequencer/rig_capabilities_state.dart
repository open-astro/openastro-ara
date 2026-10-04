import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../models/sequence/rig_capabilities.dart';
import '../equipment/filter_wheel_state.dart';
import '../equipment/focuser_state.dart';
import '../equipment/rotator_state.dart';
import '../guider/guider_state.dart';
import '../settings/phd2_settings_state.dart';

/// Read the rig off the daemon: a device type is "there" when the daemon
/// retains a selected device for it (its status endpoint answers, connected
/// or not — the §52.1 remembered selection), and the guider when the daemon
/// has a guider at all or the profile names a guide camera. Each read is
/// awaited so a fresh session that goes straight to Planning doesn't build a
/// run from providers that haven't loaded yet; a read that fails counts the
/// device as absent (the run then simply leaves that step out — the user can
/// add it in the editor), never as a crash.
Future<RigCapabilities> readRigCapabilities(ProviderContainer container) async {
  Future<bool> present<T>(Future<T?> Function() read, String what) async {
    try {
      return await read().timeout(const Duration(seconds: 8)) != null;
    } catch (e) {
      debugPrint(
        '[planning] rig read for $what failed (treated as absent): $e',
      );
      return false;
    }
  }

  final focuser = present(
    () => container.read(focuserProvider.future),
    'focuser',
  );
  final wheel = present(
    () => container.read(filterWheelProvider.future),
    'filter wheel',
  );
  final rotator = present(
    () => container.read(rotatorProvider.future),
    'rotator',
  );
  GuiderStatus? guider;
  try {
    guider = await container
        .read(guiderStatusProvider.future)
        .timeout(const Duration(seconds: 8));
  } catch (e) {
    debugPrint('[planning] rig read for guider failed: $e');
  }
  return RigCapabilities(
    focuser: await focuser,
    filterWheel: await wheel,
    rotator: await rotator,
    guider: guiderConfigured(guider, container.read(phd2SettingsProvider)),
  );
}

/// The rig as far as settings alone can tell — the offline planning path
/// (no daemon to ask). A guider counts when the profile names its camera;
/// focuser and filter wheel are assumed (the historical shape) and the
/// rotator is not. Pure — unit-tested.
RigCapabilities rigCapabilitiesFromSettings(Phd2Settings phd2) =>
    RigCapabilities(
      focuser: true,
      filterWheel: true,
      rotator: false,
      guider: guiderConfigured(null, phd2),
    );

/// A guider is part of the rig when the daemon reports one (whatever its link
/// state right now — a dropped link is still a guider) or the profile names
/// the guide camera (§63.17). Pure — unit-tested.
bool guiderConfigured(GuiderStatus? status, Phd2Settings phd2) =>
    status != null ||
    phd2.guiderCamera.trim().isNotEmpty ||
    phd2.guiderCameraId.trim().isNotEmpty;
