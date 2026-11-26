import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/selection/selection_repository.dart';

void main() {
  group('SelectionRepository.addFact (same rules as the PC)', () {
    test('a new Profile file starts with its heading', () {
      expect(SelectionRepository.addFact(null, 'Plays chess', 'Profile/hobbies.md'), '# Hobbies\n\n- Plays chess\n');
    });

    test('appends to what is there, on one line', () {
      expect(SelectionRepository.addFact('# Work\n\n- Builds JARVIS', 'Learning\nRust', 'Profile/work.md'), '# Work\n\n- Builds JARVIS\n- Learning Rust\n');
    });

    test('an empty fact changes nothing', () {
      expect(SelectionRepository.addFact('- x\n', '   ', 'Profile/work.md'), '- x\n');
    });
  });
}
