# Phase 3 Polish — Implementation Plan

Three features to bring the AI chat to production-ready quality.

---

## 1. Chat History Persistence (Drift)

### Approach
Add a `ChatMessages` table to the existing Drift database. Bump schema to v6. Save each user+assistant message pair after the AI finishes streaming.

### Files

#### [NEW] [chat_messages_table.dart](file:///b:/DEV/JARVIS/mobile/lib/features/chat/data/chat_messages_table.dart)
- Drift table definition: [id](file:///b:/DEV/JARVIS/mobile/test/sync_state_consistency_test.dart#94-121) (auto-increment), [query](file:///b:/DEV/JARVIS/brain/app/services/vector_store.py#90-116) (text), [response](file:///b:/DEV/JARVIS/brain/tests/test_ask_endpoint_integration.py#55-64) (text), `sources` (nullable JSON text), `attachments` (nullable JSON text), `timestamp` (text ISO8601)

#### [MODIFY] [app_database.dart](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart)
- Add `ChatMessages` to `@DriftDatabase(tables: [...])` 
- Bump `schemaVersion` from 5 → 6
- Add migration: `if (from < 6) await m.createTable(chatMessages);`
- Add CRUD methods: `insertChatMessage()`, `getAllChatMessages()`, `deleteChatMessage()`, `clearChatHistory()`

#### [MODIFY] [chat_screen.dart](file:///b:/DEV/JARVIS/mobile/lib/features/chat/presentation/chat_screen.dart)
- Load history from DB on [initState](file:///b:/DEV/JARVIS/mobile/lib/features/sync/presentation/conflict_detail_screen.dart#35-40)
- Save completed Q&A pair after stream finishes
- Add "Clear history" option in AppBar menu

#### Post-edit
- Run `dart run build_runner build` to regenerate [app_database.g.dart](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.g.dart)

---

## 2. Attachment Support

### Approach
Add a file picker that lets users select vault files. Selected paths are sent as `attachments` in the AskRequest. The brain's [ContextAssembler](file:///b:/DEV/JARVIS/brain/app/services/context_assembler.py#20-174) already handles attachments — we just need to pass them through.

### Files

#### [MODIFY] [chat_repository.dart](file:///b:/DEV/JARVIS/mobile/lib/features/chat/data/chat_repository.dart)
- Update [askJarvis()](file:///b:/DEV/JARVIS/mobile/lib/features/chat/data/chat_repository.dart#17-70) to accept an optional `List<String> attachments` parameter
- Include `'attachments': attachments` in the POST body

#### [MODIFY] [chat_screen.dart](file:///b:/DEV/JARVIS/mobile/lib/features/chat/presentation/chat_screen.dart)
- Add `List<String> _attachments` state
- Add attachment button (📎) next to the text field
- Show attached files as dismissible chips above the input
- Show a bottom sheet listing vault files (from [FileCacheEntries](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart#12-28)) for selection
- Pass `_attachments` to [askJarvis()](file:///b:/DEV/JARVIS/mobile/lib/features/chat/data/chat_repository.dart#17-70), clear after send

---

## 3. AI Status Indicator

### Approach
On screen init, call `GET /ask/status` to check if the AI backend is reachable. Show a chip/banner in the AppBar reflecting the state.

### Files

#### [MODIFY] [chat_repository.dart](file:///b:/DEV/JARVIS/mobile/lib/features/chat/data/chat_repository.dart)
- Add `Future<Map<String, dynamic>> getAiStatus()` method calling `GET /ask/status`

#### [MODIFY] [chat_screen.dart](file:///b:/DEV/JARVIS/mobile/lib/features/chat/presentation/chat_screen.dart)
- Add `_aiAvailable` bool state, check on [initState](file:///b:/DEV/JARVIS/mobile/lib/features/sync/presentation/conflict_detail_screen.dart#35-40)
- Show a colored chip in AppBar: green "AI Online" / red "AI Offline"
- Disable send button when offline with a tooltip

---

## Verification Plan

1. **Chat history**: Send a message → hot restart → verify messages persist
2. **Attachments**: Attach a file → send query → verify the AI response references that file's content
3. **Status**: Stop Docker containers → open chat → verify "AI Offline" displays; restart → verify it recovers
