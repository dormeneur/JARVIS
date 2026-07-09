import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:drift/drift.dart' as drift;
import 'package:uuid/uuid.dart';
import '../data/chat_repository.dart';
import '../data/chat_archive_service.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_provider.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';
import 'widgets/file_creation_modal.dart';

class ChatMessage {
  final String role; // 'user' or 'assistant'
  final String text;
  final bool isStreaming;
  final List<dynamic>? sources;
  final List<String>? attachments;

  ChatMessage({
    required this.role,
    required this.text,
    this.isStreaming = false,
    this.sources,
    this.attachments,
  });

  ChatMessage copyWith({
    String? text,
    bool? isStreaming,
    List<dynamic>? sources,
    List<String>? attachments,
  }) {
    return ChatMessage(
      role: role,
      text: text ?? this.text,
      isStreaming: isStreaming ?? this.isStreaming,
      sources: sources ?? this.sources,
      attachments: attachments ?? this.attachments,
    );
  }
}

class ChatScreen extends ConsumerStatefulWidget {
  const ChatScreen({super.key});

  @override
  ConsumerState<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends ConsumerState<ChatScreen> {
  final TextEditingController _textController = TextEditingController();
  final ScrollController _scrollController = ScrollController();
  final List<ChatMessage> _messages = [];
  final List<String> _attachments = [];
  bool _isGenerating = false;
  bool _isReindexing = false;
  bool _isArchiving = false;
  bool? _aiAvailable; // null = checking, true = online, false = offline
  String? _currentSessionId;
  String? _activeSessionId; // The session that can be texted

  @override
  void initState() {
    super.initState();
    _initializeApp();
  }

  Future<void> _initializeApp() async {
    await _ensureSessionExists();
    _loadChatHistory();
    await _checkAiStatus();
    _maybeRunArchiveJob();
  }

  Future<void> _ensureSessionExists() async {
    final db = ref.read(appDatabaseProvider);
    final sessions = await db.getAllChatSessions();
    if (sessions.isEmpty) {
      await _startNewChat();
    } else {
      _currentSessionId = sessions.first.id;
      _activeSessionId = _currentSessionId;
      await db.setActiveSession(_activeSessionId!);
    }
  }

  /// Flush all in-memory message pairs to SQLite for the current session.
  /// This ensures no messages are lost when switching away from a session.
  Future<void> _persistCurrentSession() async {
    if (_currentSessionId == null || _messages.isEmpty) return;

    final db = ref.read(appDatabaseProvider);
    final existingMessages = await db.getChatMessages(_currentSessionId!);
    final existingCount = existingMessages.length;

    // Messages are stored as user+assistant pairs.
    // Walk the in-memory list and save any pairs not already persisted.
    int pairIndex = 0;
    for (int i = 0; i < _messages.length - 1; i++) {
      final msg = _messages[i];
      final nextMsg = _messages[i + 1];
      if (msg.role == 'user' && nextMsg.role == 'assistant' && !nextMsg.isStreaming && nextMsg.text.isNotEmpty) {
        if (pairIndex >= existingCount) {
          // This pair has not been persisted yet — save it.
          final timestamp = DateTime.now().toUtc().toIso8601String();
          await db.insertChatMessage(
            query: msg.text,
            response: nextMsg.text,
            sessionId: _currentSessionId!,
            sources: nextMsg.sources != null ? jsonEncode(nextMsg.sources) : null,
            attachments: msg.attachments != null && msg.attachments!.isNotEmpty
                ? jsonEncode(msg.attachments)
                : null,
            timestamp: timestamp,
          );

          // Also sync to brain backend (best-effort)
          final repo = ref.read(chatRepositoryProvider);
          await repo.syncMessageToBrain(
            sessionId: _currentSessionId!,
            query: msg.text,
            response: nextMsg.text,
            timestamp: timestamp,
          );
        }
        pairIndex++;
        i++; // Skip the assistant message we just processed
      }
    }

    // Update session title if still default and we have messages
    if (_messages.isNotEmpty) {
      final session = await db.getChatSession(_currentSessionId!);
      if (session != null && session.title == 'New Conversation') {
        final firstUserMsg = _messages.firstWhere(
          (m) => m.role == 'user',
          orElse: () => _messages.first,
        );
        final newTitle = firstUserMsg.text.length > 60
            ? '${firstUserMsg.text.substring(0, 57)}...'
            : firstUserMsg.text;
        await db.upsertChatSession(ChatSessionsCompanion(
          id: drift.Value(_currentSessionId!),
          title: drift.Value(newTitle),
          createdAt: drift.Value(session.createdAt),
          lastActiveAt: drift.Value(DateTime.now().toUtc().toIso8601String()),
        ));
      }
    }
  }

  Future<void> _startNewChat() async {
    // Persist the current session's messages before switching
    await _persistCurrentSession();

    final sessionId = const Uuid().v4();
    final now = DateTime.now().toUtc().toIso8601String();
    final db = ref.read(appDatabaseProvider);

    await db.upsertChatSession(ChatSessionsCompanion.insert(
      id: sessionId,
      title: 'New Conversation',
      createdAt: now,
      lastActiveAt: now,
    ));

    // Mark this as the only active session
    await db.setActiveSession(sessionId);

    setState(() {
      _currentSessionId = sessionId;
      _activeSessionId = sessionId;
      _messages.clear();
    });
  }

  Future<void> _selectSession(String sessionId) async {
    if (sessionId == _currentSessionId) return;

    // Persist the current session's messages before switching
    await _persistCurrentSession();

    setState(() {
      _currentSessionId = sessionId;
      _messages.clear();
    });
    await _loadChatHistory();
  }

  Future<void> _loadChatHistory() async {
    if (_currentSessionId == null) return;
    final db = ref.read(appDatabaseProvider);
    final history = await db.getChatMessages(_currentSessionId!);
    if (mounted) {
      setState(() {
        _messages.clear();
        for (final msg in history) {
          _messages.add(ChatMessage(
            role: 'user',
            text: msg.query,
            attachments: msg.attachments != null
                ? List<String>.from(jsonDecode(msg.attachments!))
                : null,
          ));
          _messages.add(ChatMessage(
            role: 'assistant',
            text: msg.response,
            sources: msg.sources != null ? jsonDecode(msg.sources!) : null,
          ));
        }
      });
      _scrollToBottom();
    }
  }

  Future<void> _checkAiStatus() async {
    final repo = ref.read(chatRepositoryProvider);
    final available = await repo.checkAiStatus();
    if (mounted) {
      setState(() => _aiAvailable = available);
    }
  }

  /// Runs the chat archive job in the background if:
  /// 1. Auto-archive is enabled in settings
  /// 2. AI is online (reachable)
  /// 3. Last run was more than 24h ago
  Future<void> _maybeRunArchiveJob() async {
    final autoArchive = ref.read(autoArchiveProvider);
    if (!autoArchive) return;

    // Wait for AI status check to complete
    if (_aiAvailable != true) return;

    final shouldRun = await ArchiveTimestampService.shouldRunToday();
    if (!shouldRun) return;

    if (mounted) {
      setState(() => _isArchiving = true);
    }

    try {
      final db = ref.read(appDatabaseProvider);
      final chatRepo = ref.read(chatRepositoryProvider);
      final syncRepo = ref.read(syncRepositoryProvider);

      final archiveService = ChatArchiveService(
        db: db,
        chatRepo: chatRepo,
        syncRepo: syncRepo,
      );

      final count = await archiveService.runArchiveJob();

      if (mounted && count > 0) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Archived $count old chat${count > 1 ? 's' : ''} to Memory/Chats/'),
            duration: const Duration(seconds: 3),
          ),
        );
      }
    } catch (e) {
      // Archive job failure is non-critical — don't block the user
    } finally {
      if (mounted) {
        setState(() => _isArchiving = false);
      }
    }
  }

  void _sendMessage() async {
    final query = _textController.text.trim();
    if (query.isEmpty || _isGenerating) return;

    final sentAttachments = List<String>.from(_attachments);
    _textController.clear();
    setState(() {
      _messages.add(ChatMessage(
        role: 'user',
        text: query,
        attachments: sentAttachments.isNotEmpty ? sentAttachments : null,
      ));
    });

    if (query.toLowerCase().startsWith('/create')) {
      _handleFileCreation(query);
      return;
    }

    setState(() {
      _messages.add(ChatMessage(role: 'assistant', text: '', isStreaming: true));
      _isGenerating = true;
      _attachments.clear();
    });

    _scrollToBottom();

    final currentDir = ref.read(currentDirectoryProvider);
    final directory = currentDir.isNotEmpty ? currentDir : '.';

    final allHistory = _messages
        .where((m) => !m.isStreaming)
        .map((m) => {'role': m.role, 'content': m.text})
        .toList();
    final recentHistory = allHistory.length > 10 ? allHistory.sublist(allHistory.length - 10) : allHistory;

    final repo = ref.read(chatRepositoryProvider);
    try {
      await for (final chunk in repo.askJarvis(
        query,
        attachments: sentAttachments.isNotEmpty ? sentAttachments : null,
        chatHistory: recentHistory,
        currentDirectory: directory,
      )) {
        if (!mounted) break;

        setState(() {
          final lastIndex = _messages.length - 1;
          final lastMessage = _messages[lastIndex];

          if (chunk.startsWith('{')) {
            try {
              final data = jsonDecode(chunk);
              if (data.containsKey('error')) {
                _messages[lastIndex] = lastMessage.copyWith(
                  text: '${lastMessage.text}\n\n**Error:** ${data['error']}',
                  isStreaming: false,
                );
              } else if (data.containsKey('answer')) {
                _messages[lastIndex] = lastMessage.copyWith(
                  text: data['answer'],
                  isStreaming: false,
                  sources: data['sources'],
                );
              }
            } catch (_) {
              _messages[lastIndex] =
                  lastMessage.copyWith(text: lastMessage.text + chunk);
            }
          } else {
            _messages[lastIndex] =
                lastMessage.copyWith(text: lastMessage.text + chunk);
          }
        });
        _scrollToBottom();
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          final lastIndex = _messages.length - 1;
          final lastMessage = _messages[lastIndex];
          _messages[lastIndex] = lastMessage.copyWith(
            text: '${lastMessage.text}\n\n*Stream failed: $e*',
            isStreaming: false,
          );
        });
      }
    } finally {
      if (mounted) {
        setState(() {
          _isGenerating = false;
          final lastIndex = _messages.length - 1;
          if (_messages[lastIndex].isStreaming) {
            _messages[lastIndex] =
                _messages[lastIndex].copyWith(isStreaming: false);
          }
        });
        _scrollToBottom();

        // Save to chat history
        _saveChatPair(query, sentAttachments);
      }
    }
  }

  void _handleFileCreation(String query) async {
    setState(() {
      _isGenerating = true;
      _messages.add(ChatMessage(
        role: 'assistant',
        text: 'Parsing File Manifest...',
      ));
    });
    _scrollToBottom();

    final repo = ref.read(chatRepositoryProvider);
    final isDryRun = ref.read(dryRunModeProvider);
    final currentDir = ref.read(currentDirectoryProvider);
    final directory = currentDir.isNotEmpty ? currentDir : '.';

    try {
      final manifest = isDryRun 
         ? await repo.previewFiles(query, directory: directory)
         : await repo.generateFiles(query, directory: directory);
         
      if (!mounted) return;

      setState(() {
        _isGenerating = false;
        final lastIndex = _messages.length - 1;
        _messages[lastIndex] = _messages[lastIndex].copyWith(
            text: 'I have prepared a scaffold with ${manifest.length} items. Please review the manifest window to confirm file creation.');
      });

      if (manifest.isNotEmpty) {
        showDialog(
          context: context,
          barrierDismissible: false,
          builder: (_) => FileCreationModal(manifest: manifest, isDryRun: isDryRun),
        );
      } else {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('No files were generated by the AI.')),
        );
      }
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _isGenerating = false;
        final lastIndex = _messages.length - 1;
        _messages[lastIndex] = _messages[lastIndex].copyWith(
            text: 'Failed to generate files: $e');
      });
    }
  }

  Future<void> _saveChatPair(String query, List<String> attachments) async {
    if (_messages.length < 2 || _currentSessionId == null) return;
    final assistant = _messages[_messages.length - 1];
    if (assistant.role != 'assistant' || assistant.text.isEmpty) return;

    final db = ref.read(appDatabaseProvider);
    final timestamp = DateTime.now().toUtc().toIso8601String();

    // If first message, generate title
    final session = await db.getChatSession(_currentSessionId!);
    if (session != null && session.title == 'New Conversation') {
      final newTitle = query.length > 60 ? '${query.substring(0, 57)}...' : query;
      await db.upsertChatSession(ChatSessionsCompanion(
        id: drift.Value(_currentSessionId!),
        title: drift.Value(newTitle),
        createdAt: drift.Value(session.createdAt),
        lastActiveAt: drift.Value(timestamp),
      ));
    } else if (session != null) {
      await db.upsertChatSession(ChatSessionsCompanion(
        id: drift.Value(_currentSessionId!),
        title: drift.Value(session.title),
        createdAt: drift.Value(session.createdAt),
        lastActiveAt: drift.Value(timestamp),
      ));
    }

    await db.insertChatMessage(
      query: query,
      response: assistant.text,
      sessionId: _currentSessionId!,
      sources:
          assistant.sources != null ? jsonEncode(assistant.sources) : null,
      attachments: attachments.isNotEmpty ? jsonEncode(attachments) : null,
      timestamp: timestamp,
    );

    // Sync to backend history
    final repo = ref.read(chatRepositoryProvider);
    await repo.syncMessageToBrain(
      sessionId: _currentSessionId!,
      query: query,
      response: assistant.text,
      timestamp: timestamp,
    );
  }

  void _triggerReindex() async {
    setState(() => _isReindexing = true);
    final repo = ref.read(chatRepositoryProvider);
    final result = await repo.triggerReindex();
    if (mounted) {
      setState(() => _isReindexing = false);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Reindex: $result'),
          duration: const Duration(seconds: 2),
        ),
      );
    }
  }

  Future<void> _deleteCurrentSession() async {
    if (_currentSessionId == null) return;
    
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete Conversation'),
        content:
            const Text('This will delete all messages in this conversation. Continue?'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Cancel')),
          TextButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Delete', style: TextStyle(color: Colors.red))),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      final db = ref.read(appDatabaseProvider);
      final repo = ref.read(chatRepositoryProvider);
      
      final idToDelete = _currentSessionId!;
      await db.deleteChatSession(idToDelete);
      await repo.deleteSession(idToDelete);
      
      await _initializeApp();
    }
  }

  void _showAttachmentPicker() async {
    final db = ref.read(appDatabaseProvider);
    final files = await db.getAllFiles();
    final fileEntries =
        files.where((f) => f.type == 'file').toList()
          ..sort((a, b) => a.path.compareTo(b.path));

    if (!mounted || fileEntries.isEmpty) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('No synced files found. Sync first.')),
        );
      }
      return;
    }

    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => DraggableScrollableSheet(
        initialChildSize: 0.5,
        maxChildSize: 0.8,
        minChildSize: 0.3,
        expand: false,
        builder: (_, scrollController) => Column(
          children: [
            Padding(
              padding: const EdgeInsets.all(16),
              child: Text('Attach Vault File',
                  style: Theme.of(context).textTheme.titleMedium),
            ),
            const Divider(height: 1),
            Expanded(
              child: ListView.builder(
                controller: scrollController,
                itemCount: fileEntries.length,
                itemBuilder: (_, index) {
                  final file = fileEntries[index];
                  final isAttached = _attachments.contains(file.path);
                  return ListTile(
                    leading: Icon(
                      isAttached ? Icons.check_circle : Icons.insert_drive_file,
                      color: isAttached ? Colors.green : null,
                    ),
                    title: Text(file.name),
                    subtitle: Text(file.path,
                        style: Theme.of(context).textTheme.bodySmall),
                    onTap: () {
                      setState(() {
                        if (isAttached) {
                          _attachments.remove(file.path);
                        } else {
                          _attachments.add(file.path);
                        }
                      });
                      Navigator.pop(ctx);
                    },
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }

  void _scrollToBottom() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (_scrollController.hasClients) {
        _scrollController.animateTo(
          _scrollController.position.maxScrollExtent,
          duration: const Duration(milliseconds: 300),
          curve: Curves.easeOut,
        );
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      drawer: _buildHistoryDrawer(context),
      appBar: AppBar(
        title: Row(
          children: [
            const Text('JARVIS'),
            const SizedBox(width: 8),
            _buildStatusChip(theme),
          ],
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.add_comment_outlined),
            tooltip: 'New Chat',
            onPressed: _startNewChat,
          ),
          IconButton(
            icon: _isReindexing
                ? const SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.refresh),
            tooltip: 'Reindex Knowledge Base',
            onPressed: _isReindexing ? null : _triggerReindex,
          ),
          PopupMenuButton<String>(
            onSelected: (value) {
              if (value == 'delete') _deleteCurrentSession();
              if (value == 'status') _checkAiStatus();
            },
            itemBuilder: (_) => [
              const PopupMenuItem(
                  value: 'delete', child: Text('Delete Session', style: TextStyle(color: Colors.red))),
              const PopupMenuItem(
                  value: 'status', child: Text('Refresh Status')),
            ],
          ),
        ],
      ),
      body: Column(
        children: [
          // Archive progress indicator
          if (_isArchiving)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              color: theme.colorScheme.tertiaryContainer,
              child: Row(
                children: [
                  SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: theme.colorScheme.onTertiaryContainer,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Text(
                    'Archiving old chats...',
                    style: theme.textTheme.labelMedium?.copyWith(
                      color: theme.colorScheme.onTertiaryContainer,
                    ),
                  ),
                ],
              ),
            ),
          // Attachment chips
          if (_attachments.isNotEmpty)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              color: theme.colorScheme.surfaceContainerHighest,
              child: Wrap(
                spacing: 8,
                children: _attachments
                    .map((path) => Chip(
                          label: Text(
                            path.split('/').last,
                            style: theme.textTheme.labelSmall,
                          ),
                          deleteIcon: const Icon(Icons.close, size: 16),
                          onDeleted: () =>
                              setState(() => _attachments.remove(path)),
                          visualDensity: VisualDensity.compact,
                        ))
                    .toList(),
              ),
            ),
          Expanded(
            child: _messages.isEmpty
                ? Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(Icons.chat_bubble_outline,
                            size: 64, color: theme.colorScheme.outline),
                        const SizedBox(height: 16),
                        Text('Ask JARVIS anything about your vault',
                            style: theme.textTheme.bodyLarge?.copyWith(
                                color: theme.colorScheme.outline)),
                      ],
                    ),
                  )
                : ListView.builder(
                    controller: _scrollController,
                    padding: const EdgeInsets.all(16.0),
                    itemCount: _messages.length,
                    itemBuilder: (context, index) {
                      final message = _messages[index];
                      final isUser = message.role == 'user';

                      return Align(
                        alignment: isUser
                            ? Alignment.centerRight
                            : Alignment.centerLeft,
                        child: Container(
                          margin: const EdgeInsets.symmetric(vertical: 8.0),
                          padding: const EdgeInsets.all(12.0),
                          decoration: BoxDecoration(
                            color: isUser
                                ? theme.colorScheme.primaryContainer
                                : theme.colorScheme.secondaryContainer,
                            borderRadius:
                                BorderRadius.circular(16.0).copyWith(
                              bottomRight: isUser
                                  ? const Radius.circular(0)
                                  : const Radius.circular(16.0),
                              bottomLeft: isUser
                                  ? const Radius.circular(16.0)
                                  : const Radius.circular(0),
                            ),
                          ),
                          constraints: BoxConstraints(
                              maxWidth:
                                  MediaQuery.of(context).size.width * 0.8),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              // Show attached files on user messages
                              if (isUser &&
                                  message.attachments != null &&
                                  message.attachments!.isNotEmpty) ...[
                                Wrap(
                                  spacing: 4,
                                  children: message.attachments!
                                      .map((p) => Chip(
                                            avatar: const Icon(
                                                Icons.attach_file,
                                                size: 14),
                                            label: Text(p.split('/').last,
                                                style: theme
                                                    .textTheme.labelSmall),
                                            visualDensity:
                                                VisualDensity.compact,
                                            materialTapTargetSize:
                                                MaterialTapTargetSize
                                                    .shrinkWrap,
                                          ))
                                      .toList(),
                                ),
                                const SizedBox(height: 6),
                              ],
                              MarkdownBody(
                                data: message.text.isEmpty &&
                                        message.isStreaming
                                    ? '...'
                                    : message.text,
                                selectable: true,
                                styleSheet: MarkdownStyleSheet(
                                  p: theme.textTheme.bodyMedium?.copyWith(
                                      color: isUser
                                          ? theme
                                              .colorScheme.onPrimaryContainer
                                          : theme.colorScheme
                                              .onSecondaryContainer),
                                ),
                              ),
                              if (message.sources != null &&
                                  message.sources!.isNotEmpty) ...[
                                const SizedBox(height: 8),
                                Divider(
                                    color:
                                        theme.colorScheme.outlineVariant),
                                const SizedBox(height: 4),
                                Text('Sources:',
                                    style: theme.textTheme.labelSmall
                                        ?.copyWith(
                                            fontWeight: FontWeight.bold)),
                                ...message.sources!.map((s) => Text(
                                      '- ${s["path"] ?? "Unknown"}',
                                      style: theme.textTheme.labelSmall
                                          ?.copyWith(
                                              color: theme
                                                  .colorScheme.primary),
                                    )),
                              ],
                            ],
                          ),
                        ),
                      );
                    },
                  ),
          ),
          // Input area (only shown for active session)
          if (_currentSessionId == _activeSessionId)
            Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                ValueListenableBuilder<TextEditingValue>(
                  valueListenable: _textController,
                  builder: (context, value, child) {
                    final text = value.text;
                    if (text.startsWith('/')) {
                      final query = text.toLowerCase();
                      const commands = [
                        {'cmd': '/create', 'desc': 'Generate a scaffold of files from a prompt'},
                      ];
                      final matches = commands.where((c) => c['cmd']!.startsWith(query)).toList();
                      
                      if (matches.isNotEmpty) {
                        return Container(
                          margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                          decoration: BoxDecoration(
                            color: theme.colorScheme.surfaceContainerHighest,
                            borderRadius: BorderRadius.circular(12),
                            boxShadow: [
                              BoxShadow(
                                color: Colors.black.withValues(alpha: 0.1),
                                blurRadius: 4,
                                offset: const Offset(0, 2),
                              ),
                            ],
                          ),
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            children: matches.map((m) => ListTile(
                              leading: Icon(Icons.auto_awesome, color: theme.colorScheme.primary),
                              title: Text(m['cmd']!, style: const TextStyle(fontWeight: FontWeight.bold)),
                              subtitle: Text(m['desc']!, style: theme.textTheme.bodySmall),
                              onTap: () {
                                _textController.text = '${m['cmd']} ';
                                _textController.selection = TextSelection.fromPosition(
                                  TextPosition(offset: _textController.text.length),
                                );
                              },
                            )).toList(),
                          ),
                        );
                      }
                    }
                    return const SizedBox.shrink();
                  },
                ),
                Padding(
                  padding: const EdgeInsets.all(8.0),
                  child: Row(
                    children: [
                      IconButton(
                        icon: Badge(
                          isLabelVisible: _attachments.isNotEmpty,
                          label: Text('${_attachments.length}'),
                          child: const Icon(Icons.attach_file),
                        ),
                        onPressed: _isGenerating ? null : _showAttachmentPicker,
                        tooltip: 'Attach vault file',
                      ),
                      Expanded(
                        child: TextField(
                          controller: _textController,
                          decoration: InputDecoration(
                            hintText: _aiAvailable == false
                                ? 'AI is offline...'
                                : 'Ask JARVIS...',
                            border: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(24.0),
                            ),
                            contentPadding: const EdgeInsets.symmetric(
                                horizontal: 16.0, vertical: 12.0),
                          ),
                          onSubmitted: (_) => _sendMessage(),
                          enabled: !_isGenerating && _aiAvailable != false,
                        ),
                      ),
                      const SizedBox(width: 8),
                      IconButton(
                        icon: const Icon(Icons.send),
                        color: theme.colorScheme.primary,
                        onPressed: (_isGenerating || _aiAvailable == false)
                            ? null
                            : _sendMessage,
                      ),
                    ],
                  ),
                ),
              ],
            )
          else
            Container(
              padding: const EdgeInsets.symmetric(vertical: 16),
              color: theme.colorScheme.surfaceContainerHigh,
              width: double.infinity,
              child: Center(
                child: Text('Viewing past session (Read-only)',
                    style: theme.textTheme.labelMedium
                        ?.copyWith(color: theme.colorScheme.outline)),
              ),
            ),
        ],
      ),
    );
  }

  Widget _buildHistoryDrawer(BuildContext context) {
    final theme = Theme.of(context);
    final db = ref.watch(appDatabaseProvider);

    return Drawer(
      child: Column(
        children: [
          DrawerHeader(
            decoration: BoxDecoration(color: theme.colorScheme.primaryContainer),
            child: Center(
                child: Text('Chat History',
                    style: theme.textTheme.titleLarge?.copyWith(
                        color: theme.colorScheme.onPrimaryContainer))),
          ),
          ListTile(
            leading: const Icon(Icons.add),
            title: const Text('New Chat'),
            onTap: () {
              Navigator.pop(context);
              _startNewChat();
            },
          ),
          const Divider(),
          Expanded(
            child: StreamBuilder<List<ChatSession>>(
              stream: (db.select(db.chatSessions)
                    ..orderBy([(s) => drift.OrderingTerm.desc(s.lastActiveAt)]))
                  .watch(),
              builder: (context, snapshot) {
                if (!snapshot.hasData) {
                  return const Center(child: CircularProgressIndicator());
                }
                final sessions = snapshot.data!;
                if (sessions.isEmpty) {
                  return const Center(child: Text('No history yet'));
                }
                return ListView.builder(
                  itemCount: sessions.length,
                  itemBuilder: (context, index) {
                    final session = sessions[index];
                    final isViewing = session.id == _currentSessionId;
                    final isActive = session.id == _activeSessionId;
                    return ListTile(
                      selected: isViewing,
                      selectedTileColor: theme.colorScheme.primaryContainer.withValues(alpha: 0.3),
                      leading: Icon(
                        isActive ? Icons.chat : Icons.chat_bubble_outline,
                        color: isActive
                            ? theme.colorScheme.primary
                            : isViewing
                                ? theme.colorScheme.primary
                                : null,
                      ),
                      title: Text(session.title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: isActive
                              ? TextStyle(fontWeight: FontWeight.bold, color: theme.colorScheme.primary)
                              : null),
                      subtitle: Row(
                        children: [
                          if (isActive) ...[
                            Container(
                              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
                              decoration: BoxDecoration(
                                color: Colors.green.withValues(alpha: 0.2),
                                borderRadius: BorderRadius.circular(4),
                              ),
                              child: Text('Active',
                                  style: theme.textTheme.labelSmall?.copyWith(
                                      color: Colors.green, fontSize: 10)),
                            ),
                            const SizedBox(width: 6),
                          ],
                          Text(
                              session.lastActiveAt.length >= 10
                                  ? session.lastActiveAt.substring(0, 10)
                                  : session.lastActiveAt,
                              style: theme.textTheme.labelSmall),
                        ],
                      ),
                      onTap: () {
                        Navigator.pop(context);
                        _selectSession(session.id);
                      },
                      onLongPress: () {
                        // Quick delete option
                        showDialog(
                          context: context,
                          builder: (ctx) => AlertDialog(
                            title: const Text('Delete Session?'),
                            actions: [
                              TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
                              TextButton(onPressed: () async {
                                Navigator.pop(ctx);
                                await db.deleteChatSession(session.id);
                                ref.read(chatRepositoryProvider).deleteSession(session.id);
                                if (session.id == _currentSessionId) _initializeApp();
                              }, child: const Text('Delete', style: TextStyle(color: Colors.red))),
                            ],
                          ),
                        );
                      },
                    );
                  },
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildStatusChip(ThemeData theme) {
    if (_aiAvailable == null) {
      return const SizedBox(
        width: 12,
        height: 12,
        child: CircularProgressIndicator(strokeWidth: 2),
      );
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: _aiAvailable!
            ? Colors.green.withValues(alpha: 0.2)
            : Colors.red.withValues(alpha: 0.2),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        _aiAvailable! ? 'Online' : 'Offline',
        style: theme.textTheme.labelSmall?.copyWith(
          color: _aiAvailable! ? Colors.green : Colors.red,
          fontWeight: FontWeight.bold,
        ),
      ),
    );
  }
}
