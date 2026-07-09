import 'dart:convert';
import 'dart:developer' as developer;

import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/chat/data/chat_repository.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_provider.dart';
import 'package:jarvis_mobile/features/sync/data/sync_repository.dart';

/// Service that handles archiving old chat sessions to Memory/Chats/ vault files
/// and deleting the original session data after confirmed write.
class ChatArchiveService {
  final AppDatabase _db;
  final ChatRepository _chatRepo;
  final SyncRepository _syncRepo;

  /// Number of days after which inactive sessions become archival candidates.
  static const int retentionDays = 7;

  ChatArchiveService({
    required AppDatabase db,
    required ChatRepository chatRepo,
    required SyncRepository syncRepo,
  })  : _db = db,
        _chatRepo = chatRepo,
        _syncRepo = syncRepo;

  /// Run the full archive job. Returns the number of sessions archived.
  /// This is designed to be called at most once per 24h, gated by
  /// [ArchiveTimestampService.shouldRunToday].
  Future<int> runArchiveJob() async {
    developer.log('[ARCHIVE] Starting archive job', name: 'ChatArchiveService');

    // 1. Get archivable sessions
    final candidates = await getArchivableSessions();
    if (candidates.isEmpty) {
      developer.log('[ARCHIVE] No sessions to archive', name: 'ChatArchiveService');
      return 0;
    }

    developer.log('[ARCHIVE] Found ${candidates.length} sessions to archive',
        name: 'ChatArchiveService');

    int archived = 0;

    for (final session in candidates) {
      try {
        final success = await _archiveSession(session);
        if (success) {
          archived++;
        }
      } catch (e) {
        developer.log(
          '[ARCHIVE] Failed to archive session ${session.id}: $e',
          name: 'ChatArchiveService',
        );
        // Continue with other sessions — never let one failure block the rest
      }
    }

    // Update timestamp
    await ArchiveTimestampService.setLastRun(DateTime.now().toUtc());

    developer.log('[ARCHIVE] Completed. Archived $archived/${candidates.length} sessions',
        name: 'ChatArchiveService');

    return archived;
  }

  /// Get all sessions eligible for archiving:
  /// - status == 'inactive'
  /// - lastActiveAt older than [retentionDays] days
  Future<List<ChatSession>> getArchivableSessions() async {
    final cutoff = DateTime.now()
        .toUtc()
        .subtract(Duration(days: retentionDays))
        .toIso8601String();
    return _db.getInactiveSessionsOlderThan(cutoff);
  }

  /// Archive a single session:
  /// 1. Generate a summary via the AI
  /// 2. Write a .md file to the vault
  /// 3. Verify the file exists on the server
  /// 4. Delete the session from local DB and brain backend
  Future<bool> _archiveSession(ChatSession session) async {
    // Load all messages for this session
    final messages = await _db.getChatMessages(session.id);
    if (messages.isEmpty) {
      // No messages — just delete the empty session
      await _deleteSession(session.id);
      return true;
    }

    // Build the conversation text for summarization
    final conversationText = _buildConversationText(messages);

    // Generate summary via AI
    String summary;
    try {
      summary = await _generateSummary(conversationText);
    } catch (e) {
      developer.log(
        '[ARCHIVE] Summary generation failed for ${session.id}: $e. Skipping.',
        name: 'ChatArchiveService',
      );
      return false;
    }

    // Build the memory file
    final filename = _buildFilename(session);
    final filePath = 'Memory/Chats/$filename';
    final fileContent = _buildMemoryFileContent(session, summary);

    // Write to vault via server API
    try {
      await _syncRepo.createFileOnServer(filePath, fileContent);
    } catch (e) {
      developer.log(
        '[ARCHIVE] Failed to write memory file for ${session.id}: $e. Skipping deletion.',
        name: 'ChatArchiveService',
      );
      return false;
    }

    // Verify the file exists on the server
    final exists = await _verifyFileExists(filePath);
    if (!exists) {
      developer.log(
        '[ARCHIVE] Memory file verification failed for $filePath. Skipping deletion.',
        name: 'ChatArchiveService',
      );
      return false;
    }

    // Only now delete the session
    await _deleteSession(session.id);

    developer.log('[ARCHIVE] Successfully archived session ${session.id} → $filePath',
        name: 'ChatArchiveService');
    return true;
  }

