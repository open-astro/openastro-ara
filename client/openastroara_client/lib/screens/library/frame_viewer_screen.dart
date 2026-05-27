import 'package:flutter/material.dart';

import '../../models/library/frame.dart';
import '../../theme/ara_colors.dart';

/// §40.5 frame viewer. Phase 12f.1 renders metadata + placeholder preview
/// + action buttons. 12f.2 swaps the placeholder for the real preview
/// + wires Rate/Tag/Open/Show in Folder/Download FITS/Delete.
class FrameViewerScreen extends StatelessWidget {
  final CapturedFrame frame;
  const FrameViewerScreen({super.key, required this.frame});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(frame.filename, style: const TextStyle(fontSize: 14)),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: 12),
            child: Row(
              children: [
                for (var i = 1; i <= 5; i++)
                  Icon(
                    i <= frame.rating ? Icons.star : Icons.star_border,
                    size: 18,
                    color: AraColors.accentBusy,
                  ),
              ],
            ),
          ),
        ],
      ),
      body: Column(
        children: [
          const Expanded(child: _PreviewArea()),
          _MetadataPanel(frame: frame),
          _ActionBar(frame: frame),
        ],
      ),
    );
  }
}

class _PreviewArea extends StatelessWidget {
  const _PreviewArea();

  @override
  Widget build(BuildContext context) {
    return Container(
      color: AraColors.bgPrimary,
      child: const Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.image_outlined, size: 96, color: AraColors.textDisabled),
            SizedBox(height: 8),
            Text(
              'Frame preview placeholder — Phase 12f.2 wires Image.network + zoom/pan',
              style: TextStyle(color: AraColors.textSecondary),
            ),
          ],
        ),
      ),
    );
  }
}

class _MetadataPanel extends StatelessWidget {
  final CapturedFrame frame;
  const _MetadataPanel({required this.frame});

  @override
  Widget build(BuildContext context) {
    final rows = <(String, String)>[
      ('Exposure', '${frame.exposure.inSeconds}s'),
      ('Gain', '${frame.gain}'),
      ('Offset', '${frame.offset}'),
      ('Filter', frame.filter),
      ('Bin', '${frame.bin}×${frame.bin}'),
      ('HFR', frame.hfr.toStringAsFixed(2)),
      ('Stars', '${frame.starCount}'),
      ('Median ADU', '${frame.medianAdu}'),
      ('Background', '${frame.backgroundAdu}'),
      ('Sensor temp', '${frame.sensorTempC.toStringAsFixed(1)}°C'),
      ('Focus', '${frame.focusSteps} steps'),
      ('Captured', frame.capturedAt.toUtc().toIso8601String()),
    ];
    return Container(
      color: AraColors.bgPanel,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      child: Wrap(
        spacing: 24,
        runSpacing: 4,
        children: rows
            .map((r) => SizedBox(
                  width: 180,
                  child: Row(children: [
                    SizedBox(
                      width: 88,
                      child: Text(r.$1,
                          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                color: AraColors.textSecondary,
                              )),
                    ),
                    Expanded(
                        child: Text(r.$2,
                            style: Theme.of(context).textTheme.bodySmall)),
                  ]),
                ))
            .toList(),
      ),
    );
  }
}

class _ActionBar extends StatelessWidget {
  final CapturedFrame frame;
  const _ActionBar({required this.frame});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      child: Wrap(
        spacing: 8,
        children: const [
          _ActionButton(icon: Icons.star_outline, label: 'Rate'),
          _ActionButton(icon: Icons.label_outline, label: 'Tag'),
          _ActionButton(icon: Icons.open_in_new, label: 'Open in App'),
          _ActionButton(icon: Icons.folder_open, label: 'Show in Folder'),
          _ActionButton(icon: Icons.download, label: 'Download FITS'),
          _ActionButton(icon: Icons.delete_outline, label: 'Delete'),
        ],
      ),
    );
  }
}

class _ActionButton extends StatelessWidget {
  final IconData icon;
  final String label;
  const _ActionButton({required this.icon, required this.label});

  @override
  Widget build(BuildContext context) {
    return TextButton.icon(
      onPressed: null,
      icon: Icon(icon, size: 16),
      label: Text(label),
    );
  }
}
