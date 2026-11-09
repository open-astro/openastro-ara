import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../state/settings/cooling_fan_state.dart';

/// #1700 — the full UI's one-time prompt to choose the camera's cooling fan.
/// Simple Mode ([quiet]) asks with its own pill.
class CoolingFanPromptListener extends ConsumerStatefulWidget {
  const CoolingFanPromptListener({
    super.key,
    required this.child,
    this.quiet = false,
  });

  final Widget child;
  final bool quiet;

  @override
  ConsumerState<CoolingFanPromptListener> createState() =>
      _CoolingFanPromptListenerState();
}

class _CoolingFanPromptListenerState
    extends ConsumerState<CoolingFanPromptListener> {
  @override
  void initState() {
    super.initState();
    if (widget.quiet) return;
    ref.listenManual(coolingFanAskDueProvider, (_, due) async {
      if (!due || !await claimCoolingFanAsk(ref.read) || !mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(context.l10n.coolingFanAskFullUi)));
    }, fireImmediately: true);
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
