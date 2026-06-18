import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../state/sky_atlas/data_manager_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_form_kit.dart';

/// Human-readable size for a package (`1.2 GB`, `340 MB`, `512 KB`).
String formatBytes(int bytes) {
  if (bytes >= 1 << 30) return '${(bytes / (1 << 30)).toStringAsFixed(1)} GB';
  if (bytes >= 1 << 20) return '${(bytes / (1 << 20)).toStringAsFixed(0)} MB';
  if (bytes >= 1 << 10) return '${(bytes / (1 << 10)).toStringAsFixed(0)} KB';
  return '$bytes B';
}

// ── Screen 17 — Sky data downloads ──────────────────────────────────────────

/// §37.6 — optional sky-data packages (star catalogs, target lists) to fetch.
/// The user ticks what they want; the selected ids are queued for download (via
/// the Data Manager) after the profile is saved — see the wizard shell. The set
/// of selected ids lives on the draft so it survives back/forward navigation.
class ScreenSkyData extends ConsumerStatefulWidget {
  const ScreenSkyData({super.key});
  @override
  ConsumerState<ScreenSkyData> createState() => _ScreenSkyDataState();
}

class _ScreenSkyDataState extends ConsumerState<ScreenSkyData> {
  Set<String> get _selected => _draftOf(ref).skyDataDownloadIds;

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(dataManagerPackagesProvider);
    return WizardScreenScaffold(
      step: 17,
      intro: 'Optional sky-data downloads — star catalogs and target lists. Tick '
          'any you want and they download in the background after you finish. You '
          'can manage the full catalog later in Settings → Data.',
      children: [
        async.when(
          loading: () => const Padding(
            padding: EdgeInsets.symmetric(vertical: 32),
            child: Center(child: CircularProgressIndicator()),
          ),
          error: (e, _) => _Message(
            'Couldn\'t load the sky-data catalog. You can add packages later in '
            'Settings → Data.',
          ),
          data: (packages) {
            if (packages == null) {
              return _Message(
                  'Connect to a daemon to see available sky-data packages.');
            }
            final available =
                packages.where((p) => !p.isInstalled).toList(growable: false);
            if (available.isEmpty) {
              return _Message('All sky-data packages are already installed.');
            }
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    TextButton(
                      onPressed: () => setState(() =>
                          _selected.addAll(available.map((p) => p.id))),
                      child: const Text('Select all'),
                    ),
                    TextButton(
                      onPressed: _selected.isEmpty
                          ? null
                          : () => setState(_selected.clear),
                      child: const Text('Clear'),
                    ),
                  ],
                ),
                ...available.map((p) => CheckboxListTile(
                      value: _selected.contains(p.id),
                      onChanged: (v) => setState(() => v == true
                          ? _selected.add(p.id)
                          : _selected.remove(p.id)),
                      title: Text(p.name),
                      subtitle: Text(
                        '${p.description}  ·  ${formatBytes(p.sizeBytes)}',
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(color: AraColors.textSecondary),
                      ),
                      controlAffinity: ListTileControlAffinity.leading,
                      contentPadding: EdgeInsets.zero,
                    )),
              ],
            );
          },
        ),
      ],
    );
  }
}

ProfileDraft _draftOf(WidgetRef ref) =>
    ref.read(wizardControllerProvider).draft;

class _Message extends StatelessWidget {
  const _Message(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 24),
        child: Text(text,
            style: const TextStyle(color: AraColors.textSecondary)),
      );
}
