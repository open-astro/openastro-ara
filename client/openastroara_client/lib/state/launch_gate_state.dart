import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../models/server.dart';
import 'profile_management_state.dart';
import 'saved_server_state.dart';
import 'simple_mode_state.dart';
import 'ws/ws_providers.dart';

/// §30.1 launch-sequence gate — false until the user clicks [Image] on the
/// launch profile box, at which point the root router swaps in the main shell.
///
/// Deliberately session-scoped (not persisted): the gate resets each cold
/// start, and the first entry of a session can pass it on its own
/// ([launchAutoEnterProvider], ALP-4890). In-session it only re-arms through
/// the shell's explicit "Launchpad" action ([ProfileGateNotifier.reset]).
final profileGatePassedProvider =
    NotifierProvider<ProfileGateNotifier, bool>(ProfileGateNotifier.new);

/// Riverpod 3.x removed StateProvider, so the gate flip is a Notifier.
class ProfileGateNotifier extends Notifier<bool> {
  @override
  bool build() => false;

  /// Whether the gate has opened this session; only the first entry is
  /// automatic ([launchAutoEnterProvider]).
  bool get passedOnce => _passedOnce;
  bool _passedOnce = false;

  void pass() {
    _passedOnce = true;
    state = true;
  }

  /// Re-arm the gate — the shell's "Launchpad" button sends the user back
  /// through the §30 launch flow (e.g. to switch server or profile).
  void reset() => state = false;
}

/// §2 offline planning — true once the user chose "Plan offline" from the
/// launch flow, letting the root router enter the shell with no (reachable)
/// server. Session-scoped like the profile gate: a cold start goes through
/// the launch sequence again. The shell's "Launchpad" action clears it
/// ([OfflineModeNotifier.exit]) so the relaunched flow can pick a server.
final offlineModeProvider =
    NotifierProvider<OfflineModeNotifier, bool>(OfflineModeNotifier.new);

class OfflineModeNotifier extends Notifier<bool> {
  @override
  bool build() => false;

  void enter() => state = true;

  void exit() => state = false;
}

/// §30 — true while the user has asked to go ALL the way back to the server
/// chooser (the shell's "Launchpad" action). With servers already saved the
/// root router normally skips straight to the profile box; this flag forces
/// the FirstRunScreen instead, so Launchpad genuinely restarts the flow at
/// screen one (switch server, re-discover, plan offline). Cleared when the
/// user confirms a server there (or a cold start — session-scoped like the
/// other gates).
final serverChooserRequestedProvider =
    NotifierProvider<ServerChooserRequestNotifier, bool>(
        ServerChooserRequestNotifier.new);

class ServerChooserRequestNotifier extends Notifier<bool> {
  @override
  bool build() => false;

  void request() => state = true;

  void clear() => state = false;
}

/// #1137 — should a server change mid-session send the user back through the
/// launch flow? Yes when the shell is up against a server and the ACTIVE server
/// becomes a different one (host or port): the profile box, the cache seed and
/// the gear hydration all belong to the old rig. A first server appearing, a
/// server disappearing (offline planning keeps the shell), or the same server
/// re-saved are not switches.
bool shouldRearmLaunchGate({
  required ({String hostname, int port})? previous,
  required ({String hostname, int port})? next,
  required bool gatePassed,
  required bool offline,
}) {
  if (!gatePassed || offline) return false;
  if (previous == null || next == null) return false;
  return previous.hostname != next.hostname || previous.port != next.port;
}

