import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/custom_targets_service.dart';
import '../../services/profile_api.dart';
import '../profile_management_state.dart';
import '../saved_server_state.dart';

final customTargetsServiceProvider =
    Provider<CustomTargetsService>((ref) => CustomTargetsService());

/// The mirror's scope for a server and profile.
String customTargetsScope(AraServer server, String profileId) =>
    '${server.baseUrl}|$profileId';

/// The recent and saved targets (#1267 item 5). The profile is the source of
/// truth when a server is active and its profile is known: on build the
/// profile's list is merged three ways with the scope's mirror against the
/// names held at the last sync, so a forget made on another client or while
/// offline sticks and an entry typed offline is pushed; every remember /
/// forget writes the whole list through `PUT /profile/saved-targets`. The
/// mirror is per (server, profile), so one rig's list never lands in another's.
/// Without a server (the dark site) the last scope's mirror is the list and
/// the next connect syncs. Pure merge in [threeWayMergeSavedTargets].
class CustomTargetsNotifier extends AsyncNotifier<List<CustomTarget>> {
  Future<void> _pushes = Future<void>.value();

  /// The scope this build resolved (null offline: the mirror's last scope).
  String? _scope;

  @override
  Future<List<CustomTarget>> build() async {
    final svc = ref.watch(customTargetsServiceProvider);
    final api = ref.watch(profileApiProvider);
    final server = ref.watch(activeServerProvider);
    _scope = null;
    if (api == null || server == null) return (await svc.loadScope(null)).targets;
    // The profile list is loaded asynchronously; the scope needs its active
    // id, so wait for it (and rebuild when a profile is selected).
    String? id;
    try {
      id = (await ref.watch(profileManagementProvider.future)).activeId;
    } catch (e) {
      debugPrint('[custom-targets] profile list unavailable, using the mirror: $e');
    }
    if (id == null) return (await svc.loadScope(null)).targets;
    final scope = customTargetsScope(server, id);
    _scope = scope;
    // On the same chain as the pushes: a remember made while this sync's
    // read is in flight cannot be undone by the sync's later PUT and commit.
    final task = _pushes.then((_) => _sync(api, svc, scope));
    _pushes = task.then((_) {});
    return task;
  }

  Future<List<CustomTarget>> _sync(
      ProfileApi api, CustomTargetsService svc, String scope) async {
    final local = await svc.loadScope(scope);
    List<CustomTarget> remote;
    try {
      remote = await api.getSavedTargets();
    } catch (e) {
      debugPrint('[custom-targets] profile read failed, using the mirror: $e');
      return local.targets;
    }
    final merged = threeWayMergeSavedTargets(
        remote: remote, mirror: local.targets, base: local.base);
    if (sameSavedTargets(merged, remote)) {
      return svc.commitSync(scope, remote);
    }
    try {
      final echoed = await api.putSavedTargets(merged.where(isAcceptableSavedTarget).toList());
      return await svc.commitSync(scope, echoed);
    } catch (e) {
      debugPrint('[custom-targets] profile push failed, kept locally: $e');
      return svc.replaceAll(merged, scope: scope);
    }
  }

  Future<void> remember(CustomTarget t) async {
    final scope = _scope;
    final list = await ref.read(customTargetsServiceProvider).remember(t, scope: scope);
    if (ref.mounted) state = AsyncData(list);
    await _push(scope, list);
  }

  Future<void> forget(CustomTarget t) async {
    final scope = _scope;
    final list = await ref.read(customTargetsServiceProvider).forget(t, scope: scope);
    if (ref.mounted) state = AsyncData(list);
    await _push(scope, list);
  }

  // Pushes run one after another, so a remember cannot be overwritten by an
  // earlier push's echo landing late; each one re-merges against a fresh
  // read, so a target another client added since this client's last build
  // survives the push.
  Future<void> _push(String? scope, List<CustomTarget> list) {
    final api = ref.read(profileApiProvider);
    if (api == null || scope == null) return Future.value();
    final task = _pushes.then((_) async {
      try {
        final svc = ref.read(customTargetsServiceProvider);
        final local = await svc.loadScope(scope);
        final remote = await api.getSavedTargets();
        final merged = threeWayMergeSavedTargets(
            remote: remote, mirror: local.targets, base: local.base);
        final echoed = await api.putSavedTargets(merged.where(isAcceptableSavedTarget).toList());
        if (!ref.mounted) return;
        final committed = await svc.commitSync(scope, echoed);
        if (ref.mounted) state = AsyncData(committed);
      } catch (e) {
        // Offline or an older daemon: the mirror holds it; the next build syncs.
        debugPrint('[custom-targets] profile push failed, kept locally: $e');
      }
    });
    _pushes = task;
    return task;
  }
}

/// Three-way merge of the profile's list, the scope's mirror and the names the
/// profile held at the last sync ([base]): a name both have takes the
/// profile's entry; a name only the profile has is new there unless the mirror
/// forgot it since the sync (then it goes); a name only the mirror has is new
/// here unless the profile forgot it since the sync (then it goes). Newest
/// first, capped. Pure; unit-tested.
List<CustomTarget> threeWayMergeSavedTargets({
  required List<CustomTarget> remote,
  required List<CustomTarget> mirror,
  required Set<String> base,
}) {
  final r = {for (final t in remote) CustomTargetsService.nameKey(t): t};
  final m = {for (final t in mirror) CustomTargetsService.nameKey(t): t};
  final out = <CustomTarget>[];
  for (final key in {...r.keys, ...m.keys}) {
    final inR = r[key], inM = m[key];
    if (inR != null && inM != null) {
      out.add(inR);
    } else if (inR != null) {
      if (!base.contains(key)) out.add(inR);
    } else if (inM != null) {
      if (!base.contains(key)) out.add(inM);
    }
  }
  // Newest first, then by name: a deterministic order that matches the
  // daemon's, so equal-time entries never read as a difference to push.
  out.sort((a, b) {
    final byTime = b.savedUtc.compareTo(a.savedUtc);
    // OrdinalIgnoreCase on the daemon compares upper-cased text.
    return byTime != 0 ? byTime : a.name.toUpperCase().compareTo(b.name.toUpperCase());
  });
  return out.take(CustomTargetsService.maxEntries).toList();
}

bool sameSavedTargets(List<CustomTarget> a, List<CustomTarget> b) {
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (!sameSavedTarget(a[i], b[i])) return false;
  }
  return true;
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
