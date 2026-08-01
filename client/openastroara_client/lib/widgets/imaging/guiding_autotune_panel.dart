import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guiding_autotune.dart';
import '../../state/guider/guiding_autotune_state.dart';

/// Deterministic guider auto-tune controls. The server owns all mutations and
/// rollback; this panel never issues guide pulses.
class GuidingAutoTunePanel extends ConsumerStatefulWidget {
  const GuidingAutoTunePanel({super.key});

  @override
  ConsumerState<GuidingAutoTunePanel> createState() => _GuidingAutoTunePanelState();
}

class _GuidingAutoTunePanelState extends ConsumerState<GuidingAutoTunePanel> {
  bool _useMainCameraValidation = false;

  @override
  Widget build(BuildContext context) {
    final capabilities = ref.watch(guidingAutoTuneCapabilitiesProvider);
    final session = ref.watch(guidingAutoTuneProvider);
    final notifier = ref.read(guidingAutoTuneProvider.notifier);

    return Card(
      child: ExpansionTile(
        leading: const Icon(Icons.tune),
        title: const Text('Guiding auto-tune'),
        subtitle: Text(_subtitle(capabilities, session)),
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                capabilities.when(
                  data: (value) => value == null
                      ? const Text('No active ARA server.')
                      : _CapabilitySummary(value: value),
                  loading: () => const LinearProgressIndicator(),
                  error: (error, _) => Text('Capability read failed: $error'),
                ),
                const SizedBox(height: 12),
                session.when(
                  data: (value) => value == null
                      ? const Text('No auto-tune session.')
                      : _SessionSummary(value: value),
                  loading: () => const LinearProgressIndicator(),
                  error: (error, _) => Text('Session read failed: $error'),
                ),
                const SizedBox(height: 12),
                SwitchListTile.adaptive(
                  contentPadding: EdgeInsets.zero,
                  value: _useMainCameraValidation,
                  onChanged: capabilities.asData?.value?.connected == true && !session.isLoading
                      ? (value) => setState(() => _useMainCameraValidation = value)
                      : null,
                  title: const Text('Validate main-camera star shape'),
                  subtitle: const Text('Captures bounded analysis frames. Needs known main image scale and usable stars.'),
                ),
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    FilledButton.icon(
                      onPressed: capabilities.asData?.value?.canAnalyze == true &&
                              !session.isLoading
                          ? () => notifier.start(
                              dryRun: true,
                              useMainCameraValidation: _useMainCameraValidation,
                            )
                          : null,
                      icon: const Icon(Icons.analytics_outlined),
                      label: const Text('Analyze'),
                    ),
                    FilledButton.icon(
                      onPressed: capabilities.asData?.value?.canAnalyze == true &&
                              !session.isLoading
                          ? () => notifier.start(
                              dryRun: false,
                              useMainCameraValidation: _useMainCameraValidation,
                            )
                          : null,
                      icon: const Icon(Icons.science_outlined),
                      label: const Text('Run bounded tune'),
                    ),
                    OutlinedButton.icon(
                      onPressed: session.asData?.value?.canApply == true
                          ? () => notifier.apply()
                          : null,
                      icon: const Icon(Icons.check),
                      label: const Text('Apply proposal'),
                    ),
                    OutlinedButton.icon(
                      onPressed: session.asData?.value?.canRollback == true
                          ? () => notifier.rollback()
                          : null,
                      icon: const Icon(Icons.restore),
                      label: const Text('Rollback'),
                    ),
                    TextButton.icon(
                      onPressed: session.asData?.value?.canRollback == true
                          ? () => notifier.cancel()
                          : null,
                      icon: const Icon(Icons.cancel_outlined),
                      label: const Text('Cancel'),
                    ),
                    TextButton.icon(
                      onPressed: session.asData?.value != null
                          ? () async {
                              final report = await notifier.report();
                              if (!context.mounted || report == null) return;
                              await showDialog<void>(
                                context: context,
                                builder: (context) => AlertDialog(
                                  title: const Text('Guiding auto-tune report'),
                                  content: SingleChildScrollView(child: Text(report.markdown)),
                                  actions: [
                                    TextButton(
                                      onPressed: () => Navigator.of(context).pop(),
                                      child: const Text('Close'),
                                    ),
                                  ],
                                ),
                              );
                            }
                          : null,
                      icon: const Icon(Icons.description_outlined),
                      label: const Text('Report'),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                const Text(
                  'Observed behavior is probabilistic. Apply requires explicit user action. '
                  'The server snapshots and restores guider settings.',
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  String _subtitle(
    AsyncValue<GuidingAutoTuneCapabilities?> capabilities,
    AsyncValue<GuidingAutoTuneStatus?> session,
  ) {
    final status = session.asData?.value;
    if (status != null && status.state != 'idle') return status.state;
    final value = capabilities.asData?.value;
    if (value == null) return 'Checking server';
    if (!value.connected) return 'Guider disconnected';
    if (!value.canAnalyze) return 'Collect guide telemetry first';
    return 'Ready';
  }
}

class _CapabilitySummary extends StatelessWidget {
  final GuidingAutoTuneCapabilities value;
  const _CapabilitySummary({required this.value});

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Guider: ${value.connected ? 'connected' : 'disconnected'}'),
          Text('Telemetry: ${value.hasTelemetry ? 'available' : 'missing'}'),
          for (final reason in value.lockedReasons)
            Text('Locked: $reason', style: TextStyle(color: Theme.of(context).colorScheme.error)),
        ],
      );
}

class _SessionSummary extends StatelessWidget {
  final GuidingAutoTuneStatus value;
  const _SessionSummary({required this.value});

  @override
  Widget build(BuildContext context) {
    final confidence = value.behaviorConfidence;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('State: ${value.state}'),
        if (value.currentStep.isNotEmpty) Text(value.currentStep),
        if (value.state != 'idle') LinearProgressIndicator(value: value.progress),
        if (value.behaviorClass != null)
          Text('Observed behavior: ${value.behaviorClass}'),
        if (confidence != null)
          Text('Confidence: ${(confidence * 100).toStringAsFixed(0)}%'),
        Text('Telemetry samples: ${value.telemetrySamples}'),
        for (final warning in value.warnings)
          Text('Warning: $warning', style: TextStyle(color: Theme.of(context).colorScheme.error)),
      ],
    );
  }
}
