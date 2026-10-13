import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/server_internal_state.dart';
import '../../state/stats/server_internal_state_state.dart';
import '../../theme/ara_colors.dart';
import 'responsive_tile_grid.dart';
import 'stat_tile.dart';

/// §66.7 — the Stats "Pi performance" section: what the server process is
/// doing right now (memory, CPU, GC, threads, connected clients, host load).
/// Read on open and on the refresh button; nothing polls in the background.
class PiPerformanceSection extends ConsumerStatefulWidget {
  const PiPerformanceSection({super.key});

  @override
  ConsumerState<PiPerformanceSection> createState() => _PiPerformanceSectionState();
}

class _PiPerformanceSectionState extends ConsumerState<PiPerformanceSection> {
  bool _refreshing = false;
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(serverInternalStateProvider.notifier).refresh();
      if (mounted) setState(() => _staleError = false);
    } catch (_) {
      if (mounted) setState(() => _staleError = true);
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(serverInternalStateProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(serverInternalStateProvider);
    final data = async.asData?.value;
    final spinning = _refreshing || (async.isLoading && data == null);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text('Pi performance', style: Theme.of(context).textTheme.titleMedium),
            ),
            IconButton(
              tooltip: 'Refresh',
              iconSize: 18,
              visualDensity: VisualDensity.compact,
              onPressed: spinning ? null : () => unawaited(_refresh()),
              icon: spinning
                  ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.refresh),
            ),
          ],
        ),
        const SizedBox(height: 8),
        if (_staleError && data != null)
          const Padding(
            padding: EdgeInsets.only(bottom: 8),
            child: Text('Could not refresh — showing the last reading.',
                style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
          ),
        _body(context, async, data),
      ],
    );
  }

  Widget _body(BuildContext context, AsyncValue<ServerInternalState?> async, ServerInternalState? data) {
    if (async.isLoading && data == null) {
      return const _Hint('Reading the server…');
    }
    if (async.hasError && data == null) {
      return Row(
        children: [
          const Expanded(child: _Hint('Could not read the server\'s performance figures.')),
          TextButton(onPressed: () => unawaited(_refresh()), child: const Text('Retry')),
        ],
      );
    }
    if (data == null) {
      return const _Hint('Connect to a server to see how its computer is doing.');
    }
    final cpu = data.cpuPercent;
    final load = data.loadAverage1Min;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ResponsiveTileGrid(
          children: [
            StatTile(icon: Icons.timer_outlined, value: formatUptime(data.uptimeSeconds), label: 'Up for'),
            StatTile(
              icon: Icons.memory,
              value: formatBytes(data.workingSetBytes),
              label: 'Memory in use',
              subtitle: 'Managed heap ${formatBytes(data.managedHeapBytes)}',
            ),
            StatTile(
              icon: Icons.speed,
              value: cpu == null ? '—' : '${cpu.toStringAsFixed(0)} %',
              label: 'CPU (one core = 100 %)',
              subtitle: cpu == null ? 'Refresh for a reading' : '${data.processorCount} cores',
            ),
            StatTile(
              icon: Icons.cleaning_services_outlined,
              value: '${data.gen2Collections}',
              label: 'Full garbage collections',
              subtitle: 'Paused ${(data.gcPauseTotalMs / 1000).toStringAsFixed(1)} s in total',
            ),
            StatTile(
              icon: Icons.account_tree_outlined,
              value: '${data.threadCount}',
              label: 'Threads',
              subtitle: '${data.threadPoolBusyWorkers} busy, ${data.threadPoolPendingWorkItems} queued',
            ),
            StatTile(
              icon: Icons.devices_other,
              value: '${data.wsOpenConnections} / ${data.wsMaxConnections}',
              label: 'Connected clients',
            ),
            if (load != null)
              StatTile(
                icon: Icons.show_chart,
                value: load.toStringAsFixed(2),
                label: 'Load (1 min)',
                subtitle: load > data.processorCount ? 'Above the core count' : 'Within the core count',
              ),
          ],
        ),
        const SizedBox(height: 8),
        Text(
          '${data.osDescription} · ${data.architecture} · .NET ${data.runtimeVersion}',
          style: const TextStyle(color: AraColors.textSecondary, fontSize: 12),
        ),
      ],
    );
  }
}

class _Hint extends StatelessWidget {
  final String text;
  const _Hint(this.text);
  @override
  Widget build(BuildContext context) =>
      Text(text, style: const TextStyle(color: AraColors.textSecondary));
}
