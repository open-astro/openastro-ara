import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/state/equipment/mount_state.dart';

/// The telescope's status as the daemon sends it: connected, able to find
/// Home, at Home and idle unless told otherwise.
Map<String, dynamic> mountWire({
  bool atHome = true,
  bool canFindHome = true,
  bool parked = false,
  String runtime = 'idle',
  String state = 'connected',
}) => {
  'device_id': 'm',
  'name': 'Test mount',
  'state': state,
  'capabilities': {
    'can_find_home': canFindHome,
    'can_park': true,
    'can_unpark': true,
  },
  'runtime': {'state': runtime, 'at_home': atHome, 'parked': parked},
};

/// A mount for a widget test, with no HTTP: it reports [wire] (null: no mount
/// on the rig), records what it was asked in [calls], and refuses each ask
/// with [refuse] when set.
class FakeMount extends MountNotifier {
  FakeMount([this.wire]);

  /// At Home, and able to find it.
  FakeMount.atHome() : wire = mountWire();

  /// Able to find Home, and away from it.
  FakeMount.awayFromHome() : wire = mountWire(atHome: false);

  final Map<String, dynamic>? wire;
  final calls = <String>[];
  Object? refuse;

  @override
  Future<MountStatus?> build() async =>
      wire == null ? null : MountStatus.fromJson(wire!);

  /// The mount reports [next] from now on.
  void report(Map<String, dynamic> next) =>
      state = AsyncData(MountStatus.fromJson(next));

  Future<bool> _ask(String call) async {
    calls.add(call);
    if (refuse case final e?) throw e;
    return true;
  }

  @override
  Future<bool> findHome() => _ask('home');

  @override
  Future<bool> abortSlew() => _ask('abort');

  @override
  Future<bool> unpark() => _ask('unpark');
}
