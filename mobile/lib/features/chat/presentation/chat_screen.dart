import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:drift/drift.dart' as drift;
import 'package:uuid/uuid.dart';
import 'package:dio/dio.dart' show CancelToken;
import '../data/agent_models.dart';
import '../data/chat_repository.dart';
import '../data/chat_archive_service.dart';
import 'tool_permission_provider.dart';
import 'widgets/tool_call_card.dart';
import 'widgets/tool_permission_sheet.dart';
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

  /// Tools the assistant ran for this turn, in order, for the audit trail.
  final List<ToolCall> toolCalls;

  /// Text of the message this one is replying to (swipe-to-reply).
  final String? replyToText;
  final String? replyToRole;

  ChatMessage({
    required this.role,
    required this.text,
    this.isStreaming = false,
    this.sources,
    this.attachments,
    this.toolCalls = const [],
    this.replyToText,
    this.replyToRole,
  });

  ChatMessage copyWith({
    String? text,
    bool? isStreaming,
    List<dynamic>? sources,
    List<String>? attachments,
    List<ToolCall>? toolCalls,
  }) {
    return ChatMessage(
      role: role,
      text: text ?? this.text,
      isStreaming: isStreaming ?? this.isStreaming,
      sources: sources ?? this.sources,
      attachments: attachments ?? this.attachments,
      toolCalls: toolCalls ?? this.toolCalls,
      replyToText: replyToText,
      replyToRole: replyToRole,
    );
  }

  /// Upsert a tool call by id, preserving order.
  List<ToolCall> withToolCall(ToolCall call) {
    final next = List<ToolCall>.from(toolCalls);
    final i = next.indexWhere((c) => c.id == call.id);
    if (i >= 0) {
      next[i] = call;
    } else {
      next.add(call);
    }
    return next;
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

  /// Message being replied to (swipe-to-reply), quoted into the next send.
  ChatMessage? _replyingTo;

  /// Lets the Stop button abort an in-flight agent turn.
  CancelToken? _cancelToken;

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
    final reply = _replyingTo;

    _textController.clear();
    setState(() {
      _messages.add(ChatMessage(
        role: 'user',
        text: query,
        attachments: sentAttachments.isNotEmpty ? sentAttachments : null,
        replyToText: reply?.text,
        replyToRole: reply?.role,
      ));
      _replyingTo = null;
    });

    // `/create` still works, but is no longer required — the model now picks
    // its own tools for "make me a file…" style requests.
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
    await _runAgentTurn(query, sentAttachments, reply);
  }

  /// Drive the agentic loop, pausing for approval whenever the server asks.
  ///
  /// The server is stateless about approvals: when it needs one it ends the
  /// stream, and we resume by re-sending the same query with the decision
  /// appended to [transcript].
  Future<void> _runAgentTurn(
    String query,
    List<String> attachments,
    ChatMessage? reply,
  ) async {
    final currentDir = ref.read(currentDirectoryProvider);
    final directory = currentDir.isNotEmpty ? currentDir : '.';
    final repo = ref.read(chatRepositoryProvider);

    // Quoted message becomes explicit context so "it"/"that file" resolves.
    final effectiveQuery = reply == null
        ? query
        : 'Regarding this earlier ${reply.role == 'user' ? 'message of mine' : 'answer of yours'}:\n"""\n${reply.text}\n"""\n\n$query';

    final history = _messages
        .where((m) => !m.isStreaming && m.text.isNotEmpty)
        .map((m) => {'role': m.role, 'content': m.text})
        .toList();
    // Drop the just-added user turn; the server appends `query` itself.
    if (history.isNotEmpty) history.removeLast();
    final recentHistory =
        history.length > 10 ? history.sublist(history.length - 10) : history;

    final transcript = <Map<String, dynamic>>[];
    _cancelToken = CancelToken();

    try {
      // Each pass runs until the stream ends or an approval interrupts it.
      for (var pass = 0; pass < 12; pass++) {
        ToolCall? awaiting;

        await for (final event in repo.askAgent(
          effectiveQuery,
          attachments: attachments.isNotEmpty ? attachments : null,
          chatHistory: recentHistory,
          currentDirectory: directory,
          grantedTools: ref.read(toolPermissionProvider).granted,
          toolTranscript: transcript,
          cancelToken: _cancelToken,
        )) {
          if (!mounted) return;
          final i = _messages.length - 1;

          switch (event) {
            case AgentToken(:final token):
              setState(() => _messages[i] =
                  _messages[i].copyWith(text: _messages[i].text + token));
              _scrollToBottom();

            case AgentToolStart(:final call):
              setState(() => _messages[i] =
                  _messages[i].copyWith(toolCalls: _messages[i].withToolCall(call)));
              _scrollToBottom();

            case AgentToolResult(:final id, :final ok, :final summary):
              final existing =
                  _messages[i].toolCalls.where((c) => c.id == id).firstOrNull;
              if (existing != null) {
                setState(() => _messages[i] = _messages[i].copyWith(
                    toolCalls: _messages[i]
                        .withToolCall(existing.copyWith(ok: ok, summary: summary))));
              }

            case AgentApprovalRequired(:final call):
              awaiting = call;

            case AgentFinal(:final sources):
              setState(() => _messages[i] = _messages[i]
                  .copyWith(isStreaming: false, sources: sources));

            case AgentError(:final message):
              setState(() => _messages[i] = _messages[i].copyWith(
                    text: '${_messages[i].text}\n\n**Error:** $message',
                    isStreaming: false,
                  ));
              return;
          }
        }

        final pending = awaiting;
        if (pending == null) return; // turn complete

        // Ask the user, then resume the loop from this decision.
        if (!mounted) return;
        final decision = await ToolPermissionSheet.show(context, pending);
        if (!mounted) return;

        final approved = decision != null &&
            await ref
                .read(toolPermissionProvider.notifier)
                .applyDecision(pending, decision);

        transcript.add(pending.toTranscriptJson(approved));

        final i = _messages.length - 1;
        setState(() => _messages[i] = _messages[i].copyWith(
              toolCalls: _messages[i].withToolCall(
                approved ? pending : pending.copyWith(denied: true),
              ),
            ));
      }
    } catch (e) {
      if (mounted) {
        final i = _messages.length - 1;
        setState(() => _messages[i] = _messages[i].copyWith(
              text: '${_messages[i].text}\n\n*Stream failed: $e*',
              isStreaming: false,
            ));
      }
    } finally {
      _cancelToken = null;
      if (mounted) {
        setState(() {
          _isGenerating = false;
          final i = _messages.length - 1;
          if (_messages[i].isStreaming) {
            _messages[i] = _messages[i].copyWith(isStreaming: false);
          }
        });
        _scrollToBottom();
        _saveChatPair(query, attachments);
        // Tools may have changed the vault — refresh the explorer view.
        ref.invalidate(directoryEntriesProvider);
      }
    }
  }

  /// Abort the in-flight turn (the Stop button).
  void _stopGeneration() {
    _cancelToken?.cancel('stopped by user');
    _cancelToken = null;
    if (!mounted) return;
    setState(() {
      _isGenerating = false;
      final i = _messages.length - 1;
      if (i >= 0 && _messages[i].isStreaming) {
        _messages[i] = _messages[i].copyWith(
          text: _messages[i].text.isEmpty
              ? '*Stopped.*'
              : '${_messages[i].text}\n\n*Stopped.*',
          isStreaming: false,
        );
      }
    });
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

                      return _SwipeToReply(
                        onReply: message.text.trim().isEmpty
                            ? null
                            : () => setState(() => _replyingTo = message),
                        child: Align(
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
                              // Quoted message this one is replying to
                              if (message.replyToText != null) ...[
                                _QuotedReply(
                                  text: message.replyToText!,
                                  role: message.replyToRole ?? 'assistant',
                                ),
                                const SizedBox(height: 6),
                              ],
                              // Tools this turn ran, in order — the audit trail
                              if (message.toolCalls.isNotEmpty) ...[
                                ...message.toolCalls
                                    .map((c) => ToolCallCard(call: c)),
                                const SizedBox(height: 4),
                              ],
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
                        {
                          'cmd': '/create',
                          'desc': 'Scaffold several files at once (plain requests now work too)'
                        },
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
                // Reply banner — what the next message will quote
                if (_replyingTo != null)
                  Container(
                    margin: const EdgeInsets.fromLTRB(12, 0, 12, 4),
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
                    decoration: BoxDecoration(
                      color: theme.colorScheme.surfaceContainerHighest,
                      borderRadius: BorderRadius.circular(10),
                      border: Border(
                        left: BorderSide(color: theme.colorScheme.primary, width: 3),
                      ),
                    ),
                    child: Row(
                      children: [
                        Icon(Icons.reply, size: 16, color: theme.colorScheme.primary),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                _replyingTo!.role == 'user' ? 'You' : 'JARVIS',
                                style: theme.textTheme.labelSmall?.copyWith(
                                  color: theme.colorScheme.primary,
                                  fontWeight: FontWeight.bold,
                                ),
                              ),
                              Text(
                                _replyingTo!.text.replaceAll('\n', ' '),
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: theme.textTheme.bodySmall,
                              ),
                            ],
                          ),
                        ),
                        IconButton(
                          icon: const Icon(Icons.close, size: 18),
                          visualDensity: VisualDensity.compact,
                          onPressed: () => setState(() => _replyingTo = null),
                          tooltip: 'Cancel reply',
                        ),
                      ],
                    ),
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
                      // Send turns into Stop while a turn is in flight, so a
                      // runaway tool loop is always one tap from cancellable.
                      _isGenerating
                          ? IconButton(
                              icon: const Icon(Icons.stop_circle_outlined),
                              color: theme.colorScheme.error,
                              tooltip: 'Stop generating',
                              onPressed: _stopGeneration,
                            )
                          : IconButton(
                              icon: const Icon(Icons.send),
                              color: theme.colorScheme.primary,
                              onPressed: _aiAvailable == false ? null : _sendMessage,
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

/// Horizontal drag on a message bubble to reply to it, WhatsApp-style.
///
/// Only triggers past a threshold and snaps back, so it never fights the
/// ListView's vertical scroll.
class _SwipeToReply extends StatefulWidget {
  final Widget child;
  final VoidCallback? onReply;

  const _SwipeToReply({required this.child, this.onReply});

  @override
  State<_SwipeToReply> createState() => _SwipeToReplyState();
}

class _SwipeToReplyState extends State<_SwipeToReply> {
  static const _triggerAt = 56.0;
  double _dx = 0;
  bool _armed = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (widget.onReply == null) return widget.child;

    return GestureDetector(
      onHorizontalDragUpdate: (d) {
        // Right-drag only; clamp so the bubble can't be flung off-screen.
        final next = (_dx + d.delta.dx).clamp(0.0, _triggerAt + 16);
        final armed = next >= _triggerAt;
        if (armed && !_armed) Feedback.forTap(context);
        setState(() {
          _dx = next;
          _armed = armed;
        });
      },
      onHorizontalDragEnd: (_) {
        if (_armed) widget.onReply!.call();
        setState(() {
          _dx = 0;
          _armed = false;
        });
      },
      onHorizontalDragCancel: () => setState(() {
        _dx = 0;
        _armed = false;
      }),
      child: Stack(
        alignment: Alignment.centerLeft,
        children: [
          if (_dx > 4)
            Padding(
              padding: const EdgeInsets.only(left: 8),
              child: Opacity(
                opacity: (_dx / _triggerAt).clamp(0.0, 1.0),
                child: Icon(
                  Icons.reply,
                  size: 20,
                  color: _armed
                      ? theme.colorScheme.primary
                      : theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ),
          Transform.translate(
            offset: Offset(_dx, 0),
            child: widget.child,
          ),
        ],
      ),
    );
  }
}

/// The quoted snippet rendered inside a message that replied to another.
class _QuotedReply extends StatelessWidget {
  final String text;
  final String role;

  const _QuotedReply({required this.text, required this.role});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
      decoration: BoxDecoration(
        color: theme.colorScheme.surface.withValues(alpha: 0.45),
        borderRadius: BorderRadius.circular(8),
        border: Border(
          left: BorderSide(color: theme.colorScheme.primary, width: 3),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            role == 'user' ? 'You' : 'JARVIS',
            style: theme.textTheme.labelSmall?.copyWith(
              color: theme.colorScheme.primary,
              fontWeight: FontWeight.bold,
            ),
          ),
          Text(
            text.replaceAll('\n', ' '),
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodySmall,
          ),
        ],
      ),
    );
  }
}
