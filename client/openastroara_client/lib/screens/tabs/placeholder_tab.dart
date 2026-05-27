import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Shared placeholder body for tabs whose real content lands in later Phase
/// 12 sub-PRs. Renders an icon + title + a short note pointing at the
/// playbook section that will fill it in.
class PlaceholderTab extends StatelessWidget {
  final String title;
  final IconData icon;
  final String description;

  const PlaceholderTab({
    super.key,
    required this.title,
    required this.icon,
    required this.description,
  });

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 64, color: AraColors.textDisabled),
            const SizedBox(height: 16),
            Text(title, style: Theme.of(context).textTheme.headlineSmall),
            const SizedBox(height: 12),
            ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 480),
              child: Text(
                description,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