/// #1137 — root-mounted (`main.dart` keeps it alive with a listen): a different
/// ACTIVE server mid-session re-arms the launch gate so the user goes back
/// through the launch flow instead of planning against the old rig's gear.
/// The decision itself is [shouldRearmLaunchGate]; this provider only wires it
/// to the live server list so it can be tested with a bare container.
final launchGateRearmProvider = Provider<void>((ref) {
  ref.listen<AraServer?>(activeServerProvider, (previous, next) {
    if (shouldRearmLaunchGate(
      previous: previous == null ? null : (hostname: previous.hostname, port: previous.port),
      next: next == null ? null : (hostname: next.hostname, port: next.port),
      gatePassed: ref.read(profileGatePassedProvider),
      offline: ref.read(offlineModeProvider),
    )) {
      ref.read(profileGatePassedProvider.notifier).reset();
    }
  });
});

/// ALP-4890 — should the launch profile box enter on its own? Once per
/// session, on the rig's active profile (the last one loaded), when the router
/// would show the box: not offline, not on the server chooser.
bool shouldAutoEnter({
  required ProfileList? list,
  required bool gatePassed,
  required bool passedOnce,
  required bool offline,
  required bool chooserRequested,
}) =>
    list != null &&
    !gatePassed &&
    !passedOnce &&
    !offline &&
    !chooserRequested &&
    list.profiles.any((p) => p.id == list.activeId);

/// ALP-4890 — root-mounted (`main.dart` keeps it alive with a listen): the
/// first time a session's profile list loads with an active profile, pass the
/// gate, so a relaunch goes from the rig pick straight to the shell. Launchpad
/// still shows the box, since the gate has passed once by then.
final launchAutoEnterProvider = Provider<void>((ref) {
  ref.listen<AsyncValue<ProfileList>>(profileManagementProvider, (_, next) {
    final gate = ref.read(profileGatePassedProvider.notifier);
    if (shouldAutoEnter(
      list: next is AsyncData<ProfileList> ? next.value : null,
      gatePassed: ref.read(profileGatePassedProvider),
      passedOnce: gate.passedOnce,
      offline: ref.read(offlineModeProvider),
      chooserRequested: ref.read(serverChooserRequestedProvider),
    )) {
      gate.pass();
    }
  }, fireImmediately: true);
});

/// #1570 — how long the full UI waits on a lost rig link before it goes back to
/// the launch profile screen. Long enough for a short Wi-Fi drop or a daemon
/// restart (the user's call, 2026-11-03).
const rigUnreachableGrace = Duration(seconds: 15);

/// #1570 — is the full UI up against a rig it cannot reach? Simple Mode shows
/// its Connect to your rig card instead (#1559), and offline planning has no
/// rig to lose.
bool rigUnreachableInFullUi({
  required bool gatePassed,
  required bool offline,
  required bool simple,
  required bool linkUp,
}) =>
    gatePassed && !offline && !simple && !linkUp;

/// #1570 — root-mounted (`main.dart` keeps it alive with a listen): after
/// [rigUnreachableGrace] of a lost link in the full UI, re-arm the launch gate.
/// The profile screen already offers Retry, Plan offline and Back to network.
/// A link back inside the grace cancels the timer, so drops never add up. A
/// running sequence keeps going on the rig either way.
final rigUnreachableReturnProvider = Provider<void>((ref) {
  Timer? timer;
  bool unreachable() => rigUnreachableInFullUi(
        gatePassed: ref.read(profileGatePassedProvider),
        offline: ref.read(offlineModeProvider),
        simple: ref.read(simpleModeActiveProvider),
        linkUp: ref.read(serverLinkUpProvider),
      );
  void update() {
    if (!unreachable()) {
      timer?.cancel();
      timer = null;
      return;
    }
    timer ??= Timer(rigUnreachableGrace, () {
      timer = null;
      if (unreachable()) ref.read(profileGatePassedProvider.notifier).reset();
    });
  }

  ref.listen(profileGatePassedProvider, (_, _) => update());
  ref.listen(offlineModeProvider, (_, _) => update());
  ref.listen(simpleModeActiveProvider, (_, _) => update());
  ref.listen(serverLinkUpProvider, (_, _) => update(), fireImmediately: true);
  ref.onDispose(() => timer?.cancel());
});
