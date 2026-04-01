import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_pdfview/flutter_pdfview.dart';
import 'package:docx_to_text/docx_to_text.dart';

class FileViewerScreen extends StatefulWidget {
  final String localPath;
  final String fileName;

  const FileViewerScreen({
    super.key,
    required this.localPath,
    required this.fileName,
  });

  @override
  State<FileViewerScreen> createState() => _FileViewerScreenState();
}

class _FileViewerScreenState extends State<FileViewerScreen> {
  int? _pages;
  int? _currentPage = 0;
  bool _isReady = false;
  String? _errorMessage;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final ext = widget.fileName.split('.').last.toLowerCase();

    return Scaffold(
      backgroundColor: Colors.black, // Dark background for media
      appBar: AppBar(
        title: Text(widget.fileName),
        backgroundColor: Colors.black,
        iconTheme: const IconThemeData(color: Colors.white),
        titleTextStyle: const TextStyle(color: Colors.white, fontSize: 20),
      ),
      body: SafeArea(
        child: _buildBody(ext, theme),
      ),
    );
  }

  Widget _buildBody(String ext, ThemeData theme) {
    // 1. Image Viewer
    if (['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp'].contains(ext)) {
      return Center(
        child: InteractiveViewer(
          minScale: 0.5,
          maxScale: 4.0,
          child: Image.file(
            File(widget.localPath),
            fit: BoxFit.contain,
            errorBuilder: (context, error, stackTrace) =>
                _buildErrorState(theme, 'Failed to load image.'),
          ),
        ),
      );
    }

    // 2. PDF Viewer
    if (ext == 'pdf') {
      return Stack(
        children: [
          PDFView(
            filePath: widget.localPath,
            enableSwipe: true,
            swipeHorizontal: false,
            autoSpacing: true,
            pageSnap: true,
            pageFling: true,
            onRender: (pages) {
              setState(() {
                _pages = pages;
                _isReady = true;
              });
            },
            onError: (error) {
              setState(() {
                _errorMessage = 'Failed to load PDF: $error';
              });
            },
            onPageError: (page, error) {
              setState(() {
                _errorMessage = 'Error on page $page: $error';
              });
            },
            onPageChanged: (page, total) {
              setState(() {
                _currentPage = page;
              });
            },
          ),
          if (_errorMessage != null)
            _buildErrorState(theme, _errorMessage!)
          else if (!_isReady)
            const Center(child: CircularProgressIndicator()),
          if (_isReady && _pages != null)
            Positioned(
              bottom: 16,
              right: 16,
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                decoration: BoxDecoration(
                  color: Colors.black54,
                  borderRadius: BorderRadius.circular(16),
                ),
                child: Text(
                  '${(_currentPage ?? 0) + 1} / $_pages',
                  style: const TextStyle(color: Colors.white),
                ),
              ),
            ),
        ],
      );
    }
    
    // 3. DOCX Viewer
    if (ext == 'docx') {
      return FutureBuilder<String>(
        future: Future.value(docxToText(File(widget.localPath).readAsBytesSync())),
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return _buildErrorState(theme, 'Failed to parse DOCX: ${snapshot.error}');
          }
          
          final text = snapshot.data ?? '';
          return Container(
            color: Colors.white,
            width: double.infinity,
            height: double.infinity,
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(16.0),
              child: Text(
                text,
                style: const TextStyle(
                  color: Colors.black87,
                  fontSize: 16,
                  height: 1.5,
                ),
              ),
            ),
          );
        },
      );
    }

    // 4. Fallback (Unsupported Type)
    return _buildErrorState(
        theme, 'Preview not supported for .${ext.toUpperCase()} files.');
  }

  Widget _buildErrorState(ThemeData theme, String message) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.broken_image_outlined, size: 64, color: Colors.grey),
          const SizedBox(height: 16),
          Text(
            message,
            textAlign: TextAlign.center,
            style: const TextStyle(color: Colors.grey, fontSize: 16),
          ),
        ],
      ),
    );
  }
}
