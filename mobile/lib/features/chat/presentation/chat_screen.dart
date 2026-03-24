import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import '../data/chat_repository.dart';

class ChatMessage {
  final String role; // 'user' or 'assistant'
  final String text;
  final bool isStreaming;
  final List<dynamic>? sources;

  ChatMessage({
    required this.role,
    required this.text,
    this.isStreaming = false,
    this.sources,
  });

  ChatMessage copyWith({String? text, bool? isStreaming, List<dynamic>? sources}) {
    return ChatMessage(
      role: role,
      text: text ?? this.text,
      isStreaming: isStreaming ?? this.isStreaming,
      sources: sources ?? this.sources,
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
  bool _isGenerating = false;
  bool _isReindexing = false;

  void _sendMessage() async {
    final query = _textController.text.trim();
    if (query.isEmpty || _isGenerating) return;

    _textController.clear();
    setState(() {
      _messages.add(ChatMessage(role: 'user', text: query));
      _messages.add(ChatMessage(role: 'assistant', text: '', isStreaming: true));
      _isGenerating = true;
    });

    _scrollToBottom();

    final repo = ref.read(chatRepositoryProvider);
    try {
      await for (final chunk in repo.askJarvis(query)) {
        if (!mounted) break;
        
        setState(() {
          final lastIndex = _messages.length - 1;
          final lastMessage = _messages[lastIndex];
          
          // Check if it's the final JSON payload
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
                  sources: data['sources']
                );
              } else {
                // If it's a {"token": ...} fallback but normally the repo extracts the text
                // from {"token": ...} directly and yields string. So if it's JSON, it's final or error.
              }
            } catch (e) {
              // Not JSON, just append
              _messages[lastIndex] = lastMessage.copyWith(text: lastMessage.text + chunk);
            }
          } else {
            // normal text token
            _messages[lastIndex] = lastMessage.copyWith(text: lastMessage.text + chunk);
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
          // Ensure last message streaming flag is false
          final lastIndex = _messages.length - 1;
          if (_messages[lastIndex].isStreaming) {
            _messages[lastIndex] = _messages[lastIndex].copyWith(isStreaming: false);
          }
        });
        _scrollToBottom();
      }
    }
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
        title: const Text('JARVIS AI'),
        actions: [
          IconButton(
            icon: _isReindexing
                ? const SizedBox(
                    width: 20, height: 20,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.refresh),
            tooltip: 'Reindex Knowledge Base',
            onPressed: _isReindexing ? null : _triggerReindex,
          ),
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: ListView.builder(
              controller: _scrollController,
              padding: const EdgeInsets.all(16.0),
              itemCount: _messages.length,
              itemBuilder: (context, index) {
                final message = _messages[index];
                final isUser = message.role == 'user';
                
                return Align(
                  alignment: isUser ? Alignment.centerRight : Alignment.centerLeft,
                  child: Container(
                    margin: const EdgeInsets.symmetric(vertical: 8.0),
                    padding: const EdgeInsets.all(12.0),
                    decoration: BoxDecoration(
                      color: isUser ? theme.colorScheme.primaryContainer : theme.colorScheme.secondaryContainer,
                      borderRadius: BorderRadius.circular(16.0).copyWith(
                        bottomRight: isUser ? const Radius.circular(0) : const Radius.circular(16.0),
                        bottomLeft: isUser ? const Radius.circular(16.0) : const Radius.circular(0),
                      ),
                    ),
                    constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.8),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        MarkdownBody(
                          data: message.text.isEmpty && message.isStreaming ? '...' : message.text,
                          selectable: true,
                          styleSheet: MarkdownStyleSheet(
                            p: theme.textTheme.bodyMedium?.copyWith(
                              color: isUser ? theme.colorScheme.onPrimaryContainer : theme.colorScheme.onSecondaryContainer
                            ),
                          ),
                        ),
                        if (message.sources != null && message.sources!.isNotEmpty) ...[
                          const SizedBox(height: 8),
                          Divider(color: theme.colorScheme.outlineVariant),
                          const SizedBox(height: 4),
                          Text('Sources:', style: theme.textTheme.labelSmall?.copyWith(fontWeight: FontWeight.bold)),
                          ...message.sources!.map((s) => Text(
                            '- ${s["path"] ?? "Unknown"}',
                            style: theme.textTheme.labelSmall?.copyWith(
                              color: theme.colorScheme.primary
                            ),
                          )),
                        ],
                      ],
                    ),
                  ),
                );
              },
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(8.0),
            child: Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _textController,
                    decoration: InputDecoration(
                      hintText: 'Ask JARVIS...',
                      border: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(24.0),
                      ),
                      contentPadding: const EdgeInsets.symmetric(horizontal: 16.0, vertical: 12.0),
                    ),
                    onSubmitted: (_) => _sendMessage(),
                    enabled: !_isGenerating,
                  ),
                ),
                const SizedBox(width: 8),
                IconButton(
                  icon: const Icon(Icons.send),
                  color: theme.colorScheme.primary,
                  onPressed: _isGenerating ? null : _sendMessage,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
