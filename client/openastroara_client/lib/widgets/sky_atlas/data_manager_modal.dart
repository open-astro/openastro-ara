import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/data_package.dart';
import '../../state/sky_atlas/data_manager_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Data Manager — the real sky-data package manager. Lists the curated
/// catalog from the active server (`/api/v1/data-manager/*`), shows on-disk
/// install state and live download progress (folded from the
/// `data_manager.download.*` WS stream), and wires download / cancel / remove.
class DataManagerModal extends ConsumerStatefulWidget {
  const DataManagerModal({super.key});

  @override
  ConsumerState<DataManagerModal> createState() => _DataManagerModalState();
}

class _DataManagerModalState extends ConsumerState<DataManagerModal> {
  @override
  void initState() {
    super.initState();
    // Re-read the catalog on open so a package installed/removed elsewhere shows fresh.
    Future.microtask(() {
      if (mounted) {
        unawaited(ref.read(dataManagerPackagesProvider.notifier).refresh());
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(dataManagerPackagesProvider);
    final packages = async.asData?.value;
    final usedBytes = (packages ?? const <DataPackage>[])
        .where((p) => p.isInstalled)
        .fold<int>(0, (sum, p) => sum + p.sizeBytes);

    return Dialog.fullscreen(
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Data Manager'),
          leading: IconButton(
            icon: const Icon(Icons.close),
            onPressed: () => Navigator.of(context).pop(),
          ),
          actions: [
            if (packages != null)
              Center(
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: Text(
                    '${formatBytes(usedBytes)} installed',
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: AraColors.textSecondary),
                  ),
                ),
              ),
          ],
        ),
        body: _body(context, async, packages),
      ),
    );
  }

  Widget _body(
    BuildContext context,
    AsyncValue<List<DataPackage>?> async,
    List<DataPackage>? packages,
  ) {
    if (async.isLoading && packages == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (async.hasError) {
      return _Centered(
        message: 'Could not load the data catalog.',
        onRetry: () => unawaited(ref.read(dataManagerPackagesProvider.notifier).refresh()),
      );
    }
    if (packages == null) {
      return const _Centered(message: 'Connect to a server to manage sky-data packages.');
    }
    if (packages.isEmpty) {
      return const _Centered(message: 'No sky-data packages are available.');
    }

    // Group by category so related packages cluster (star catalogs, horizons, …).
    final byCategory = <String, List<DataPackage>>{};
    for (final p in packages) {
      (byCategory[p.category] ??= <DataPackage>[]).add(p);
    }
    final categories = byCategory.keys.toList()..sort();

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        for (final cat in categories) ...[
          Text(_categoryLabel(cat), style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 4),
          ...byCategory[cat]!.map((p) => _PackageRow(package: p)),
          const SizedBox(height: 16),
        ],
      ],
    );
  }
}

String _categoryLabel(String c) => switch (c) {
      'catalog' => 'Star catalogs',
      'horizon' => 'Horizon profiles',
      '' => 'Other',
      _ => c[0].toUpperCase() + c.substring(1),
    };

/// Human-readable byte size (binary units). Public so widget tests can assert it.
String formatBytes(int bytes) {
  if (bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  var value = bytes.toDouble();
  var unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  if (unit == 0) return '$bytes B';
  return '${value.toStringAsFixed(value >= 100 ? 0 : 1)} ${units[unit]}';
}

class _PackageRow extends ConsumerWidget {
  final DataPackage package;
  const _PackageRow({required this.package});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final progress = ref.watch(
      dataManagerDownloadsProvider.select((m) => m[package.id]),
    );
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(package.name.isEmpty ? package.id : package.name,
                    style: theme.textTheme.bodyMedium),
                if (package.description.isNotEmpty)
                  Text(package.description,
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: AraColors.textSecondary)),
                if (progress != null && progress.phase == DownloadPhase.failed)
                  Text(
                    'Download ${progress.error ?? 'failed'}',
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: theme.colorScheme.error),
                  ),
                if (progress != null && progress.isActive) ...[
                  const SizedBox(height: 6),
                  LinearProgressIndicator(
                    // Indeterminate until the server reports a total; otherwise the fraction.
                    value: progress.totalBytes > 0 ? progress.fraction : null,
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(width: 12),
          Text(
            formatBytes(package.sizeBytes),
            style: theme.textTheme.bodySmall?.copyWith(color: AraColors.textDisabled),
          ),
          const SizedBox(width: 12),
          _action(context, ref, progress),
        ],
      ),
    );
  }

  Widget _action(BuildContext context, WidgetRef ref, DownloadProgress? progress) {
    if (progress != null && progress.isActive) {
      return TextButton(
        onPressed: () => unawaited(_cancel(context, ref, progress.downloadId)),
        child: const Text('Cancel'),
      );
    }
    if (package.isInstalled) {
      return TextButton(
        onPressed: () => unawaited(_delete(context, ref)),
        child: const Text('Remove'),
      );
    }
    return FilledButton.tonal(
      onPressed: () => unawaited(_download(context, ref)),
      child: const Text('Download'),
    );
  }

  Future<void> _download(BuildContext context, WidgetRef ref) => _guarded(
      context, () => ref.read(dataManagerPackagesProvider.notifier).download(package.id), 'Download failed');

  Future<void> _cancel(BuildContext context, WidgetRef ref, String downloadId) => _guarded(
      context, () => ref.read(dataManagerPackagesProvider.notifier).cancel(downloadId), 'Cancel failed');

  Future<void> _delete(BuildContext context, WidgetRef ref) => _guarded(
      context, () => ref.read(dataManagerPackagesProvider.notifier).delete(package.id), 'Remove failed');

  // Run an action and surface any failure as a SnackBar (download throws a 409 for an
  // already-installed package, or a transport error — the failure isn't in provider state).
  Future<void> _guarded(BuildContext context, Future<void> Function() action, String label) async {
    try {
      await action();
    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$label: $e')));
      }
    }
  }
}

class _Centered extends StatelessWidget {
  final String message;
  final VoidCallback? onRetry;
  const _Centered({required this.message, this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message, textAlign: TextAlign.center),
          if (onRetry != null) ...[
            const SizedBox(height: 12),
            TextButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ],
      ),
    );
  }
}
