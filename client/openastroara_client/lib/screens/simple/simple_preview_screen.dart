import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/focus/guide_focus_state.dart';
import 'simple_preview_panel.dart';
import 'simple_screen.dart';

/// Preview: the camera's settings, its shutter, and "5 s · 100" on the chip.
class SimplePreviewScreen extends SimpleScreen {
  const SimplePreviewScreen();

  @override
  Widget panel(SimpleScreenHost host) =>
      const SizedBox(width: 280, child: SimpleSettingsPopover());

  @override
  Widget get shutter => const SimpleShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) =>
      simpleSettingsSummary(context, ref);

  /// On the guide camera, the frames the guider gives it.
  @override
  Widget? frame(WidgetRef ref) {
    if (!simplePreviewGuide(ref)) return null;
    final bytes = ref.watch(guideFocusProvider.select((l) => l.frame));
    return bytes == null ? null : simpleScreenFrame(bytes);
  }
}
