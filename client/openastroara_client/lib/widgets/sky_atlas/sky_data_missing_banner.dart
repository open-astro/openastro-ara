import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';

/// §36.13 silent banner — shown across the app when the active profile has
/// no HiPS sky imagery downloaded yet. Dismissible per the §30.7 shared
/// banner pattern; "Open Data Manager" CTA jumps to §36.2 inline. The
/// dismiss state is session-local; the banner re-appears on next launch
/// until the user actually downloads something (matches §30.7 silent-
/// banner contract).
class SkyDataMissingBanner extends ConsumerStatefulWidget {
  final VoidCallback onOpenDataManager;
  const SkyDataMissingBanner({super.key, required this.onOpenDataManager});

  @override
  ConsumerState<SkyDataMissingBanner> createState() =>
      _SkyDataMissingBannerState();
}

class _SkyDataMissingBannerState extends ConsumerState<SkyDataMissingBanner> {
  bool _dismissed = false;

  @override
  Widget build(BuildContext context) {
    final hasImagery = ref.watch(skyImageryAvailableProvider);
    if (hasImagery || _dismissed) return const SizedBox.shrink();

    return Container(
      decoration: BoxDecoration(
        color: AraColors.accentBusy.withValues(alpha: 0.12),
        border: const Border(bottom: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          Icon(Icons.cloud_off_outlined,
              size: 18, color: AraColors.accentBusy),
          const SizedBox(width: 12),
          const Expanded(
            child: Text(
              'No sky imagery downloaded yet — Aladin will fall back to '
              'online CDS access. Download a HiPS survey to make Sky Atlas '
              'work offline.',
            ),
          ),
          TextButton(
            onPressed: widget.onOpenDataManager,
            child: const Text('Open Data Manager'),
          ),
          IconButton(
            onPressed: () => setState(() => _dismissed = true),
            icon: const Icon(Icons.close, size: 18),
            tooltip: 'Dismiss',
          ),
        ],
      ),
    );
  }
}
