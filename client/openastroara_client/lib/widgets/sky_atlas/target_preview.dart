import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/target_preview_service.dart';
import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/target_preview_state.dart';
import '../../theme/ara_colors.dart';

/// A square DSS2 thumbnail of [object] — what it actually looks like — with
/// tap-to-enlarge. Serves the disk cache first; on a dark site with no
/// internet an uncached target shows a quiet "no preview cached" tile rather
/// than an error. Never a gate on anything.
class TargetPreview extends ConsumerWidget {
  final TonightSkyObject object;
  final double size;
  const TargetPreview({super.key, required this.object, this.size = 72});

  TargetPreviewKey get _key => (
        id: object.id,
        raDeg: object.raDeg,
        decDeg: object.decDeg,
        sizeMajArcmin: object.sizeMajArcmin,
      );

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final preview = ref.watch(targetPreviewProvider(_key));
    final fov = TargetPreviewService.fovDegFor(object.sizeMajArcmin);
    final label = '${object.name} preview, ${fov.toStringAsFixed(1)}° field';
    final Widget tile = preview.when(
      loading: () => const Center(
        child: SizedBox(
          width: 16,
          height: 16,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
      ),
      error: (_, _) => const _NoPreview(),
      data: (bytes) => bytes == null
          ? const _NoPreview()
          : Image.memory(
              bytes,
              fit: BoxFit.cover,
              gaplessPlayback: true,
              semanticLabel: label,
            ),
    );
    final bytes = preview.hasValue ? preview.value : null;
    return Semantics(
      label: label,
      button: bytes != null,
      child: InkWell(
        borderRadius: BorderRadius.circular(6),
        onTap: bytes != null ? () => _enlarge(context, bytes) : null,
        child: ClipRRect(
          borderRadius: BorderRadius.circular(6),
          child: Container(
            width: size,
            height: size,
            color: Colors.black,
            child: tile,
          ),
        ),
      ),
    );
  }

  void _enlarge(BuildContext context, Uint8List bytes) {
    final fov = TargetPreviewService.fovDegFor(object.sizeMajArcmin);
    showDialog<void>(
      context: context,
      builder: (ctx) => Dialog(
        backgroundColor: AraColors.bgPanel,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 480, maxHeight: 480),
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(6),
                  child: Image.memory(bytes, fit: BoxFit.contain),
                ),
              ),
              const SizedBox(height: 8),
              Text(object.name, style: Theme.of(ctx).textTheme.bodyMedium),
              Text(
                '${fov.toStringAsFixed(1)}° field · DSS2 colour survey '
                '(CDS hips2fits)',
                style: Theme.of(ctx)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.textSecondary),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _NoPreview extends StatelessWidget {
  const _NoPreview();

  @override
  Widget build(BuildContext context) => Tooltip(
        message:
            'No preview cached — connect to the internet once to fetch it',
        child: Center(
          child: Icon(Icons.image_not_supported_outlined,
              size: 20, color: AraColors.textDisabled),
        ),
      );
}
