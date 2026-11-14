import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/focus/guide_focus_state.dart';
import '../../state/guider/guider_live_state.dart';
import '../../state/simple_preflight_state.dart';
import '../../state/simple_messages_state.dart';
import 'simple_preview_panel.dart';
import 'simple_screen.dart';

/// Preview: the camera's settings, its shutter, and "5 s · 100" on the chip.
class SimplePreviewScreen extends SimpleScreen {
  const SimplePreviewScreen();

  @override
  SimpleModeScreen get id => SimpleModeScreen.preview;

  @override
  SimpleStatusOrigin get origin => SimpleStatusOrigin.preview;

  @override
  Widget panel(SimpleScreenHost host) =>
      const SizedBox(width: 280, child: SimpleSettingsPopover());

  @override
  Widget get shutter => const SimpleShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) =>
      simpleSettingsSummary(context, ref);

  /// On the guide camera, the guider's looping frames (#1875 slice 3), or a
  /// guide-camera focus loop's found running.
  @override
  Widget? frame(WidgetRef ref) {
    if (!simplePreviewGuide(ref)) return null;
    final bytes = ref.watch(guideFocusProvider.select((l) => l.status.active))
        ? ref.watch(guideFocusProvider.select((l) => l.frame))
        : ref.watch(guiderLiveProvider.select((l) => l.frame));
    return bytes == null ? null : simpleScreenFrame(bytes);
  }
}
