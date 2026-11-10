import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import 'simple_polar_panel.dart';
import 'simple_screen.dart';

/// Polar align: the routine's settings, its shutter, exposure and Loop/Single
/// on the chip.
class SimplePolarScreen extends SimpleScreen {
  const SimplePolarScreen();

  @override
  Widget panel(SimpleScreenHost host) => const SimplePolarPanel();

  @override
  Widget get shutter => const SimplePolarShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simplePolarSummary(
    ref.watch(simplePolarSettingsProvider).value ?? const PolarAlignSettings(),
    context.l10n,
  );
}
