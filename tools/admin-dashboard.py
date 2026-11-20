#!/usr/bin/env python3
"""Local-only, snapshot-backed administration.

python tools/admin-dashboard.py --backup "C:/.../checkins-2026....json"
Then open the printed loopback URL. Nothing connects to Twitch or the running
Streamer.bot process. Save produces a NEW validated backup for review/import.
"""
import argparse
import datetime as dt
import hashlib
import html
import importlib.util
import json
import pathlib
import secrets
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("checkin_ops", ROOT / "tools/checkin-ops.py")
ops = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)


def e(value):
    return html.escape(str(value if value is not None else ""), quote=True)


def month_entries(data, month):
    key = "ci_wheel_month_" + month.replace("-", "_")
    raw = data["globals"].get(key)
    try:
        items = json.loads(raw) if raw else []
        if not isinstance(items, list):
            raise ValueError("Monthly ledger is not an array")
    except (ValueError, TypeError) as exc:
        raise ops.CheckinError("Invalid wheel data: " + str(exc))
    return key, items


def append_audit(data, record):
    key = "ci_wheel_audit_json" if record["type"] == "WHEEL_REVIEW" else "ci_events_month_" + dt.datetime.now(dt.timezone.utc).strftime("%Y_%m")
    current = data["globals"].get(key)
    try:
        items = json.loads(current) if current else []
        if not isinstance(items, list):
            raise ValueError("Audit storage is not a JSON array")
    except (ValueError, TypeError) as exc:
        raise ops.CheckinError("Invalid audit data: " + str(exc))
    record["id"] = "offline-" + secrets.token_hex(16)
    record["atUtc"] = dt.datetime.now(dt.timezone.utc).isoformat()
    record["actor"] = "local-dashboard"
    items.append(record)
    data["globals"][key] = json.dumps(items, separators=(",", ":"), ensure_ascii=False)


