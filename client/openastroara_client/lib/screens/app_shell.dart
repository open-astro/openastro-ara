import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../theme/ara_colors.dart';
import '../widgets/equipment_chip.dart';
import '../widgets/status_indicator.dart';
import 'tabs/framing_tab.dart';
import 'tabs/imaging_tab.dart';
import 'tabs/options_tab.dart';
import 'tabs/sequencer_tab.dart';
import 'tabs/sky_atlas_tab.dart';

/// Main app shell — replaces the first-run screen once a server is saved
/// (playbook §25 layout: nav rail on left, top equipment bar, center
/// workspace, bottom status). Per-area content lives in the 5 tab widgets.
/// Equipment chips along the top are placeholder until Phase 12c wires the
/// chooser bottom-sheet + Alpaca discovery flow per §25.3.
class AppShell extends ConsumerStatefulWidget {
  const AppShell({super.key});

  @override
  ConsumerState<AppShell> createState() => _AppShellState();
}

class _AppShellState extends ConsumerState<AppShell> {
  int _selectedTab = 0;

  static const _tabs = <_TabSpec>[
    _TabSpec(icon: Icons.camera_alt, label: 'Imaging', body: ImagingTab()),
    _TabSpec(icon: Icons.crop_free, label: 'Framing', body: FramingTab()),
    _TabSpec(icon: Icons.list_alt, label: 'Sequencer', body: SequencerTab()),
    _TabSpec(icon: Icons.public, label: 'Sky Atlas', body: SkyAtlasTab()),
    _TabSpec(icon: Icons.settings, label: 'Options', body: OptionsTab()),
  ];

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: Column(
          children: [
            const _TopEquipmentBar(),
            Expanded(
              child: Row(
                children: [
                  NavigationRail(
                    selectedIndex: _selectedTab,
                    onDestinationSelected: (i) => setState(() => _selectedTab = i),
                    labelType: NavigationRailLabelType.all,
                    destinations: [
                      for (final t in _tabs)
                        NavigationRailDestination(
                          icon: Icon(t.icon),
                          label: Text(t.label),
                        ),
                    ],
                  ),
                  const VerticalDivider(width: 1, thickness: 1),
                  Expanded(child: _tabs[_selectedTab].body),
                ],
              ),
            ),
            const _BottomStatusBar(),
          ],
        ),
      ),
    );
  }
}

class _TabSpec {
  final IconData icon;
  final String label;
  final Widget body;
  const _TabSpec({required this.icon, required this.label, required this.body});
}

class _TopEquipmentBar extends StatelessWidget {
  const _TopEquipmentBar();

  // Per §25.3 device-type order. Each chip is disconnected until Phase 12c
  // wires the Alpaca chooser + connect flow.
  static const _chips = <(IconData, String)>[
    (Icons.camera_alt, 'CAM'),
    (Icons.filter_alt, 'FW'),
    (Icons.adjust, 'FOC'),
    (Icons.public, 'MOUNT'),
    (Icons.rotate_right, 'ROT'),
    (Icons.gps_fixed, 'GUIDE'),
    (Icons.wb_sunny, 'FLAT'),
    (Icons.power, 'SW'),
    (Icons.cloud_outlined, 'WX'),
    (Icons.shield_outlined, 'SAFE'),
    (Icons.home_outlined, 'DOME'),
  ];

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 64,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: 8),
        child: Row(
          children: [
            for (final (icon, label) in _chips)
              EquipmentChip(
                icon: icon,
                label: label,
                status: StatusLevel.disconnected,
              ),
          ],
        ),
      ),
    );
  }
}

class _BottomStatusBar extends StatelessWidget {
  const _BottomStatusBar();

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 28,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          const SizedBox(width: 8),
          const StatusIndicator(
            level: StatusLevel.disconnected,
            label: 'Disconnected',
          ),
          const Spacer(),
          // Bug-report entry (§54) — wired in a Phase 12a follow-up.
          IconButton(
            icon: const Icon(Icons.help_outline, size: 18),
            tooltip: 'Help / Report a bug (§54)',
            onPressed: () {},
          ),
          const SizedBox(width: 4),
        ],
      ),
    );
  }
}
