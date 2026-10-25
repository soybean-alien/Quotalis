# Hermes usage export for Claudex: metadata only, read-only.
# Runs inside the Hermes container (python3 -), prints one JSON document to stdout.
# Never reads message text, reasoning, system prompts, titles, chat or user ids, paths or URLs.
import hashlib, json, sqlite3, sys, time

DB = sys.argv[1] if len(sys.argv) > 1 else "/opt/data/state.db"
FORMAT = 1


def sid(value):
    # Short one-way tag so rows can be linked without exporting Hermes's own ids.
    return hashlib.sha256(str(value).encode()).hexdigest()[:12] if value else None


def rows(c, sql):
    cur = c.execute(sql)
    names = [d[0] for d in cur.description]
    return [dict(zip(names, r)) for r in cur.fetchall()]


c = sqlite3.connect("file:%s?mode=ro" % DB, uri=True, timeout=10)
c.execute("BEGIN")  # one consistent snapshot while Hermes keeps writing

schema = c.execute("SELECT MAX(version) FROM schema_version").fetchone()[0]

sessions = rows(c, """
    SELECT id, parent_session_id, source, model, billing_provider, billing_mode,
           started_at, ended_at, last_activity_at, message_count, tool_call_count, api_call_count,
           input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
           estimated_cost_usd, actual_cost_usd, cost_status, cost_source, archived, hidden
    FROM sessions""")
for s in sessions:
    s["id"] = sid(s["id"])
    s["parent"] = sid(s.pop("parent_session_id"))

model_usage = rows(c, """
    SELECT session_id, model, billing_provider, task, api_call_count,
           input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
           estimated_cost_usd, actual_cost_usd, cost_status, first_seen, last_seen
    FROM session_model_usage""")
for m in model_usage:
    m["session"] = sid(m.pop("session_id"))

# Tool names and counts only (no arguments, no results).
tools = rows(c, """
    SELECT session_id, tool_name, COUNT(*) AS calls, MAX(timestamp) AS last_at
    FROM messages WHERE tool_name IS NOT NULL AND tool_name <> ''
    GROUP BY session_id, tool_name""")
for t in tools:
    t["session"] = sid(t.pop("session_id"))

latest = c.execute("""
    SELECT model, last_seen FROM session_model_usage
    WHERE model IS NOT NULL ORDER BY last_seen DESC LIMIT 1""").fetchone()
heartbeat = c.execute("SELECT MAX(last_heartbeat) FROM gateway_heartbeats").fetchone()[0]
last_activity = c.execute("SELECT MAX(last_activity_at) FROM sessions").fetchone()[0]
c.rollback()
c.close()

json.dump({
    "format": FORMAT,
    "generated_at": time.time(),
    "schema_version": schema,
    "gateway_last_heartbeat": heartbeat,
    "last_activity_at": last_activity,
    "current_model": {"model": latest[0], "last_seen": latest[1]} if latest else None,
    "sessions": sessions,
    "model_usage": model_usage,
    "tools": tools,
}, sys.stdout, separators=(",", ":"))
