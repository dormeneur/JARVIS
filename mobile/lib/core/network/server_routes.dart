/// The phone reaches the PC's jv-api more than one way: Tailscale (anywhere), home Wi-Fi, or USB
/// (`adb reverse`). The same device token works on every one, so the app keeps a list and, on each
/// check, uses the first that answers, starting with the one that worked last.
class ServerRoutes {
  ServerRoutes._();

  /// USB (`adb reverse tcp:8000 tcp:8000`): always worth a try last, so a phone whose saved address
  /// went stale can still find its way back while plugged in, and learn the others from there.
  static const usb = 'http://127.0.0.1:8000';

  /// Where to try, in order: the last one that worked, then the rest as learned, then USB. No duplicates.
  static List<String> candidates(String? current, List<String> known) {
    final seen = <String>{};
    return [
      if (current != null && current.isNotEmpty) current,
      ...known,
      usb,
    ].map(normalise).where((url) => url.isNotEmpty && seen.add(url)).toList();
  }

  /// The first of [candidates] whose /health answers (all tried at once), or null.
  static Future<String?> firstAnswering(List<String> candidates, Future<bool> Function(String url) healthy) async {
    final answers = await Future.wait(candidates.map(healthy));
    final index = answers.indexOf(true);
    return index < 0 ? null : candidates[index];
  }

  /// Merges newly learned addresses into what's kept (most recent first, at most 8).
  static List<String> remember(List<String> known, Iterable<String> found) {
    final seen = <String>{};
    return [...found, ...known].map(normalise).where((url) => url.isNotEmpty && seen.add(url)).take(8).toList();
  }

  static String normalise(String url) {
    final trimmed = url.trim();
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) return '';
    return trimmed.endsWith('/') ? trimmed.substring(0, trimmed.length - 1) : trimmed;
  }

  /// How the phone is connected, in words for the status pill.
  static String label(String url) {
    final host = Uri.tryParse(url)?.host ?? '';
    if (host == '127.0.0.1' || host == 'localhost') return 'USB';
    if (host.endsWith('.ts.net') || _inTailnet(host)) return 'Tailscale';
    if (host.startsWith('192.168.') || host.startsWith('10.') || _private172(host)) return 'Home Wi-Fi';
    return 'Server';
  }

  // Tailscale hands out 100.64.0.0/10.
  static bool _inTailnet(String host) {
    final parts = host.split('.');
    if (parts.length != 4 || parts[0] != '100') return false;
    final second = int.tryParse(parts[1]) ?? -1;
    return second >= 64 && second <= 127;
  }

  static bool _private172(String host) {
    final parts = host.split('.');
    if (parts.length != 4 || parts[0] != '172') return false;
    final second = int.tryParse(parts[1]) ?? -1;
    return second >= 16 && second <= 31;
  }
}
