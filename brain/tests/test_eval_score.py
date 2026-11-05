from app.evals.retrieval import aggregate, load_golden, score


def test_perfect_and_miss():
    assert score(["a.md", "b.md"], ["a.md"]) == {"hit1": 1.0, "hit5": 1.0, "rr": 1.0}
    assert score(["x.md", "y.md"], ["a.md"]) == {"hit1": 0.0, "hit5": 0.0, "rr": 0.0}


def test_rank_three_counts_for_hit5_not_hit1():
    r = score(["x", "y", "a.md"], ["a.md"])
    assert r["hit1"] == 0.0 and r["hit5"] == 1.0 and abs(r["rr"] - 1 / 3) < 1e-9


def test_rank_seven_misses_hit5_but_has_rr():
    r = score(["1", "2", "3", "4", "5", "6", "a.md"], ["a.md"])
    assert r["hit5"] == 0.0 and abs(r["rr"] - 1 / 7) < 1e-9


def test_any_expected_path_counts_and_paths_are_normalised():
    assert score(["Profile\\Work.md"], ["profile/work.md", "other.md"])["hit1"] == 1.0


def test_aggregate_and_empty():
    rows = [score(["a"], ["a"]), score(["x"], ["a"])]
    assert aggregate(rows) == {"n": 2, "hit1": 0.5, "hit5": 0.5, "mrr": 0.5}
    assert aggregate([])["n"] == 0


def test_load_golden_skips_blank_comments_and_incomplete(tmp_path):
    f = tmp_path / "g.jsonl"
    f.write_text('# note\n\n{"q": "x", "expect": ["a.md"]}\n{"q": "no expect"}\n', encoding="utf-8")
    assert load_golden(f) == [{"q": "x", "expect": ["a.md"]}]
