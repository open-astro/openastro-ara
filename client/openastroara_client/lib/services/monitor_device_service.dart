import 'dart:async';

import 'package:battery_plus/battery_plus.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:wakelock_plus/wakelock_plus.dart';

/// Keeps this device's screen awake (#1724: only while Monitor shows).
class KeepAwake {
  const KeepAwake();

  Future<void> set(bool on) async {
    try {
      await WakelockPlus.toggle(enable: on);
    } catch (_) {
      // A platform without a wake lock just dims as it always has.
    }
  }
}

final keepAwakeProvider = Provider<KeepAwake>((_) => const KeepAwake());

/// This device's battery: its level (%) and whether it is charging; null
/// where it can't be read (a desktop with no battery), so no indicator shows.
final deviceBatteryProvider = StreamProvider.autoDispose<({int level, bool charging})?>((ref) {
  final battery = Battery();
  final out = StreamController<({int level, bool charging})?>();
  Future<void> read([BatteryState? known]) async {
    try {
      final state = known ?? await battery.batteryState;
      if (state == BatteryState.unknown) return out.add(null);
      out.add((level: await battery.batteryLevel, charging: state == BatteryState.charging || state == BatteryState.full));
    } catch (_) {
      out.add(null);
    }
  }

  unawaited(read());
  final timer = Timer.periodic(const Duration(minutes: 1), (_) => read());
  StreamSubscription<BatteryState>? sub;
  try {
    sub = battery.onBatteryStateChanged.listen(read, onError: (_) {});
  } catch (_) {}
  ref.onDispose(() {
    timer.cancel();
    sub?.cancel();
    out.close();
  });
  return out.stream;
});
