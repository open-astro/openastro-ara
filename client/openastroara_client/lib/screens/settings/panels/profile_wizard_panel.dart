import 'package:flutter/material.dart';

import '../../../screens/wizard/wizard_shell.dart';
import '../../../theme/ara_colors.dart';

/// §37 Re-run profile wizard panel. 12h.2 wires the launcher — the wizard
/// shell itself already exists from Phase 12b.
class ProfileWizardPanel extends StatelessWidget {
  const ProfileWizardPanel({super.key});

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
              Text('Re-run profile wizard',
                  style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: 8),
              Text(
                'Walks through the §37 18-screen / 7-stage wizard again, '
                'starting from the current profile values as defaults. '
                'Useful after swapping equipment or moving site.',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    ),
              ),
              const SizedBox(height: 24),
              FilledButton.icon(
                onPressed: () {
                  Navigator.of(context).push(
                    MaterialPageRoute(
                      builder: (_) => const WizardShell(),
                      fullscreenDialog: true,
                    ),
                  );
                },
                icon: const Icon(Icons.play_arrow),
                label: const Text('Open wizard'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
