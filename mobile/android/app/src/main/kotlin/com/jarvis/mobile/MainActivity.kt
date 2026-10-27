package com.jarvis.mobile

import io.flutter.embedding.android.FlutterFragmentActivity

// FlutterFragmentActivity (not FlutterActivity) is required by local_auth's
// BiometricPrompt for App Lock.
class MainActivity : FlutterFragmentActivity()
