import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/plate_solve_result.dart';
import '../../services/plate_solve_api.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/solve_state.dart';
import '../../state/saved_server_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/coord_format.dart';

/// §18.I — plate-solve the last captured frame and show its astrometric
/// solution (RA / Dec / rotation / scale). Sits under the frame viewer on the
/// Imaging tab; the Solve button is enabled once a frame has been captured.
class SolvePanel extends ConsumerWidget {
  const SolvePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final frameId = ref.watch(lastCapturedFrameIdProvider);
    final result = ref.watch(solveResultProvider);
    final solving = result.isLoading;
    final canSolve = frameId != null && !solving;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          FilledButton.icon(
            onPressed: canSolve ? () => _solve(context, ref, frameId) : null,
            icon: solving
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.my_location, size: 16),
            label: Text(solving ? 'Solving…' : 'Solve frame'),
          ),
          const SizedBox(width: 12),
          Expanded(child: _resultText(context, result, frameId != null)),
        ],
      ),
    );
  }

  Future<void> _solve(
      BuildContext context, WidgetRef ref, String frameId) async {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Not connected to a server.')),
      );
      return;
    }
    await ref.read(solveResultProvider.notifier).solve(server, frameId);
  }

  Widget _resultText(
      BuildContext context, AsyncValue<PlateSolveResult?> result, bool hasFrame) {
    final mono = Theme.of(context)
        .textTheme
        .bodySmall
        ?.copyWith(fontFeatures: const [FontFeature.tabularFigures()]);

    return result.when(
      loading: () => Text('Solving with ASTAP…', style: mono),
      error: (e, _) => Text(
        e is PlateSolveException ? e.message : 'Solve failed: $e',
        style: mono?.copyWith(color: AraColors.accentError),
      ),
      data: (r) {
        if (r == null) {
          return Text(
            hasFrame ? 'Solve the captured frame for its sky coordinates.'
                : 'Capture a frame, then solve it.',
            style: mono?.copyWith(color: AraColors.textSecondary),
          );
        }
        if (!r.success) {
          return Text(
            'No solution — too few stars or wrong field. Try a longer exposure.',
            style: mono?.copyWith(color: AraColors.accentBusy),
          );
        }
        return Text(
          'RA ${r.ra == null ? '—' : formatRaHms(r.ra!)}   '
          'Dec ${r.dec == null ? '—' : formatDecDms(r.dec!)}   '
          'Rot ${_deg(r.orientation)}   Scale ${_scale(r.pixelScale)}',
          style: mono,
        );
      },
    );
  }

  static String _deg(double? d) => d == null ? '—' : '${d.toStringAsFixed(1)}°';
  static String _scale(double? s) =>
      s == null ? '—' : '${s.toStringAsFixed(2)}"/px';
}
