import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §30.1 launch-sequence gate — false until the user clicks [Image] on the
/// launch profile box, at which point the root router swaps in the main shell.
///
/// Deliberately session-scoped (not persisted): §30.3 wants the profile box on
/// EVERY launch (pre-selecting the last-used profile), so the gate must reset
/// each cold start. In-session it only re-arms through the shell's explicit
/// "Launchpad" action ([ProfileGateNotifier.reset]).
final profileGatePassedProvider =
    NotifierProvider<ProfileGateNotifier, bool>(ProfileGateNotifier.new);

/// Riverpod 3.x removed StateProvider, so the gate flip is a Notifier.
class ProfileGateNotifier extends Notifier<bool> {
  @override
  bool build() => false;

  void pass() => state = true;

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
