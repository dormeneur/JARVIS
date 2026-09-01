import 'package:flutter/material.dart';

import '../../data/agent_models.dart';

/// Inline record of a tool the assistant ran, shown in the transcript.
///
/// The point is auditability: after the fact you can still see exactly which
/// tools touched the vault, with what arguments, and whether they succeeded.
class ToolCallCard extends StatelessWidget {
  final ToolCall call;

  const ToolCallCard({super.key, required this.call});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    final (Color color, IconData icon, String status) = switch (call) {
      ToolCall(denied: true) => (
          theme.colorScheme.onSurfaceVariant,
          Icons.block,
          'denied',
        ),
      ToolCall(ok: null) => (theme.colorScheme.primary, Icons.sync, 'running…'),
      ToolCall(ok: true) => (Colors.green.shade600, Icons.check_circle, 'done'),
      _ => (theme.colorScheme.error, Icons.error_outline, 'failed'),
    };

    return Container(
      margin: const EdgeInsets.only(bottom: 6),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.5),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: color.withValues(alpha: 0.35)),
      ),
      child: Theme(
        data: theme.copyWith(dividerColor: Colors.transparent),
        child: ExpansionTile(
          dense: true,
          tilePadding: const EdgeInsets.symmetric(horizontal: 12),
          childrenPadding: const EdgeInsets.fromLTRB(12, 0, 12, 10),
          visualDensity: VisualDensity.compact,
          leading: call.isRunning
              ? SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2, color: color),
                )
              : Icon(icon, size: 20, color: color),
          title: Row(
            children: [
              Flexible(
                child: Text(
                  call.label,
                  style: theme.textTheme.labelLarge
                      ?.copyWith(fontWeight: FontWeight.w600, color: color),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              const SizedBox(width: 6),
              Text(
                status,
                style: theme.textTheme.labelSmall
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
              ),
            ],
          ),
          subtitle: call.subject.isEmpty
              ? null
              : Text(
                  call.subject,
                  style: theme.textTheme.bodySmall?.copyWith(
                    fontFamily: 'monospace',
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
          children: [
            if (call.summary != null && call.summary!.isNotEmpty)
              Align(
                alignment: Alignment.centerLeft,
                child: SelectableText(
                  call.summary!,
                  style: theme.textTheme.bodySmall
                      ?.copyWith(fontFamily: 'monospace'),
                ),
              ),
          ],
        ),
      ),
    );
  }
}
