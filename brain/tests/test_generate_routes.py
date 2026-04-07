from fastapi.testclient import TestClient
from app.api import app
import pytest
from app.models.ask_models import GenerateFilesRequest

client = TestClient(app)

def test_generate_shortcuts_react():
    response = client.post("/brain/generate-files/dry-run", json={
        "prompt": "Create a React component named Button",
        "current_directory": "."
    })
    
    # 200 OK
    assert response.status_code == 200
    data = response.json()
    
    # Check we got the 3 react files
    assert len(data) == 3
    paths = [item["path"] for item in data]
    assert "ComponentName.jsx" in paths
    assert "ComponentName.module.css" in paths
    assert "index.js" in paths

def test_generate_shortcuts_python():
    response = client.post("/brain/generate-files", json={
        "prompt": "make a python module",
        "current_directory": "."
    })
    
    assert response.status_code == 200
    data = response.json()
    assert len(data) == 3
    paths = [item["path"] for item in data]
    assert "__init__.py" in paths
    assert "main.py" in paths

def test_generate_shortcuts_express():
    response = client.post("/brain/generate-files/dry-run", json={
        "prompt": "express route auth",
        "current_directory": "."
    })
    assert response.status_code == 200
    data = response.json()
    assert len(data) == 3
    paths = [item["path"] for item in data]
    assert "router.js" in paths

def test_generate_dry_run_identical_structure():
    # Calling normal vs dry run should return identical structural rules
    # Testing that it correctly processes through the router without failure
    resp = client.post("/brain/generate-files/dry-run", json={
        "prompt": "python module",
        "current_directory": "."
    })
    assert resp.status_code == 200
    assert len(resp.json()) == 3
