import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';
import '../../../widgets/sky_atlas/data_manager_modal.dart';

/// §36 Sky Atlas → Data Manager panel. The full §36.2 modal already exists
/// from Phase 12e.1; this panel embeds a launcher + a one-paragraph
/// orientation. Phase 12h.2c may inline the survey list directly here.
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
              Text('Sky data — surveys, catalogs, thumbnails',
                  style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: 8),
              Text(
                'Manages local downloads of HiPS sky surveys, star catalogs '
                '(Tycho-2, GAIA DR3, UCAC4, HD, HIP), famous-target thumbnails, '
                'and solar-system ephemerides (DE440 + MPC). Open the full '
                'manager to browse and download.',
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
