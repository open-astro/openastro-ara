import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Which side of a landscape iPhone the Dynamic Island (or notch) is on.
enum NotchSide { left, right, unknown }

/// iOS reserves the same landscape margin on both sides although the island is
/// on one, and Flutter's padding can't tell which. The Runner answers with the
/// window's interface orientation over `openastroara/display`; anything it
/// can't say (no handler, portrait, another platform) is [NotchSide.unknown],
/// which keeps both margins.
class NotchSideProbe {
  static const _channel = MethodChannel('openastroara/display');

  /// Test seam.
  @visibleForTesting
  static Future<String?> Function() interfaceOrientation = query;

  static Future<String?> query() async {
    try {
      return await _channel.invokeMethod<String>('interfaceOrientation');
    } on MissingPluginException {
      return null;
    } on PlatformException {
      return null;
    }
  }

  static Future<NotchSide> side() async => sideFor(await interfaceOrientation());

  /// UIKit's `landscapeRight` turns the device's top, where the island sits,
  /// to the left; `landscapeLeft` to the right.
  static NotchSide sideFor(String? orientation) => switch (orientation) {
        'landscapeRight' => NotchSide.left,
        'landscapeLeft' => NotchSide.right,
        _ => NotchSide.unknown,
      };
}
