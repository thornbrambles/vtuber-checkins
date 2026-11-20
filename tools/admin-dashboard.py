#!/usr/bin/env python3
"""Local-only, snapshot-backed administration.

python tools/admin-dashboard.py --backup "C:/.../checkins-2026....json"
Then open the printed loopback URL. Nothing connects to Twitch or the running
Streamer.bot process. CSV export is available for approved games.
"""
import argparse
import re
import html
import importlib.util
import json
import pathlib
import secrets
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlsplit

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("checkin_ops", ROOT / "tools/checkin-ops.py")
ops = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)
wheel_export = ops.wheel_export


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


def page(data, token, notice, query=""):
    viewer_rows = []
    normalized = query.strip().casefold()
    matched = [user for user in data["users"]
               if not normalized or normalized in str(user.get("userName", "")).casefold()
               or normalized in str(user["userId"])]
    for u in sorted(matched, key=lambda x: (x.get("userName") or "").lower())[:500]:
        v = u["vars"]
        uid = e(u["userId"])
        viewer_rows.append(
            "<tr><td>" + e(u.get("userName", uid)) + "<br><small>" + uid + "</small></td>"
            "<td>" + e(v.get("ci_total") or 0) + " · " + e(v.get("ci_streak") or 0) + "</td>"
            "<td>" + e(max(0, max(v.get("ci_wheel_earned") or 0, (v.get("ci_total") or 0)//10) - (v.get("ci_wheel_claimed") or 0))) + "</td></tr>"
        )
    months = sorted((k[-7:].replace("_", "-") for k in data["globals"] if k.startswith("ci_wheel_month_")), reverse=True)
    game_rows = []
    for month in months:
        _, entries = month_entries(data, month)
        for x in entries:
            if x.get("replacedBy"):
                continue
            status = wheel_export.entry_status(x)
            entry_id = e(x.get("id", ""))
            game_rows.append(
                "<tr><td>" + e(month) + "</td><td>" + e(x.get("game")) + "</td><td>" + e(x.get("user")) +
                "</td><td>" + e(status) + (" (removed)" if x.get("removed") else "") +
                "</td><td><code>" + entry_id + "</code></td></tr>"
            )
    report = ops.reconcile(data)
    html_rows = "\n".join(viewer_rows)
    games_html = "\n".join(game_rows)
    search_form = (
        "<form method='get' action='/'>"
        "<input type='hidden' name='token' value='" + e(token) + "'>"
        "<input name='q' placeholder='Search viewer' style='width:240px' value='" + e(query) + "'>"
        "<button>Search</button></form>"
    )
    wheel_forms = "".join(
        "<form method='post' action='/exportwheel' style='display:inline'>"
        "<input type='hidden' name='token' value='" + e(token) + "'>"
        "<input type='hidden' name='month' value='" + e(month) + "'>"
        "<button>Export " + e(month) + " CSV</button></form>"
        for month in months[:6]
    )
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
<p><strong>Read-only snapshot</strong> · Use <code>!checkinadmin</code> and <code>!wheeladmin</code> in Twitch chat to make updates, including between streams.</p>
<article>""" + e(notice) + """</article>
<article><strong>Snapshot: </strong>""" + str(len(data["users"])) + """ viewers ·
<strong>Wheel entries:</strong> """ + str(report["wheelEntryCount"]) + """ ·
<strong>Reconciliation findings:</strong> <span class="warn">""" + str(len(report["findings"])) + """</span>
</article>
<h2>Viewer records</h2>""" + search_form + """<table><thead><tr><th>Viewer</th><th>Total · Streak</th><th>Credits</th></tr></thead><tbody>""" + html_rows + """</tbody></table>
<h2>Game submissions</h2>""" + wheel_forms + """<table><thead><tr><th>Month</th><th>Game</th><th>Viewer</th><th>Status</th><th>Entry ID</th></tr></thead><tbody>""" + games_html + """</tbody></table>
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
            self.respond(200, page(data, token, "No changes are applied to the live bot.",
                                   parse_qs(url.query).get("q", [""])[0][:100]))

        def do_POST(self):
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
            if self.path != "/exportwheel":
                self.respond(404, "Unknown action", "text/plain")
                return
            try:
                month = params.get("month", [""])[0]
                if not re.fullmatch(r"\d{4}-(0[1-9]|1[0-2])", month):
                    raise ops.CheckinError("Invalid month")
                output = pathlib.Path(output_dir)
                output.mkdir(parents=True, exist_ok=True)
                target = output / ("wheel-" + month + "-" + secrets.token_hex(6) + ".csv")
                count = wheel_export.export(data, month, target)
                self.respond(200, page(data, token, f"Exported {count} approved games to {target}."))
            except (ops.CheckinError, ValueError, OSError) as exc:
                self.respond(400, page(data, token, "ERROR: " + str(exc)))

        def log_message(self, fmt, *args):
            return  # Do not log local auth tokens or viewer data into console.

    server = HTTPServer(("127.0.0.1", port), Handler)
    print(f"Local-only admin dashboard: http://127.0.0.1:{port}/?token={token}", flush=True)
    print("Read-only snapshot. Press Ctrl+C to stop.")
    try:
        server.serve_forever()
    finally:
        server.server_close()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--backup", required=True, help="Path to verified snapshot JSON")
    p.add_argument("--out-dir", default=".sandbox/exports", help="Private output folder")
    p.add_argument("--port", type=int, default=8765)
    args = p.parse_args()
    if not 1024 <= args.port <= 65535:
        p.error("--port must be between 1024 and 65535")
    serve(pathlib.Path(args.backup), args.out_dir, args.port)


if __name__ == "__main__":
    main()
