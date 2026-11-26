"""Read the DeepFind test-runner tracking journal without modifying it."""
import json
import os
from pathlib import Path
import sqlite3
import sys

if os.environ.get("GITHUB_ACTIONS") != "true":
    raise SystemExit("Only intended for a disposable CI runner.")
runner = Path(os.environ["RUNNER_TEMP"]).resolve()
database = Path(sys.argv[1]).resolve()
if runner not in database.parents or database.name != "tracking.db":
    raise SystemExit("Refusing to inspect a non-test database.")

try:
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True, timeout=1) as db:
        count = db.execute("SELECT count(*) FROM files").fetchone()[0]
        requested, completed = db.execute("SELECT requested,completed FROM scan_jobs WHERE id=1").fetchone()
        print(json.dumps(dict(count=count, requested=requested, completed=completed)))
except sqlite3.Error:
    # Index startup or a concurrent write may briefly make the journal unavailable.
    pass
