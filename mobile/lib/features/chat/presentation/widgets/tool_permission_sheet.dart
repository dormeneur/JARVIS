import 'dart:convert';

import 'package:flutter/material.dart';

import '../../data/agent_models.dart';

/// What the user chose when asked to approve a tool call.
enum ToolDecision {
  /// Run it this once; ask again next time.
  once,

  /// Run it and stop asking for this tool for the rest of the session.
  session,

  /// Run it and stop asking for this tool permanently (persisted).
  always,

  /// Don't run it. The model is told and can suggest an alternative.
  deny,
}

/// Claude-Code-style approval prompt for a single tool call.
///
/// Shows exactly what will happen — tool, target, and the full arguments —
/// before anything touches the vault. Destructive calls never offer a
/// remember-my-answer option: a blanket "always delete" is not a choice
/// worth being one tap away.
class ToolPermissionSheet extends StatelessWidget {
  final ToolCall call;

  const ToolPermissionSheet({super.key, required this.call});

  static Future<ToolDecision?> show(BuildContext context, ToolCall call) {
    return showModalBottomSheet<ToolDecision>(
      context: context,
      isScrollControlled: true,
      isDismissible: false,
      enableDrag: false,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) => ToolPermissionSheet(call: call),
    );
  }

  bool get _isDestructive => call.risk == ToolRisk.delete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final accent =
        _isDestructive ? theme.colorScheme.error : theme.colorScheme.primary;

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 36,
                height: 4,
                decoration: BoxDecoration(
                  color: theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Icon(
                  _isDestructive
                      ? Icons.warning_amber_rounded
                      : Icons.build_circle_outlined,
                  color: accent,
                  size: 28,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    _isDestructive
                        ? 'JARVIS wants to delete something'
                        : 'JARVIS wants to use a tool',
                    style: theme.textTheme.titleMedium
                        ?.copyWith(fontWeight: FontWeight.w600),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            Container(
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: theme.colorScheme.surfaceContainerHighest,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: accent.withValues(alpha: 0.4)),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Text(
                        call.label,
                        style: theme.textTheme.titleSmall
                            ?.copyWith(fontWeight: FontWeight.bold, color: accent),
                      ),
                      const SizedBox(width: 8),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                        decoration: BoxDecoration(
                          color: accent.withValues(alpha: 0.15),
                          borderRadius: BorderRadius.circular(4),
                        ),
                        child: Text(
                          call.name,
                          style: theme.textTheme.labelSmall?.copyWith(
                            fontFamily: 'monospace',
                            color: accent,
                          ),
                        ),
                      ),
                    ],
                  ),
                  if (call.subject.isNotEmpty) ...[
                    const SizedBox(height: 8),
                    Text(
                      call.subject,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontFamily: 'monospace',
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ],
                  if (call.arguments.isNotEmpty) ...[
                    const SizedBox(height: 10),
                    _ArgumentDetails(arguments: call.arguments),
                  ],
                ],
              ),
            ),
            if (_isDestructive) ...[
              const SizedBox(height: 12),
              Text(
                'This permanently removes data from your vault and syncs to '
                'every device. It cannot be undone.',
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: theme.colorScheme.error),
              ),
            ],
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: () => Navigator.pop(context, ToolDecision.once),
              icon: const Icon(Icons.check, size: 18),
              label: const Text('Allow once'),
              style: _isDestructive
                  ? FilledButton.styleFrom(backgroundColor: theme.colorScheme.error)
                  : null,
            ),
            // Remembering a destructive answer is deliberately not offered.
            if (!_isDestructive) ...[
              const SizedBox(height: 8),
              OutlinedButton.icon(
                onPressed: () => Navigator.pop(context, ToolDecision.session),
                icon: const Icon(Icons.timelapse, size: 18),
                label: const Text('Allow for this session'),
              ),
              const SizedBox(height: 8),
              OutlinedButton.icon(
                onPressed: () => Navigator.pop(context, ToolDecision.always),
                icon: const Icon(Icons.lock_open, size: 18),
                label: Text('Always allow ${call.name}'),
              ),
            ],
            const SizedBox(height: 8),
            TextButton(
              onPressed: () => Navigator.pop(context, ToolDecision.deny),
              child: const Text('Deny'),
            ),
          ],
        ),
      ),
    );
  }
}

/// Collapsed argument dump — expandable so long file content is reviewable
/// without burying the decision buttons below the fold.
class _ArgumentDetails extends StatefulWidget {
  final Map<String, dynamic> arguments;
  const _ArgumentDetails({required this.arguments});

  @override
  State<_ArgumentDetails> createState() => _ArgumentDetailsState();
}

class _ArgumentDetailsState extends State<_ArgumentDetails> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final pretty =
        const JsonEncoder.withIndent('  ').convert(widget.arguments);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        InkWell(
          onTap: () => setState(() => _expanded = !_expanded),
          child: Row(
            children: [
              Icon(
                _expanded ? Icons.expand_less : Icons.expand_more,
                size: 18,
                color: theme.colorScheme.onSurfaceVariant,
              ),
              const SizedBox(width: 4),
              Text(
                _expanded ? 'Hide details' : 'Show details',
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
              ),
            ],
          ),
        ),
        if (_expanded)
          Container(
            width: double.infinity,
            constraints: const BoxConstraints(maxHeight: 220),
            margin: const EdgeInsets.only(top: 6),
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: theme.colorScheme.surface,
              borderRadius: BorderRadius.circular(8),
            ),
            child: SingleChildScrollView(
              child: SelectableText(
                pretty,
                style: theme.textTheme.bodySmall
                    ?.copyWith(fontFamily: 'monospace'),
              ),
            ),
          ),
      ],
    );
  }
}
