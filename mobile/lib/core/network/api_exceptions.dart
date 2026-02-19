import 'dart:io';

import 'package:dio/dio.dart';
import 'package:jarvis_mobile/core/errors/app_error.dart';

/// Converts Dio exceptions into typed app errors.
NetworkError mapDioError(DioException e) {
  if (e.type == DioExceptionType.connectionTimeout ||
      e.type == DioExceptionType.sendTimeout ||
      e.type == DioExceptionType.receiveTimeout) {
    return NetworkError('Connection timed out', cause: e);
  }

  if (e.type == DioExceptionType.connectionError ||
      e.error is SocketException) {
    return const NetworkError('Unable to reach server. Check your connection.');
  }

  final statusCode = e.response?.statusCode;
  final body = e.response?.data;

  String message = 'Request failed';
  if (body is Map && body['error'] is Map) {
    message = body['error']['message'] as String? ?? message;
  } else if (body is Map && body['detail'] is String) {
    message = body['detail'] as String;
  }

  return NetworkError(message, statusCode: statusCode, cause: e);
}