  /// Build a human-readable conversation string from message records.
  String _buildConversationText(List<ChatMessage> messages) {
    final buffer = StringBuffer();
    for (final msg in messages) {
      buffer.writeln('User: ${msg.query}');
      buffer.writeln('Assistant: ${msg.response}');
      buffer.writeln();
    }
    return buffer.toString();
  }

  /// Call the AI to generate a one-liner summary of the conversation.
  Future<String> _generateSummary(String conversationText) async {
    // Truncate very long conversations to avoid token limits
    final truncated = conversationText.length > 4000
        ? conversationText.substring(0, 4000)
        : conversationText;

    final prompt =
        'Summarise this conversation in one concise sentence that captures '
        'the main topic or outcome. Reply with ONLY the summary sentence, '
        'nothing else.\n\nConversation:\n$truncated';

    final buffer = StringBuffer();
    await for (final chunk in _chatRepo.askJarvis(prompt)) {
      if (chunk.startsWith('{')) {
        try {
          final data = jsonDecode(chunk);
          if (data.containsKey('answer')) {
            // Final structured response — use it
            buffer.clear();
            buffer.write(data['answer'] as String);
          } else if (data.containsKey('error')) {
            throw Exception(data['error']);
          }
        } catch (e) {
          if (e is Exception) rethrow;
          // Partial JSON chunk — append as token
          buffer.write(chunk);
        }
      } else {
        buffer.write(chunk);
      }
    }

    final result = buffer.toString().trim();
    if (result.isEmpty) {
      throw Exception('Empty summary returned from AI');
    }
    return result;
  }

  /// Build the memory file path slug from session title + date.
  String _buildFilename(ChatSession session) {
    final date = session.lastActiveAt.length >= 10
        ? session.lastActiveAt.substring(0, 10)
        : DateTime.now().toUtc().toIso8601String().substring(0, 10);

    // Create a kebab-case slug from the title (3-5 words)
    final slug = _toSlug(session.title);
    return '$date-$slug.md';
  }

  /// Convert a title to a 3-5 word kebab-case slug.
  static String _toSlug(String title) {
    // Remove special characters and split into words
    final words = title
        .replaceAll(RegExp(r'[^a-zA-Z0-9\s]'), '')
        .trim()
        .split(RegExp(r'\s+'))
        .where((w) => w.isNotEmpty)
        .take(5)
        .map((w) => w.toLowerCase())
        .toList();

    if (words.isEmpty) return 'untitled-chat';
    return words.join('-');
  }

  /// Build the markdown content for the memory file.
  static String _buildMemoryFileContent(ChatSession session, String summary) {
    final date = session.lastActiveAt.length >= 10
        ? session.lastActiveAt.substring(0, 10)
        : 'Unknown';

    return '''# ${session.title}
**Date:** $date  
**Summary:** $summary
''';
  }

  /// Verify a file physically exists on the server.
  Future<bool> _verifyFileExists(String filePath) async {
    // First check local SQLite cache
    final entry = await _db.getEntry(filePath);
    if (entry != null && entry.lastSynced != null) {
      return true;
    }

    // Fall back to server check via GET /files/{path}
    try {
      final exists = await _chatRepo.checkFileExists(filePath);
      return exists;
    } catch (_) {
      return false;
    }
  }

  /// Delete a session from both local SQLite and brain backend.
  Future<void> _deleteSession(String sessionId) async {
    // Delete from local DB (cascade deletes messages too)
    await _db.deleteChatSession(sessionId);

    // Delete from brain backend (best-effort)
    try {
      await _chatRepo.deleteSession(sessionId);
    } catch (e) {
      developer.log(
        '[ARCHIVE] Failed to delete session $sessionId from brain: $e',
        name: 'ChatArchiveService',
      );
    }
  }
}
