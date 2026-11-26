"""Tasks across the vault, and jobs sent from the phone to the PC."""
from app.services import relay


def _write(vault, rel, text):
    p = vault / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


def test_tasks_are_found_everywhere_but_secrets_and_system(client, auth_headers, tmp_vault):
    _write(tmp_vault, "Agent/tasks.md", "# Tasks\n\n- [ ] Renew passport  (added 2026-11-20)\n")
    _write(tmp_vault, "Work/plan.md", "Notes\n- [ ] Ship phase 5\n- [x] Fix routes\n")
    _write(tmp_vault, "Secrets/s.md", "- [ ] secret task\n")
    _write(tmp_vault, "system/x.md", "- [ ] system task\n")
    data = client.get("/tasks", headers=auth_headers).json()
    texts = [t["text"] for t in data["open"]]
    assert texts[0] == "Renew passport  (added 2026-11-20)"  # the inbox first
    assert "Ship phase 5" in texts and "secret task" not in texts and "system task" not in texts
    assert [t["text"] for t in data["done"]] == ["Fix routes"]


def test_ticking_rewrites_only_that_line_and_refuses_a_stale_one(client, auth_headers, tmp_vault):
    _write(tmp_vault, "Work/plan.md", "Intro\n- [ ] Ship phase 5\n- [ ] Other\n")
    assert client.post("/tasks/toggle", json={"path": "Work/plan.md", "text": "Ship phase 5", "done": True}, headers=auth_headers).status_code == 200
    assert (tmp_vault / "Work/plan.md").read_text(encoding="utf-8") == "Intro\n- [x] Ship phase 5\n- [ ] Other\n"
    again = client.post("/tasks/toggle", json={"path": "Work/plan.md", "text": "Ship phase 5", "done": True}, headers=auth_headers)
    assert again.status_code == 409  # already ticked (on another device, say): nothing written
    assert client.post("/tasks/toggle", json={"path": "Secrets/s.md", "text": "x", "done": True}, headers=auth_headers).status_code == 403


def test_adding_creates_the_inbox_then_appends(client, auth_headers, tmp_vault):
    assert client.post("/tasks", json={"text": "  Call   mum "}, headers=auth_headers).status_code == 200
    client.post("/tasks", json={"text": "Buy milk"}, headers=auth_headers)
    text = (tmp_vault / "Agent/tasks.md").read_text(encoding="utf-8")
    assert text.startswith("# Tasks\n\n- [ ] Call mum  (added ") and "- [ ] Buy milk" in text
    assert client.post("/tasks", json={"text": "   "}, headers=auth_headers).status_code == 422


def test_a_job_goes_queued_running_finished(client, auth_headers):
    job = client.post("/relay/jobs", json={"brief": "Research three laptops under 80k and compare them"}, headers=auth_headers).json()
    assert job["status"] == "queued" and job["title"] == "Research three laptops under 80k and…"
    claimed = client.post("/relay/jobs/claim", headers=auth_headers).json()["job"]
    assert claimed["id"] == job["id"] and claimed["status"] == "running"
    assert client.post("/relay/jobs/claim", headers=auth_headers).json()["job"] is None  # nothing else queued
    done = client.post(f"/relay/jobs/{job['id']}", json={"status": "finished", "result": "Here are three."}, headers=auth_headers).json()
    assert done["status"] == "finished"
    assert client.get("/relay/jobs", headers=auth_headers).json()["jobs"][0]["result"] == "Here are three."


def test_secret_looking_briefs_are_refused_and_jobs_need_a_device(client, auth_headers):
    assert client.post("/relay/jobs", json={"brief": "log in with password: hunter22"}, headers=auth_headers).status_code == 422
    assert client.get("/relay/jobs").status_code in (401, 403, 422)
    assert client.post("/relay/jobs/nope", json={"status": "finished"}, headers=auth_headers).status_code == 404
    assert client.post("/relay/jobs/x", json={"status": "deleted"}, headers=auth_headers).status_code == 422


def test_a_job_left_running_is_failed_after_a_while(client, auth_headers, monkeypatch):
    client.post("/relay/jobs", json={"brief": "Summarise the news"}, headers=auth_headers)
    client.post("/relay/jobs/claim", headers=auth_headers)
    monkeypatch.setattr(relay, "STALE_RUNNING", relay.timedelta(seconds=-1))
    assert client.get("/relay/jobs", headers=auth_headers).json()["jobs"][0]["status"] == "failed"