def edit_user(data, user_id, total, streak):
    matches = [u for u in data["users"] if u["userId"] == user_id]
    if len(matches) != 1:
        raise ops.CheckinError("Viewer not found")
    if not all(type(x) is int and 0 <= x <= 1_000_000 for x in (total, streak)):
        raise ops.CheckinError("Counts must be whole numbers from 0 to 1,000,000")
    user = matches[0]
    old_total = user["vars"].get("ci_total", 0)
    old_streak = user["vars"].get("ci_streak", 0)
    user["vars"]["ci_total"] = total
    user["vars"]["ci_streak"] = streak if total else 0
    spent = user["vars"].get("ci_wheel_claimed", 0)
    user["vars"]["ci_wheel_earned"] = max(
        user["vars"].get("ci_wheel_earned", 0), spent, total // 10)
    append_audit(data, {
        "type": "ADMIN_EDIT", "userId": user_id,
        "totalBefore": old_total, "totalAfter": total,
        "streakBefore": old_streak, "streakAfter": user["vars"]["ci_streak"]
    })


def review_entry(data, month, entry_id, decision):
    if decision not in ("approved", "rejected"):
        raise ops.CheckinError("Only approve/reject is supported")
    key, entries = month_entries(data, month)
    matches = [x for x in entries if x.get("id") == entry_id]
    if len(matches) != 1:
        raise ops.CheckinError("Game entry not found or duplicate ID")
    entry = matches[0]
    if entry.get("removed") or entry.get("replacedBy"):
        raise ops.CheckinError("Cannot review removed/replaced game")
    if decision == "approved":
        title = " ".join(entry.get("game", "").split()).casefold()
        if any(x is not entry and not x.get("removed") and x.get("status", "approved") == "approved"
               and " ".join(x.get("game", "").split()).casefold() == title for x in entries):
            raise ops.CheckinError("Same game already approved this month")
    before = entry.get("status", "approved")
    entry["status"] = decision
    data["globals"][key] = json.dumps(entries, separators=(",", ":"), ensure_ascii=False)
    append_audit(data, {
        "type": "WHEEL_REVIEW", "operation": decision,
        "entryId": entry_id, "month": month, "statusBefore": before,
        "statusAfter": decision
    })


def export_snapshot(data, original, output_dir):
    ops.check_backup(data)
    output_dir = pathlib.Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    suffix = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d-%H%M%S-%f")
    dest = output_dir / ("staged-" + suffix + ".json")
    if dest.resolve() == pathlib.Path(original).resolve():
        raise ops.CheckinError("Refusing to overwrite original backup")
    payload = (json.dumps(data, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    with dest.open("xb") as handle:
        handle.write(payload)
    digest = hashlib.sha256(payload).hexdigest()
    with pathlib.Path(str(dest) + ".sha256").open("x", encoding="ascii") as handle:
        handle.write(digest + "  " + dest.name + "\n")
    return dest


def page(data, token, notice):
    from html import escape
    viewer_rows = []
    for u in sorted(data["users"], key=lambda x: (x.get("userName") or "").lower())[:500]:
        v = u["vars"]
        uid = e(u["userId"])
        viewer_rows.append(
            "<tr><td>" + e(u.get("userName", uid)) + "<br><small>" + uid + "</small></td>"
            "<td><form method='post' action='/edit'>"
            "<input type='hidden' name='token' value='" + e(token) + "'>"
            "<input type='hidden' name='userId' value='" + uid + "'>"
            "<input type='number' name='total' min='0' max='1000000' value='" + e(v.get("ci_total", 0)) + "'>"
            "<input type='number' name='streak' min='0' max='1000000' value='" + e(v.get("ci_streak", 0)) + "'>"
            "<button>Stage edit</button></form></td>"
            "<td>" + e(max(v.get("ci_wheel_earned", 0),v.get("ci_total",0)//10)-v.get("ci_wheel_claimed",0)) + "</td></tr>"
        )
    months = sorted((k[-7:].replace("_", "-") for k in data["globals"] if k.startswith("ci_wheel_month_")), reverse=True)
    game_rows = []
    for month in months:
        _, entries = month_entries(data, month)
        for x in entries:
            if x.get("replacedBy"):
                continue
            status = x.get("status", "approved")
            entry_id = e(x.get("id", ""))
            actions = ""
            if not x.get("removed"):
                for outcome in ("approved", "rejected"):
                    actions += ("<button name='decision' value='" + outcome + "'>" + outcome.capitalize() + "</button>")
            game_rows.append(
                "<tr><td>" + e(month) + "</td><td>" + e(x.get("game")) + "</td><td>" + e(x.get("user")) +
                "</td><td>" + e(status) + (" (removed)" if x.get("removed") else "") +
                "</td><td><form action='/review' method='post'>"
                "<input type='hidden' name='token' value='" + e(token) + "'>"
                "<input type='hidden' name='month' value='" + e(month) + "'>"
                "<input type='hidden' name='entryId' value='" + entry_id + "'>"
                + actions + "</form></td></tr>"
            )
    report = ops.reconcile(data)
    html_rows = "\n".join(viewer_rows)
    games_html = "\n".join(game_rows)
    return """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Check-in Admin · Offline</title>
<style>
body{font:15px system-ui, sans-serif;max-width:1100px;margin:30px auto;padding:0 18px;color:#21362c;background:#f6f8f5}
h1{font-size:25px}h2{font-size:19px;margin-top:36px}small{color:#657368}
table{width:100%;border-collapse:collapse;background:white}td,th{border-bottom:1px solid #dae2d9;padding:10px;text-align:left}
input{width:75px;padding:6px;margin:4px;border:1px solid #b9caba;border-radius:4px}
button{padding:7px 10px;background:#2a6247;color:white;border:0;border-radius:4px;margin:2px;cursor:pointer}
article{background:white;border:1px solid #d5dfd5;border-radius:8px;padding:14px;margin:18px 0}
.warn{color:#9c3d1b}body{overflow-wrap:break-word}
</style></head><body>
<h1>🌿 Check-in administration</h1>
<p><strong>OFFLINE SNAPSHOT ONLY</strong> · Edits are staged in memory and never touch Streamer.bot or Twitch.</p>
<article>""" + e(notice) + """</article>
<article><strong>Snapshot: </strong>""" + str(len(data["users"])) + """ viewers ·
<strong>Wheel entries:</strong> """ + str(report["wheelEntryCount"]) + """ ·
<strong>Reconciliation findings:</strong> <span class="warn">""" + str(len(report["findings"])) + """</span>
<p>Review entries and viewer edits before exporting. Import requires a separate, explicit Streamer.bot action.</p>
<form action="/save" method="post">
<input type="hidden" name="token" value='""" + e(token) + """'>
<button>Save staged JSON + SHA-256</button></form></article>
<h2>Viewer records</h2><table><thead><tr><th>Viewer</th><th>Total · Streak</th><th>Credits</th></tr></thead><tbody>""" + html_rows + """</tbody></table>
<h2>Game submissions</h2><table><thead><tr><th>Month</th><th>Game</th><th>Viewer</th><th>Status</th><th>Review</th></tr></thead><tbody>""" + games_html + """</tbody></table>
<p><small>Local snapshot management only. Keep these exports private.</small></p></body></html>"""


def serve(snapshot, output_dir, port):
    data = ops.load_backup(snapshot)
    token = secrets.token_urlsafe(32)

    class Handler(BaseHTTPRequestHandler):
        def respond(self, code, body, typ="text/html; charset=utf-8"):
            blob = body.encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", typ)
            self.send_header("Content-Length", str(len(blob)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.end_headers()
            self.wfile.write(blob)

        def do_GET(self):
            url = urlsplit(self.path)
            provided = parse_qs(url.query).get("token", [""])[0]
            if url.path != "/" or not secrets.compare_digest(provided, token):
                self.respond(403, "Access denied", "text/plain")
                return
            self.respond(200, page(data, token, "No changes are applied to the live bot."))

        def do_POST(self):
            global data
            origin = self.headers.get("Origin", "")
            if origin and origin not in (f"http://127.0.0.1:{port}", f"http://localhost:{port}"):
                self.respond(403, "Invalid origin", "text/plain")
                return
            length = self.headers.get("Content-Length", "0")
            if not length.isdigit() or int(length) > 16384:
                self.respond(413, "Invalid request size", "text/plain")
                return
            params = parse_qs(self.rfile.read(int(length)).decode("utf-8"), keep_blank_values=True)
            provided = params.get("token", [""])[0]
            if not secrets.compare_digest(provided, token):
                self.respond(403, "Access denied", "text/plain")
                return
            def field(name):
                return params.get(name, [""])[0]
            try:
                if self.path == "/edit":
                    edit_user(data, field("userId"), int(field("total")), int(field("streak")))
                    notice = "Viewer edit staged. Save to write a new backup file."
                elif self.path == "/review":
                    review_entry(data, field("month"), field("entryId"), field("decision"))
                    notice = "Game review staged. Save to write a new backup file."
                elif self.path == "/save":
                    saved = export_snapshot(data, snapshot, output_dir)
                    notice = "Saved staged snapshot: " + str(saved) + ". Import it manually after another backup."
                else:
                    self.respond(404, "Unknown action", "text/plain")
                    return
                self.respond(200, page(data, token, notice))
            except (ops.CheckinError, ValueError, OSError) as exc:
                self.respond(400, page(data, token, "ERROR: " + str(exc)))

        def log_message(self, fmt, *args):
            return  # Do not log local auth tokens or viewer data into console.

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"Local-only admin dashboard: http://127.0.0.1:{port}/?token={token}", flush=True)
    print("Snapshot edits are staged. Press Ctrl+C to stop; no live Streamer.bot connection.")
    try:
        server.serve_forever()
    finally:
        server.server_close()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--backup", required=True, help="Path to verified snapshot JSON")
    p.add_argument("--out-dir", default=".sandbox/staged", help="Private output folder")
    p.add_argument("--port", type=int, default=8765)
    args = p.parse_args()
    if not 1024 <= args.port <= 65535:
        p.error("--port must be between 1024 and 65535")
    serve(pathlib.Path(args.backup), args.out_dir, args.port)


if __name__ == "__main__":
    main()
