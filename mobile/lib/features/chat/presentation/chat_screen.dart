import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import '../data/chat_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

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
  bool? _aiAvailable; // null = checking, true = online, false = offline

  @override
  void initState() {
    super.initState();
    _loadChatHistory();
    _checkAiStatus();
  }

  Future<void> _loadChatHistory() async {
    final db = ref.read(appDatabaseProvider);
    final history = await db.getAllChatMessages();
    if (mounted && history.isNotEmpty) {
      setState(() {
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
      _messages.add(ChatMessage(role: 'assistant', text: '', isStreaming: true));
      _isGenerating = true;
      _attachments.clear();
    });

    _scrollToBottom();

    final repo = ref.read(chatRepositoryProvider);
    try {
      await for (final chunk in repo.askJarvis(
        query,
        attachments: sentAttachments.isNotEmpty ? sentAttachments : null,
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

  Future<void> _saveChatPair(String query, List<String> attachments) async {
    if (_messages.length < 2) return;
    final assistant = _messages[_messages.length - 1];
    if (assistant.role != 'assistant' || assistant.text.isEmpty) return;

    final db = ref.read(appDatabaseProvider);
    await db.insertChatMessage(
      query: query,
      response: assistant.text,
      sources:
          assistant.sources != null ? jsonEncode(assistant.sources) : null,
      attachments: attachments.isNotEmpty ? jsonEncode(attachments) : null,
      timestamp: DateTime.now().toUtc().toIso8601String(),
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

  Future<void> _clearHistory() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Clear Chat History'),
        content:
            const Text('This will delete all saved messages. Continue?'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Cancel')),
          TextButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Clear', style: TextStyle(color: Colors.red))),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      final db = ref.read(appDatabaseProvider);
      await db.clearChatHistory();
      setState(() => _messages.clear());
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
      appBar: AppBar(
        title: Row(
          children: [
            const Text('JARVIS AI'),
            const SizedBox(width: 8),
            _buildStatusChip(theme),
          ],
        ),
        actions: [
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
              if (value == 'clear') _clearHistory();
              if (value == 'status') _checkAiStatus();
            },
            itemBuilder: (_) => [
              const PopupMenuItem(
                  value: 'clear', child: Text('Clear History')),
              const PopupMenuItem(
                  value: 'status', child: Text('Refresh Status')),
            ],
          ),
        ],
      ),
      body: Column(
        children: [
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
          // Input area
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
