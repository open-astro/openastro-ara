import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §30.1 launch-sequence gate — false until the user clicks [Image] on the
/// launch profile box, at which point the root router swaps in the main shell.
///
/// Deliberately session-scoped (not persisted): §30.3 wants the profile box on
/// EVERY launch (pre-selecting the last-used profile), so the gate must reset
/// each cold start. Nothing ever sets it back to false — leaving the shell
/// mid-session goes through in-app navigation, not the launch flow.
final profileGatePassedProvider =
    NotifierProvider<ProfileGateNotifier, bool>(ProfileGateNotifier.new);

/// Riverpod 3.x removed StateProvider, so the one-way gate flip is a Notifier.
class ProfileGateNotifier extends Notifier<bool> {
  @override
  bool build() => false;

  /// One-way: the launch flow only ever passes the gate, never re-arms it.
  void pass() => state = true;
}
