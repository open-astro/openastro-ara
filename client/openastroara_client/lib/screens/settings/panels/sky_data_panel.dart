import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';
import '../../../widgets/sky_atlas/data_manager_modal.dart';

/// §36 System → Sky data panel (was "Sky Atlas → Data Manager" pre-Stellarium:
/// the embedded planetarium renders its own sky now, so what remains here is
/// COMPUTE data — the catalogs planning and plate solving rank/match against,
/// not atlas imagery). Embeds a launcher + a one-paragraph orientation for the
/// §36.2 manager modal.
class SkyDataPanel extends StatelessWidget {
  const SkyDataPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 520),
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Sky data — catalogs for planning + solving',
                  style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: 8),
              Text(
                'Manages the data packages the daemon computes with: the '
                'OpenNGC deep-sky catalog behind Tonight\'s Sky ranking and '
                'the HYG star catalog. The planetarium itself needs no '
                'downloads — Stellarium renders its own sky. Open the manager '
                'to browse, download, or remove packages.',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    ),
              ),
              const SizedBox(height: 24),
              FilledButton.icon(
                onPressed: () {
                  showDialog(
                    context: context,
                    builder: (_) => const DataManagerModal(),
                  );
                },
                icon: const Icon(Icons.cloud_download),
                label: const Text('Open Data Manager'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
