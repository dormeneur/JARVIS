"""Stand-in for the `claude` CLI: replays stream-json the way `claude -p` emits
it, so the runner is tested without spending tokens. Behaviour is picked by
the prompt text."""
import json
import os
import sys
import time
from pathlib import Path

argv = sys.argv[1:]
if argv[:2] == ["auth", "status"]:
    print("Logged in (fake)")
    sys.exit(0)

prompt = argv[argv.index("-p") + 1]
Path(os.environ["SCRATCHPAD"], "argv.json").write_text(json.dumps(argv), encoding="utf-8")


def emit(msg):
    print(json.dumps(msg), flush=True)


def result(envelope=None, text="", is_error=False):
    msg = {"type": "result", "subtype": "success", "is_error": is_error, "result": text,
           "session_id": "sess-1", "usage": {"input_tokens": 100, "output_tokens": 20},
           "total_cost_usd": 0.01}
    if envelope is not None:
        msg["structured_output"] = envelope
    emit(msg)


emit({"type": "system", "subtype": "init", "session_id": "sess-1"})

if prompt.startswith("/apply"):
    emit({"type": "assistant", "message": {"content": [
        {"type": "tool_use", "name": "Bash", "input": {"command": "python3 tools/posting_guard.py --text --json"}}]}})
    result({"status": "awaiting_input", "stage": "evaluate", "report_markdown": "Strong fit",
            "question": "Should I proceed with drafting?", "options": ["Draft", "Stop"],
            "data": {"fit_score": 78}})
elif prompt == "Draft":
    tracker = Path("job_search_tracker.csv")
    if not tracker.exists():
        tracker.write_text("date,company,role,status\n", encoding="utf-8")
    with tracker.open("a", encoding="utf-8") as f:
        f.write("2026-10-27,Acme,ML Engineer,drafted\n")
    emit({"type": "user", "message": {"content": [
        {"type": "tool_result", "is_error": True, "content": "Permission to use Bash(curl x) has been denied"}]}})
    result({"status": "done", "report_markdown": "Drafted",
            "artifacts": [{"kind": "cv_pdf", "path": "cv/main_acme_ml_engineer.pdf"}]})
elif prompt.startswith("/rank"):
    result(text='Ranked.\n```json\n{"status": "done", "report_markdown": "3 ranked"}\n```')
elif prompt == "CORRUPT":
    Path("job_search_tracker.csv").write_text("garbage\n", encoding="utf-8")
    result({"status": "done", "report_markdown": "oops"})
elif prompt == "PROFILE":
    Path("CLAUDE.md").write_text("# profile\n- new fact\n", encoding="utf-8")
    result({"status": "done", "report_markdown": "saved a fact"})
elif prompt == "NOISY":
    sys.stderr.write("x" * 1_000_000)  # far more than a pipe buffer holds
    sys.stderr.flush()
    result({"status": "done", "report_markdown": "loud but fine"})
elif prompt == "SLEEP":
    time.sleep(30)
else:
    result(text="plain answer")
