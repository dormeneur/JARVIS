import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import 'auth_provider.dart';

/// QR payload schema (version 1):
///   {"v":1, "url":"http://100.x.x.x:8000", "t":"<invite_token>"}
class _InvitePayload {
  final String serverUrl;
  final String inviteToken;

  const _InvitePayload({required this.serverUrl, required this.inviteToken});

  static _InvitePayload? tryParse(String raw) {
    try {
      final j = jsonDecode(raw) as Map<String, dynamic>;
      if (j['v'] != 1) return null;
      final url = j['url'] as String?;
      final token = j['t'] as String?;
      if (url == null || token == null) return null;
      return _InvitePayload(serverUrl: url, inviteToken: token);
    } catch (_) {
      return null;
    }
  }
}

/// Guest screen — scans the admin's invite QR, then prompts for a device name
/// and registers directly without a setup secret.
class QrScanScreen extends ConsumerStatefulWidget {
  const QrScanScreen({super.key});

  @override
  ConsumerState<QrScanScreen> createState() => _QrScanScreenState();
}

class _QrScanScreenState extends ConsumerState<QrScanScreen> {
  final MobileScannerController _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.normal,
  );

  bool _scanned = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_scanned) return;
    final raw = capture.barcodes.firstOrNull?.rawValue;
    if (raw == null) return;

    final payload = _InvitePayload.tryParse(raw);
    if (payload == null) return;

    setState(() => _scanned = true);
    _controller.stop();
    _showNameDialog(payload);
  }

  Future<void> _showNameDialog(_InvitePayload payload) async {
    final controller = TextEditingController();
    String? dialogError;

    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setDialogState) {
          return AlertDialog(
            title: const Text('Name this device'),
            content: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Connecting to: ${payload.serverUrl}',
                  style: Theme.of(ctx).textTheme.bodySmall?.copyWith(
                        color: Theme.of(ctx).colorScheme.onSurfaceVariant,
                      ),
                ),
                const SizedBox(height: 16),
                TextField(
                  controller: controller,
                  autofocus: true,
                  decoration: InputDecoration(
                    labelText: 'Device name',
                    hintText: 'e.g., moto_g84',
                    border: const OutlineInputBorder(),
                    errorText: dialogError,
                  ),
                  onSubmitted: (_) => _register(
                    ctx,
                    setDialogState,
                    payload,
                    controller,
                    (e) => setDialogState(() => dialogError = e),
                  ),
                ),
              ],
            ),
            actions: [
              TextButton(
                onPressed: () {
                  Navigator.pop(ctx);
                  setState(() => _scanned = false);
                  _controller.start();
                },
                child: const Text('Cancel'),
              ),
              FilledButton(
                onPressed: () => _register(
                  ctx,
                  setDialogState,
                  payload,
                  controller,
                  (e) => setDialogState(() => dialogError = e),
                ),
                child: const Text('Register'),
              ),
            ],
          );
        },
      ),
    );
  }

  Future<void> _register(
    BuildContext ctx,
    StateSetter setDialogState,
    _InvitePayload payload,
    TextEditingController nameCtrl,
    void Function(String?) setError,
  ) async {
    final name = nameCtrl.text.trim();
    if (name.isEmpty) {
      setError('Enter a device name');
      return;
    }
    setError(null);

    try {
      final repo = ref.read(authRepositoryProvider);
      await repo.registerViaInvite(
        serverUrl: payload.serverUrl,
        deviceName: name,
        inviteToken: payload.inviteToken,
      );
      await ref.read(authProvider.notifier).initialize();

      if (ctx.mounted) Navigator.pop(ctx);
      if (mounted) Navigator.of(context).popUntil((r) => r.isFirst);
    } catch (e) {
      setError(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Scan Invite QR'),
        actions: [
          IconButton(
            icon: ValueListenableBuilder(
              valueListenable: _controller,
              builder: (_, state, __) => Icon(
                state.torchState == TorchState.on
                    ? Icons.flash_on
                    : Icons.flash_off,
              ),
            ),
            tooltip: 'Toggle torch',
            onPressed: _controller.toggleTorch,
          ),
        ],
      ),
      body: Stack(
        children: [
          MobileScanner(
            controller: _controller,
            onDetect: _onDetect,
          ),
          // Overlay with scanning cutout
          _ScanOverlay(),
          Positioned(
            bottom: 48,
            left: 0,
            right: 0,
            child: Text(
              'Point your camera at the QR code\ndisplayed on the admin device',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: Colors.white,
                shadows: const [Shadow(blurRadius: 8, color: Colors.black54)],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Semi-transparent overlay with a square cutout for the scan area.
class _ScanOverlay extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return CustomPaint(
      painter: _OverlayPainter(),
      child: const SizedBox.expand(),
    );
  }
}

class _OverlayPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    const cutSize = 240.0;
    final left = (size.width - cutSize) / 2;
    final top = (size.height - cutSize) / 2;
    final cutRect = Rect.fromLTWH(left, top, cutSize, cutSize);

    // Dark overlay
    final paint = Paint()..color = Colors.black54;
    final full = Rect.fromLTWH(0, 0, size.width, size.height);
    final path = Path()
      ..addRect(full)
      ..addRRect(RRect.fromRectAndRadius(cutRect, const Radius.circular(12)))
      ..fillType = PathFillType.evenOdd;
    canvas.drawPath(path, paint);

    // Corner brackets
    final bracket = Paint()
      ..color = Colors.white
      ..strokeWidth = 3
      ..style = PaintingStyle.stroke;
    const r = 12.0;
    const l = 24.0;

    for (final (dx, dy) in [
      (left, top),
      (left + cutSize, top),
      (left, top + cutSize),
      (left + cutSize, top + cutSize),
    ]) {
      final sx = dx == left ? 1.0 : -1.0;
      final sy = dy == top ? 1.0 : -1.0;
      canvas.drawLine(
        Offset(dx + sx * r, dy),
        Offset(dx + sx * (r + l), dy),
        bracket,
      );
      canvas.drawLine(
        Offset(dx, dy + sy * r),
        Offset(dx, dy + sy * (r + l)),
        bracket,
      );
      canvas.drawArc(
        Rect.fromCenter(
          center: Offset(dx + sx * r, dy + sy * r),
          width: r * 2,
          height: r * 2,
        ),
        sy == 1 ? (sx == 1 ? 3.14 : 1.57) : (sx == 1 ? 4.71 : 0),
        1.57,
        false,
        bracket,
      );
    }
  }

  @override
  bool shouldRepaint(_) => false;
}
