"""
Deterministic manual concurrency simulation test for version-based conflict detection.

This test proves that version-based conflict detection works correctly by simulating
two devices editing the same file concurrently, where one device has stale version info.
"""

import tempfile
from datetime import datetime, timezone
from pathlib import Path

import pytest


class TestVersionConflictSimulation:
    """
    Simulates concurrent edits from two devices to prove version-based conflict detection.
    
    Scenario:
    1. Device A and Device B both have file at version 1
    2. Device A pushes update → version becomes 2
    3. Device B tries to push with base_version=1 (stale) → CONFLICT
    """

    def test_concurrent_edit_conflict_detection(self):
        """
        OBJECTIVE: Prove that base_version != current_server_version triggers conflict.
        
        This test does NOT use timestamp fallback - it uses ONLY version-based detection.
        """
        from app.services import sync
        from app.services.version_tracker import VersionTracker
        from app.config import Settings
        
        print("\n" + "="*70)
        print("VERSION-BASED CONFLICT DETECTION SIMULATION")
        print("="*70)
        
        # ===== STEP 1: SETUP =====
        print("\n[STEP 1] Setup: Create clean vault and initialize file")
        
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create settings with temporary vault
            test_settings = Settings(vault_path=vault_path)
            
            # Patch settings for this test
            import app.services.sync as sync_module
            import app.services.version_tracker as version_tracker_module
            original_sync_settings = sync_module.settings
            original_vt_settings = version_tracker_module.settings
            sync_module.settings = test_settings
            version_tracker_module.settings = test_settings
            
            try:
                # Create initial file
                initial_content = b"Initial content"
                initial_mtime = datetime(2026, 1, 1, tzinfo=timezone.utc)
                
                result_path, is_conflict, version = sync.push_file(
                    "test.md",
                    initial_content,
                    initial_mtime,
                    base_version=None  # First write, no base version
                )
                
                print(f"  ✓ Created test.md")
                print(f"  ✓ Initial version: {version}")
                print(f"  ✓ Content: {initial_content.decode()}")
                
                assert version == 1, f"Expected version 1, got {version}"
                assert not is_conflict, "Initial write should not be a conflict"
                assert result_path == "test.md"
                
                # Verify version tracker state
                tracker = VersionTracker(vault_path / "system" / "file_versions.db")
                stored_version = tracker.get_version("test.md")
                print(f"  ✓ Version tracker confirms version: {stored_version}")
                assert stored_version == 1
                
                # ===== STEP 2: DEVICE A UPDATE =====
                print("\n[STEP 2] Device A: Push update with base_version=1")
                
                device_a_content = b"Device A change"
                device_a_mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
                
                result_path_a, is_conflict_a, version_a = sync.push_file(
                    "test.md",
                    device_a_content,
                    device_a_mtime,
                    base_version=1  # Device A has version 1, expects to increment to 2
                )
                
                print(f"  ✓ Device A push completed")
                print(f"  ✓ Conflict detected: {is_conflict_a}")
                print(f"  ✓ New version: {version_a}")
                print(f"  ✓ Content: {device_a_content.decode()}")
                
                assert not is_conflict_a, "Device A should not have conflict"
                assert version_a == 2, f"Expected version 2, got {version_a}"
                assert result_path_a == "test.md"
                
                # Verify file content
                file_content_after_a = (vault_path / "test.md").read_bytes()
                assert file_content_after_a == device_a_content
                print(f"  ✓ Server file content verified: {file_content_after_a.decode()}")
                
                # Verify version tracker
                stored_version_a = tracker.get_version("test.md")
                print(f"  ✓ Version tracker confirms version: {stored_version_a}")
                assert stored_version_a == 2
                
                # ===== STEP 3: DEVICE B UPDATE (STALE VERSION) =====
                print("\n[STEP 3] Device B: Push update with base_version=1 (STALE)")
                print("  → Device B still thinks server is at version 1")
                print("  → But server is actually at version 2")
                print("  → This should trigger CONFLICT")
                
                device_b_content = b"Device B change"
                device_b_mtime = datetime(2026, 3, 1, tzinfo=timezone.utc)
                
                result_path_b, is_conflict_b, version_b = sync.push_file(
                    "test.md",
                    device_b_content,
                    device_b_mtime,
                    base_version=1  # STALE! Server is at version 2
                )
                
                print(f"\n  ✓ Device B push completed")
                print(f"  ✓ Conflict detected: {is_conflict_b}")
                print(f"  ✓ Returned version: {version_b}")
                print(f"  ✓ Returned path: {result_path_b}")
                
                # ===== STEP 4: VERIFY CONFLICT BEHAVIOR =====
                print("\n[STEP 4] Verify conflict behavior")
                
                # CRITICAL ASSERTIONS
                assert is_conflict_b, "❌ FAILED: Conflict should be detected!"
                assert version_b is None, "❌ FAILED: Conflict should return None version!"
                assert "_conflict_" in result_path_b, f"❌ FAILED: Should create conflict file, got {result_path_b}"
                
                print(f"  ✓ Conflict correctly detected")
                print(f"  ✓ Conflict file created: {result_path_b}")
                
                # Verify original file is PRESERVED
                original_file_content = (vault_path / "test.md").read_bytes()
                assert original_file_content == device_a_content, \
                    f"❌ FAILED: Original file was overwritten! Expected '{device_a_content.decode()}', got '{original_file_content.decode()}'"
                print(f"  ✓ Original file preserved: {original_file_content.decode()}")
                
                # Verify original file version is UNCHANGED
                original_version = tracker.get_version("test.md")
                assert original_version == 2, \
                    f"❌ FAILED: Original version changed! Expected 2, got {original_version}"
                print(f"  ✓ Original file version unchanged: {original_version}")
                
                # Verify conflict file exists
                conflict_file = vault_path / result_path_b
                assert conflict_file.exists(), f"❌ FAILED: Conflict file not created at {result_path_b}"
                conflict_content = conflict_file.read_bytes()
                assert conflict_content == device_b_content, \
                    f"❌ FAILED: Conflict file has wrong content"
                print(f"  ✓ Conflict file exists: {result_path_b}")
                print(f"  ✓ Conflict file content: {conflict_content.decode()}")
                
                # Verify conflict file has version tracking
                conflict_version = tracker.get_version(result_path_b)
                assert conflict_version == 1, \
                    f"❌ FAILED: Conflict file should have version 1, got {conflict_version}"
                print(f"  ✓ Conflict file version: {conflict_version}")
                
                # ===== STEP 5: VERIFY VERSION TABLE STATE =====
                print("\n[STEP 5] Verify version table state")
                
                # Query all version entries
                import sqlite3
                db_path = vault_path / "system" / "file_versions.db"
                with sqlite3.connect(db_path) as conn:
                    cursor = conn.execute("SELECT path, version, last_hash FROM file_versions ORDER BY path")
                    rows = cursor.fetchall()
                    
                    print("\n  Version table contents:")
                    print("  " + "-" * 60)
                    print(f"  {'Path':<40} {'Version':<10} {'Hash':<20}")
                    print("  " + "-" * 60)
                    for row in rows:
                        path, version, hash_val = row
                        hash_short = hash_val[:20] + "..." if len(hash_val) > 20 else hash_val
                        print(f"  {path:<40} {version:<10} {hash_short:<20}")
                    print("  " + "-" * 60)
                    
                    # Verify we have exactly 2 entries
                    assert len(rows) == 2, f"Expected 2 version entries, got {len(rows)}"
                    
                    # Verify original file entry
                    original_entry = [r for r in rows if r[0] == "test.md"][0]
                    assert original_entry[1] == 2, "Original file should be version 2"
                    
                    # Verify conflict file entry
                    conflict_entries = [r for r in rows if "_conflict_" in r[0]]
                    assert len(conflict_entries) == 1, "Should have exactly 1 conflict file"
                    assert conflict_entries[0][1] == 1, "Conflict file should be version 1"
                
                # ===== FINAL SUMMARY =====
                print("\n" + "="*70)
                print("VERSION CONFLICT TEST RESULT")
                print("="*70)
                print(f"\nInitial version: 1")
                print(f"After Device A: version = 2")
                print(f"\nAfter Device B (with stale base_version=1):")
                print(f"  Conflict created: {is_conflict_b}")
                print(f"  Conflict filename: {result_path_b}")
                print(f"  Server file content: '{original_file_content.decode()}'")
                print(f"  Conflict file content: '{conflict_content.decode()}'")
                print(f"  Final version of original: {original_version}")
                print(f"  Version of conflict file: {conflict_version}")
                
                print("\nVersion table rows:")
                for row in rows:
                    print(f"  {row[0]}: version={row[1]}, hash={row[2][:16]}...")
                
                print("\n" + "="*70)
                print("✅ ALL ASSERTIONS PASSED")
                print("="*70)
                print("\nConclusion:")
                print("  • Version-based conflict detection works correctly")
                print("  • base_version != current_server_version triggers conflict")
                print("  • Original file is preserved at correct version")
                print("  • Conflict file is created with version tracking")
                print("  • No silent data loss occurs")
                print("="*70 + "\n")
                
            finally:
                # Restore original settings
                sync_module.settings = original_sync_settings
                version_tracker_module.settings = original_vt_settings


if __name__ == "__main__":
    # Run the test directly
    test = TestVersionConflictSimulation()
    test.test_concurrent_edit_conflict_detection()
