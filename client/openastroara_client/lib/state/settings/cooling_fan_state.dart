import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;

import '../../models/switch_device.dart';
import '../../services/simple_mode_prefs_service.dart';
import '../equipment/camera_state.dart';
import '../equipment/switch_state.dart';
import '../profile_management_state.dart';

/// #1700 — the profile's designated cooling fan. A value only once the daemon
/// has answered, so "none" is never read off a load still in flight, and [choose]
/// keeps the old value until the daemon has the new one.
class CoolingFanNotifier extends AsyncNotifier<CoolingFan> {
  @override
  Future<CoolingFan> build() async {
    final api = ref.watch(profileApiProvider);
    // Another profile, another fan.
    ref.watch(profileManagementProvider.select((p) => p.value?.activeId));
    return api == null ? CoolingFan.none : api.getCoolingFan();
  }

  /// Throws when the daemon didn't take it, for the caller to say so: a fan
  /// the user thinks is set must really be.
  Future<void> choose(CoolingFan fan) async {
    final api = ref.read(profileApiProvider);
    if (api == null) throw StateError('no rig connected');
    state = AsyncData(await api.putCoolingFan(fan));
  }
}

final coolingFanProvider =
    AsyncNotifierProvider<CoolingFanNotifier, CoolingFan>(
      CoolingFanNotifier.new,
    );

/// A connected cooling camera and a switch, and no fan chosen: the user has no
/// interlock and must be told (#1700). Watches the fan last, so a rig that
/// isn't cooling never reads it.
final coolingFanAskDueProvider = Provider<bool>((ref) {
  final camera = ref.watch(cameraStatusProvider).value;
  if (camera == null ||
      !camera.isConnected ||
      camera.capabilities?.hasCooler != true) {
    return false;
  }
  final switches =
      ref.watch(switchListProvider).value ?? const <SwitchDevice>[];
  if (!switches.any((d) => d.isConnected && d.ports.any((p) => p.canWrite))) {
    return false;
  }
  return ref.watch(coolingFanProvider).value?.isNone ?? false;
});

/// Per profile, so a new profile is asked too; a file per profile because the
/// prefs service holds one flag.
final coolingFanAskedPrefsProvider =
    Provider.family<SimpleModePrefsService, String>(
      (ref, profileId) =>
          SimpleModePrefsService(fileName: 'cooling_fan_asked_$profileId.json'),
    );

/// True the first time for this profile, then false: the prompt asks once.
Future<bool> claimCoolingFanAsk(
  T Function<T>(ProviderListenable<T>) read,
) async {
  try {
    final id = (await read(profileManagementProvider.future)).activeId;
    if (id == null) return false;
    final prefs = read(coolingFanAskedPrefsProvider(id));
    if (await prefs.load()) return false;
    await prefs.save(true);
    return true;
  } catch (_) {
    return false; // no list to key on (no rig): nothing to ask about either
  }
}
