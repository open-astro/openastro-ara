import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Android filters inbound multicast unless an app holds a Wi-Fi
/// MulticastLock, so mDNS answers from the rig never arrive without one
/// (#1129). Held only while the scan screen is open — discovery runs nowhere
/// else, and a lock held for a whole imaging session costs battery.
///
/// Handled by the Android runner over `openastroara/multicast_lock`; a no-op
/// on every other platform. Fails silent: without the lock discovery falls
/// back to the subnet sweep, which needs no multicast.
class MulticastLock {
  static const _channel = MethodChannel('openastroara/multicast_lock');

  /// Platform gate; a test seam.
  @visibleForTesting
  static bool Function() isAndroid = () => defaultTargetPlatform == TargetPlatform.android;

  static Future<void> acquire() => _call('acquire');

  static Future<void> release() => _call('release');

  static Future<void> _call(String method) async {
    if (!isAndroid()) return;
    try {
      await _channel.invokeMethod<void>(method);
    } on MissingPluginException {
      // An embedding without the handler: nothing to hold.
    } on PlatformException {
      // Wi-Fi service unavailable: the sweep still finds the rig.
    }
  }
}
