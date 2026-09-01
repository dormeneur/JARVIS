import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'auth_provider.dart';

/// Admin screen — generates a 10-minute invite QR that a guest device scans.
///
/// The QR payload is a compact JSON object:
///   {"v":1, "url":"http://100.x.x.x:8000", "t":"<invite_token>"}
///
/// The guest device reads this, pre-fills the server URL, and calls
/// POST /auth/invite/register with the token.
class InviteQrScreen extends ConsumerStatefulWidget {
  const InviteQrScreen({super.key});

  @override
  ConsumerState<InviteQrScreen> createState() => _InviteQrScreenState();
}

class _InviteQrScreenState extends ConsumerState<InviteQrScreen> {
  _QrState _state = const _Loading();
  Timer? _countdownTimer;
  int _secondsLeft = 0;

  @override
  void initState() {
    super.initState();
    _generateInvite();
  }

  @override
  void dispose() {
    _countdownTimer?.cancel();
    super.dispose();
  }

  Future<void> _generateInvite() async {
    setState(() => _state = const _Loading());
    _countdownTimer?.cancel();

    try {
      final repo = ref.read(authRepositoryProvider);
      final result = await repo.createInviteToken();

      // Build the compact QR payload.
      final payload = jsonEncode({
        'v': 1,
        'url': result.serverUrl,
        't': result.inviteToken,
      });

      setState(() {
        _secondsLeft = result.ttlSeconds;
        _state = _Ready(qrData: payload, expiresAt: result.expiresAt);
      });

      _countdownTimer = Timer.periodic(const Duration(seconds: 1), (_) {
        if (!mounted) return;
        setState(() {
          _secondsLeft = (_secondsLeft - 1).clamp(0, result.ttlSeconds);
        });
        if (_secondsLeft == 0) {
          _countdownTimer?.cancel();
          setState(() => _state = const _Expired());
        }
      });
    } catch (e) {
      setState(() => _state = _Failed(message: e.toString()));
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Invite Device'),
        actions: [
          if (_state is _Ready || _state is _Expired)
            IconButton(
              icon: const Icon(Icons.refresh),
              tooltip: 'Generate new QR',
              onPressed: _generateInvite,
            ),
        ],
      ),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: switch (_state) {
            _Loading() => const Center(child: CircularProgressIndicator()),
            _Failed(:final message) => _ErrorBody(
                message: message,
                onRetry: _generateInvite,
              ),
            _Expired() => _ExpiredBody(onRefresh: _generateInvite),
            _Ready(:final qrData) => _QrBody(
                qrData: qrData,
                secondsLeft: _secondsLeft,
                onRefresh: _generateInvite,
              ),
          },
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// State types
// ---------------------------------------------------------------------------

sealed class _QrState {
  const _QrState();
}

class _Loading extends _QrState {
  const _Loading();
}

class _Ready extends _QrState {
  final String qrData;
  final DateTime expiresAt;
  const _Ready({required this.qrData, required this.expiresAt});
}

class _Expired extends _QrState {
  const _Expired();
}

class _Failed extends _QrState {
  final String message;
  const _Failed({required this.message});
}

// ---------------------------------------------------------------------------
// Body widgets
// ---------------------------------------------------------------------------

class _QrBody extends StatelessWidget {
  final String qrData;
  final int secondsLeft;
  final VoidCallback onRefresh;

  const _QrBody({
    required this.qrData,
    required this.secondsLeft,
    required this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final mins = secondsLeft ~/ 60;
    final secs = secondsLeft % 60;
    final timeStr = '$mins:${secs.toString().padLeft(2, '0')}';
    final isUrgent = secondsLeft < 60;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'Scan with the guest device',
          textAlign: TextAlign.center,
          style: theme.textTheme.titleMedium,
        ),
        const SizedBox(height: 8),
        Text(
          'Guest must be on Tailscale first',
          textAlign: TextAlign.center,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 24),
        Center(
          child: Container(
            decoration: BoxDecoration(
              color: Colors.white,
              borderRadius: BorderRadius.circular(16),
              boxShadow: [
                BoxShadow(
                  color: Colors.black.withValues(alpha: 0.08),
                  blurRadius: 12,
                  offset: const Offset(0, 4),
                ),
              ],
            ),
            padding: const EdgeInsets.all(16),
            child: QrImageView(
              data: qrData,
              version: QrVersions.auto,
              size: 240,
              eyeStyle: const QrEyeStyle(
                eyeShape: QrEyeShape.square,
                color: Color(0xFF1A1A2E),
              ),
              dataModuleStyle: const QrDataModuleStyle(
                dataModuleShape: QrDataModuleShape.square,
                color: Color(0xFF1A1A2E),
              ),
            ),
          ),
        ),
        const SizedBox(height: 24),
        // Countdown timer
        Center(
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 10),
            decoration: BoxDecoration(
              color: isUrgent
                  ? theme.colorScheme.errorContainer
                  : theme.colorScheme.surfaceContainerHighest,
              borderRadius: BorderRadius.circular(24),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.timer_outlined,
                  size: 18,
                  color: isUrgent
                      ? theme.colorScheme.error
                      : theme.colorScheme.onSurfaceVariant,
                ),
                const SizedBox(width: 6),
                Text(
                  'Expires in $timeStr',
                  style: theme.textTheme.labelLarge?.copyWith(
                    color: isUrgent
                        ? theme.colorScheme.error
                        : theme.colorScheme.onSurfaceVariant,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 24),
        OutlinedButton.icon(
          onPressed: onRefresh,
          icon: const Icon(Icons.refresh, size: 18),
          label: const Text('Generate new QR'),
        ),
        const SizedBox(height: 16),
        Text(
          'This QR is single-use and expires automatically.\n'
          'Do not share it outside of trusted people.',
          textAlign: TextAlign.center,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }
}

class _ExpiredBody extends StatelessWidget {
  final VoidCallback onRefresh;
  const _ExpiredBody({required this.onRefresh});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Icon(Icons.timer_off_outlined, size: 64, color: theme.colorScheme.error),
        const SizedBox(height: 16),
        Text(
          'QR code expired',
          textAlign: TextAlign.center,
          style: theme.textTheme.titleLarge,
        ),
        const SizedBox(height: 8),
        Text(
          'Generate a new one to continue.',
          textAlign: TextAlign.center,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 24),
        FilledButton.icon(
          onPressed: onRefresh,
          icon: const Icon(Icons.qr_code, size: 18),
          label: const Text('Generate New QR'),
        ),
      ],
    );
  }
}

class _ErrorBody extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  const _ErrorBody({required this.message, required this.onRetry});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Icon(Icons.error_outline, size: 64, color: theme.colorScheme.error),
        const SizedBox(height: 16),
        Text(
          'Could not generate invite',
          textAlign: TextAlign.center,
          style: theme.textTheme.titleLarge,
        ),
        const SizedBox(height: 8),
        Text(
          message,
          textAlign: TextAlign.center,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 24),
        FilledButton.icon(
          onPressed: onRetry,
          icon: const Icon(Icons.refresh, size: 18),
          label: const Text('Try Again'),
        ),
      ],
    );
  }
}
