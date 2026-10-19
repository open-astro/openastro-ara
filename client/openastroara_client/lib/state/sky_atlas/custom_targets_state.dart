import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/custom_targets_service.dart';
import '../../services/profile_api.dart';
import '../profile_management_state.dart';

final customTargetsServiceProvider =
    Provider<CustomTargetsService>((ref) => CustomTargetsService());

/// The recent and saved targets (#1267 item 5). The profile is the source of
/// truth when a server is active: on build the profile's list is read and
/// merged with the local mirror (an entry only the mirror has, typed while
/// offline, is pushed), and every remember / forget writes the whole list
/// through `PUT /profile/saved-targets`. The local file stays the offline
/// mirror, so at the dark site with no server the list still works and the
/// next connect pushes what changed. Pure merge in [mergeSavedTargets].
class CustomTargetsNotifier extends AsyncNotifier<List<CustomTarget>> {
  @override
  Future<List<CustomTarget>> build() async {
    final mirror = await ref.watch(customTargetsServiceProvider).load();
    final api = ref.watch(profileApiProvider);
    if (api == null) return mirror;
    return _sync(api, mirror);
  }

  Future<List<CustomTarget>> _sync(
      ProfileApi api, List<CustomTarget> mirror) async {
    List<CustomTarget> remote;
    try {
      remote = await api.getSavedTargets();
    } catch (e) {
      debugPrint('[custom-targets] profile read failed, using the mirror: $e');
      return mirror;
    }
    final merged = mergeSavedTargets(remote: remote, mirror: mirror);
    final svc = ref.read(customTargetsServiceProvider);
    if (!_sameList(merged, remote)) {
      try {
        remote = await api.putSavedTargets(merged);
      } catch (e) {
        debugPrint('[custom-targets] profile push failed, kept locally: $e');
        await svc.replaceAll(merged);
        return merged;
      }
    }
    await svc.replaceAll(remote);
    return remote;
  }

  Future<void> remember(CustomTarget t) async {
    final list = await ref.read(customTargetsServiceProvider).remember(t);
    if (ref.mounted) state = AsyncData(list);
    await _push(list);
  }

  Future<void> forget(CustomTarget t) async {
    final list = await ref.read(customTargetsServiceProvider).forget(t);
    if (ref.mounted) state = AsyncData(list);
    await _push(list);
  }

  Future<void> _push(List<CustomTarget> list) async {
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    try {
      final echoed = await api.putSavedTargets(list);
      if (!ref.mounted) return;
      if (!_sameList(echoed, list)) {
        await ref.read(customTargetsServiceProvider).replaceAll(echoed);
        if (ref.mounted) state = AsyncData(echoed);
      }
    } catch (e) {
      // Offline or an older daemon: the mirror holds it; the next build pushes.
      debugPrint('[custom-targets] profile push failed, kept locally: $e');
    }
  }

  static bool _sameList(List<CustomTarget> a, List<CustomTarget> b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (!sameSavedTarget(a[i], b[i])) return false;
    }
    return true;
  }
}

/// Merge the profile's list with the local mirror: the profile wins for a
/// name both have; a name only the mirror has (typed while offline) joins;
/// newest first, capped like the mirror. Pure; unit-tested.
List<CustomTarget> mergeSavedTargets({
  required List<CustomTarget> remote,
  required List<CustomTarget> mirror,
}) {
  String key(CustomTarget t) => t.name.trim().toLowerCase();
  final byKey = <String, CustomTarget>{for (final t in remote) key(t): t};
  for (final t in mirror) {
    byKey.putIfAbsent(key(t), () => t);
  }
  final out = byKey.values.toList()
    ..sort((a, b) => b.savedUtc.compareTo(a.savedUtc));
  return out.take(CustomTargetsService.maxEntries).toList();
}

bool sameSavedTarget(CustomTarget a, CustomTarget b) =>
    a.name == b.name &&
    a.raDeg == b.raDeg &&
    a.decDeg == b.decDeg &&
    a.source == b.source &&
    a.typedAsJNow == b.typedAsJNow &&
    listEquals(a.aliases, b.aliases) &&
    a.savedUtc.toUtc() == b.savedUtc.toUtc();

final customTargetsProvider =
    AsyncNotifierProvider<CustomTargetsNotifier, List<CustomTarget>>(
        CustomTargetsNotifier.new);
