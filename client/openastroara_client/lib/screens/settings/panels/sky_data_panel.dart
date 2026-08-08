import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';
import '../../../widgets/sky_atlas/data_manager_modal.dart';

/// §36 System → Sky data panel (was "Sky Atlas → Data Manager" pre-Stellarium:
/// the embedded planetarium renders its own sky now, so what remains here is
/// COMPUTE data — the catalogs planning and plate solving rank/match against,
/// not atlas imagery). The manager is embedded directly — no modal hop.
class SkyDataPanel extends StatelessWidget {
  const SkyDataPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Sky data — catalogs for planning + solving',
            style: Theme.of(context).textTheme.headlineSmall,
          ),
          const SizedBox(height: 8),
          Text(
            'Star and object catalogues Ara plans and solves with — the '
            'planetarium itself needs no downloads (Stellarium renders its '
            'own sky). Everything below ships pre-installed with the rig '
            'server, so nothing here is required at a remote site; download '
            'buttons fetch updates or restore a removed catalog.',
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary),
          ),
          const SizedBox(height: 16),
          const Expanded(child: DataManagerView()),
        ],
      ),
    );
  }
}
