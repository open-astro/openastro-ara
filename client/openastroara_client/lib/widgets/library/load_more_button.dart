import 'package:flutter/material.dart';

/// Load-more with a local in-flight spinner; the paging notifiers' own
/// in-flight guard makes double-taps a no-op even if this widget's state lags.
class LoadMoreButton extends StatefulWidget {
  final Future<void> Function() onLoadMore;
  const LoadMoreButton({super.key, required this.onLoadMore});

  @override
  State<LoadMoreButton> createState() => _LoadMoreButtonState();
}

class _LoadMoreButtonState extends State<LoadMoreButton> {
  bool _busy = false;

  Future<void> _tap() async {
    setState(() => _busy = true);
    try {
      await widget.onLoadMore();
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return OutlinedButton.icon(
      onPressed: _busy ? null : _tap,
      icon: _busy
          ? const SizedBox(
              width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
          : const Icon(Icons.expand_more, size: 16),
      label: const Text('Load more sessions'),
    );
  }
}
