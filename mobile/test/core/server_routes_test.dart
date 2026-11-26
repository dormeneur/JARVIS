import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/network/server_routes.dart';

void main() {
  group('ServerRoutes', () {
    test('tries the one that worked last, then the rest, without repeats', () {
      expect(
        ServerRoutes.candidates('http://127.0.0.1:8000/', ['http://100.66.98.47:8000', 'http://127.0.0.1:8000', 'nonsense']),
        ['http://127.0.0.1:8000', 'http://100.66.98.47:8000'],
      );
    });

    test('a stale saved address still finds its way back over USB', () {
      expect(ServerRoutes.candidates('http://192.168.1.9:8000', const []), ['http://192.168.1.9:8000', ServerRoutes.usb]);
    });

    test('the first route in order that answers wins', () async {
      final working = await ServerRoutes.firstAnswering(['http://a', 'http://b', 'http://c'], (url) async => url != 'http://a');
      expect(working, 'http://b');
      expect(await ServerRoutes.firstAnswering(['http://a'], (_) async => false), isNull);
    });

    test('remembers new routes first and keeps at most eight', () {
      final kept = ServerRoutes.remember(List.generate(8, (i) => 'http://10.0.0.$i:8000'), ['http://100.66.98.47:8000']);
      expect(kept.first, 'http://100.66.98.47:8000');
      expect(kept.length, 8);
    });

    test('names how the phone is connected', () {
      expect(ServerRoutes.label('http://127.0.0.1:8000'), 'USB');
      expect(ServerRoutes.label('http://100.66.98.47:8000'), 'Tailscale');
      expect(ServerRoutes.label('http://msi.tailaa774b.ts.net:8000'), 'Tailscale');
      expect(ServerRoutes.label('http://192.168.1.20:8000'), 'Home Wi-Fi');
      expect(ServerRoutes.label('http://100.20.1.1:8000'), 'Server'); // outside Tailscale's 100.64/10
    });
  });
}
