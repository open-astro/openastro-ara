import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/stellarium_server.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../help_icon.dart';

/// Files and bytes in this computer's DSS2 tile cache.
typedef SkyPhotoCacheInfo = ({int files, int bytes});

/// Measures the cache; overridden in widget tests.
final skyPhotoCacheMeasureProvider =
    Provider<Future<SkyPhotoCacheInfo> Function()>(
      (ref) => StellariumServer.measureDssCache,
    );

/// Clears the cache and returns what it removed; overridden in widget tests.
final skyPhotoCacheClearProvider =
    Provider<Future<SkyPhotoCacheInfo> Function()>(
      (ref) => StellariumServer.clearDssCache,
    );

/// Size of this computer's DSS2 sky-photo tile cache (#1143); no server
/// involved, so it reads even before a rig is connected.
final skyPhotoCacheProvider = FutureProvider.autoDispose<SkyPhotoCacheInfo>(
  (ref) => ref.watch(skyPhotoCacheMeasureProvider)(),
);

/// "Sky photos" row for Settings → Storage — the planetarium's DSS2 tile
/// cache on THIS computer, beside the server-side preview cache: size + a
/// Clean button. The cache also caps itself (oldest tiles go first past the
/// cap), so this is for a corrupted backdrop or reclaiming space before a trip.
class SkyPhotoCacheRow extends ConsumerStatefulWidget {
  const SkyPhotoCacheRow({super.key});

  static const label = 'Sky photos (this computer)';

  @override
  ConsumerState<SkyPhotoCacheRow> createState() => _SkyPhotoCacheRowState();
}

class _SkyPhotoCacheRowState extends ConsumerState<SkyPhotoCacheRow> {
  bool _cleaning = false;

  static String _size(SkyPhotoCacheInfo info) {
    final mb = info.bytes / 1e6;
    final size = mb >= 1000
        ? '${(mb / 1000).toStringAsFixed(2)} GB'
        : '${mb.toStringAsFixed(0)} MB';
    return '$size · ${info.files} file${info.files == 1 ? '' : 's'}';
  }

  Future<void> _clean() async {
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _cleaning = true);
    try {
      final freed = await ref.read(skyPhotoCacheClearProvider)();
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            freed.files == 0
                ? 'Sky photo cache was already empty.'
                : 'Cleaned the sky photo cache — freed ${_size(freed)}.',
          ),
        ),
      );
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text(friendlyError(e, action: 'clean the sky photo cache')),
        ),
      );
    } finally {
      if (mounted) setState(() => _cleaning = false);
      ref.invalidate(skyPhotoCacheProvider);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cache = ref.watch(skyPhotoCacheProvider);
    final info = cache.asData?.value;
    final String sizeText;
    if (cache.isLoading) {
      sizeText = 'Measuring…';
    } else if (info == null) {
      sizeText = 'Size unavailable';
    } else if (info.files == 0) {
      sizeText = 'Empty';
    } else {
      sizeText = _size(info);
    }
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          SizedBox(
            width: 280,
            child: Row(
              children: [
                Flexible(
                  child: Text(
                    SkyPhotoCacheRow.label,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.bodyMedium
                        ?.copyWith(color: AraColors.textSecondary),
                  ),
                ),
                const HelpIcon(helpKey: 'session.storage.sky_photo_cache'),
              ],
            ),
          ),
          Expanded(child: Text(sizeText)),
          OutlinedButton.icon(
            onPressed: _cleaning || info == null || info.files == 0
                ? null
                : _clean,
            icon: _cleaning
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.cleaning_services_outlined, size: 16),
            label: Text(_cleaning ? 'Cleaning…' : 'Clean cache'),
          ),
        ],
      ),
    );
  }
}
