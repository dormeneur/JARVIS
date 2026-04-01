import 'dart:io'; import 'package:docx_to_text/docx_to_text.dart'; void main() { final file = File('test.docx'); final bytes = file.readAsBytesSync(); final text = docxToText(bytes); print(text); }
