import 'package:flutter/material.dart';

import '../../widgets/alpacabridge_setup/alpacabridge_setup_panel.dart';

/// #1580 — Simple Mode's Devices section with no device connected: the same add-your-devices
/// panel the setup wizard shows (ADR 0009: one flow, two shells).
class SimpleDevicesEmpty extends StatelessWidget {
  const SimpleDevicesEmpty({super.key});

  @override
  Widget build(BuildContext context) => const AlpacaBridgeSetupPanel();
}
