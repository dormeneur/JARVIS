import pytest
from app.services.sanitizer import sanitize_path, sanitize_manifest

def test_sanitize_path_traversal():
    # Should squash to flat level and remove bad absolute markers
    assert sanitize_path("../../etc/passwd") == "passwd"
    assert sanitize_path("C:\\Windows\\System32\\cmd.exe") == "cmd.exe"
    assert sanitize_path("/var/log/syslog") == "syslog"
    assert sanitize_path("nested/folder/file.txt") == "file.txt"
    assert sanitize_path("..\\something\\evil.sh") == "evil.sh"
    
def test_sanitize_manifest_allowlist():
    raw_manifest = [
        {"path": "good.txt", "content": "hello", "type": "file"},
        {"path": "bad.exe", "content": "malware", "type": "file"},
        {"path": "script.py", "content": "print()", "type": "file"}
    ]
    
    clean = sanitize_manifest(raw_manifest)
    assert len(clean) == 2
    assert clean[0]["path"] == "good.txt"
    assert clean[1]["path"] == "script.py"

def test_sanitize_manifest_truncation():
    # Generate 60KB string
    huge_content = "A" * (60 * 1024)
    raw_manifest = [
        {"path": "big.md", "content": huge_content, "type": "file"}
    ]
    clean = sanitize_manifest(raw_manifest)
    assert len(clean) == 1
    assert "big.md" in clean[0]["path"]
    
    content = clean[0]["content"]
    assert "[TRUNCATED BY SYSTEM]" in content
    
    # 50KB limit + length of truncate msg ~ 51221 chars
    assert len(content) <= (50 * 1024) + 100

def test_sanitize_manifest_fixes_missing():
    raw_manifest = [
        # missing content
        {"path": "empty.md", "type": "file"}
    ]
    clean = sanitize_manifest(raw_manifest)
    assert len(clean) == 1
    assert "Add your content here" in clean[0]["content"]

    raw2 = [
        # missing name
        {"type": "file", "content": "some text"}
    ]
    clean2 = sanitize_manifest(raw2)
    assert len(clean2) == 1
    assert clean2[0]["path"] == "untitled.txt"
    
    raw3 = [
        # missing extension
        {"path": "missing_ext", "type": "file", "content": "text"}
    ]
    clean3 = sanitize_manifest(raw3)
    assert clean3[0]["path"] == "missing_ext.txt"

def test_sanitize_manifest_cap():
    raw_manifest = [{"path": f"f_{i}.txt", "content": "x"} for i in range(30)]
    clean = sanitize_manifest(raw_manifest)
    assert len(clean) == 20
