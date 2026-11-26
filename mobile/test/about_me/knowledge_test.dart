import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/about_me/domain/knowledge.dart';

void main() {
  const traits = '# What JARVIS knows about you\n\nIntro line.\n\n- Works late — yes: “Spot on”\n- Learns from videos\n';

  group('ProfileNotes (same rules as the PC)', () {
    test('facts are the bullet lines', () {
      expect(ProfileNotes.facts(traits), ['Works late — yes: “Spot on”', 'Learns from videos']);
    });

    test('replace changes only that line and keeps the rest', () {
      final edited = ProfileNotes.replace(traits, 'Learns from videos', 'Learns from videos and podcasts')!;
      expect(edited, contains('- Learns from videos and podcasts'));
      expect(edited, contains('Intro line.'));
    });

    test('replace with nothing deletes; a line changed elsewhere is null', () {
      expect(ProfileNotes.replace(traits, 'Learns from videos', '  '), isNot(contains('videos')));
      expect(ProfileNotes.replace(traits, 'Not there', 'x'), isNull);
    });

    test('add starts a new file with its heading and keeps facts on one line', () {
      expect(ProfileNotes.add(null, 'Likes chess', ProfileNotes.traits), '# What Jarvis knows about you\n\n- Likes chess\n');
      expect(ProfileNotes.add(traits, 'Two\nlines', ProfileNotes.traits), endsWith('- Two lines\n'));
    });

    test('topic paths stay inside Profile', () {
      expect(ProfileNotes.topicPath('Travel'), 'Profile/travel.md');
      expect(ProfileNotes.topicPath('people/Family Members'), 'Profile/people/family-members.md');
      expect(ProfileNotes.topicPath('a/b/c'), isNull);
      expect(ProfileNotes.topicPath('forms/new'), isNull);
      expect(ProfileNotes.topicPath('../../Secrets'), 'Profile/secrets.md');
    });

    test('files: traits first, wrong guesses last, the brain\'s view hidden', () {
      ({String path, String name, bool isFolder}) f(String name) => (path: 'Profile/$name', name: name, isFolder: false);
      expect(ProfileNotes.files([f('work.md'), f('hunches.md'), f('not-me.md'), f('traits.md'), f('form-memory.json')]),
          ['Profile/traits.md', 'Profile/work.md', 'Profile/not-me.md']);
    });

    test('pages of paragraphs are not knowledge; new topics are', () {
      expect(ProfileNotes.isKnowledge('Profile/travel.md', '# Travel\n\n'), isTrue);
      expect(ProfileNotes.isKnowledge('Profile/standard-answers.md', '## Why us?\nBecause.'), isFalse);
    });
  });

  group('KnowledgeGraph', () {
    final files = [
      ('Profile/traits.md', '- Codes late at night — yes: “Spot on”\n- Learns new tools from videos\n'),
      ('Profile/interests.md', '- Watches coding videos on YouTube\n'),
      ('Profile/people/family.md', '- Sister Priya\n'),
    ];

    test('you, folders, topics and facts, joined', () {
      final map = KnowledgeGraph.build(files);
      expect(map.nodes.where((n) => n.kind == NodeKind.folder).single.label, 'People');
      expect(map.edges.any((e) => e.from == KnowledgeGraph.youId && e.to == 'Profile/people/'), isTrue);
      expect(map.nodes.singleWhere((n) => n.id == 'Profile/traits.md#0').label, 'Codes late at night');
      expect(map.edges.any((e) => e.related), isTrue); // "videos" joins two facts across topics
    });

    test('search matches every word typed', () {
      const node = GraphNode('x', NodeKind.fact, 'Plays chess', 'Plays chess on weekends', 'Profile/interests.md', 1);
      expect(node.matches('CHESS weekends'), isTrue);
      expect(node.matches('chess football'), isFalse);
      expect(node.matches(''), isFalse);
    });

    test('the layout settles, and an edit keeps what was there', () {
      final first = ForceLayout(KnowledgeGraph.build(files));
      var moving = 1.0;
      for (var i = 0; i < 600; i++) {
        moving = first.step();
      }
      expect(moving, lessThan(0.05));
      final grown = [files[0], ('Profile/interests.md', '${files[1].$2}- Reads sci-fi\n'), files[2]];
      final after = ForceLayout(KnowledgeGraph.build(grown), previous: first.snapshot());
      final ids = after.nodes.map((n) => n.id).toList();
      expect(after.positions[ids.indexOf('Profile/traits.md#0')], first.snapshot()['Profile/traits.md#0']);
      expect((after.positions[ids.indexOf('Profile/interests.md#1')] - after.positions[ids.indexOf('Profile/interests.md')]).distance, lessThan(25));
    });
  });
}
