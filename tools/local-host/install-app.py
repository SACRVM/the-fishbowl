"""Installs Fishbowl as an app in the launcher's own Chromium profile.

    python3 -I install-app.py <chromium> <profile dir> <url>

Prints the app's id. An installed app (a web app with a manifest), not a
plain --app window: its window is named after the app, carries the
manifest's icon, and can give its title bar to the page (the manifest's
window-controls-overlay). Talks to Chromium over --remote-debugging-pipe
(fds 3 and 4), so no port is open while it runs. Standard library only.
"""
import json
import os
import subprocess
import sys
import threading
import time
from urllib.parse import urlsplit

chromium, profile, url = sys.argv[1:4]
parts = urlsplit(url)
origin = f"{parts.scheme}://{parts.netloc}"
manifest_id = origin + "/"  # the manifest's "id": "/"
apps_dir = os.path.join(profile, "Default", "Web Applications", "Manifest Resources")


def fail(message):
    print(f"install-app: {message}", file=sys.stderr)
    sys.exit(1)


def browser_running():
    # The profile lock is a link to <host>-<pid>; a browser that was killed
    # leaves it behind.
    try:
        pid = int(os.readlink(os.path.join(profile, "SingletonLock")).rsplit("-", 1)[1])
        os.kill(pid, 0)
        return True
    except (OSError, ValueError, IndexError):
        return False


if browser_running():
    fail("the app's browser is running; close its window first")

# Chromium draws its own title bar for the app (its custom frame), the one
# a window-controls overlay can hand to the page. Set before it starts: a
# running browser writes its preferences back.
prefs_path = os.path.join(profile, "Default", "Preferences")
os.makedirs(os.path.dirname(prefs_path), exist_ok=True)
prefs = {}
if os.path.exists(prefs_path):
    with open(prefs_path, encoding="utf-8") as f:
        prefs = json.load(f)
prefs.setdefault("browser", {})["custom_chrome_frame"] = True
with open(prefs_path, "w", encoding="utf-8") as f:
    json.dump(prefs, f)

before = set(os.listdir(apps_dir)) if os.path.isdir(apps_dir) else set()

to_chrome_r, to_chrome_w = os.pipe()
from_chrome_r, from_chrome_w = os.pipe()


def wire_pipe():
    os.dup2(to_chrome_r, 3)
    os.dup2(from_chrome_w, 4)
    # dup2 onto the same number keeps close-on-exec: clear it on both.
    os.set_inheritable(3, True)
    os.set_inheritable(4, True)


browser = subprocess.Popen(
    [chromium, f"--user-data-dir={profile}", "--remote-debugging-pipe",
     "--no-first-run", "--no-default-browser-check", "about:blank"],
    preexec_fn=wire_pipe, close_fds=False,
    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
os.close(to_chrome_r)
os.close(from_chrome_w)
to_chrome = os.fdopen(to_chrome_w, "wb", buffering=0)
from_chrome = os.fdopen(from_chrome_r, "rb", buffering=0)

replies = {}
arrived = threading.Condition()


def read_replies():
    pending = b""
    while chunk := from_chrome.read(65536):
        pending += chunk
        while b"\0" in pending:
            message, pending = pending.split(b"\0", 1)
            reply = json.loads(message)
            if "id" in reply:
                with arrived:
                    replies[reply["id"]] = reply
                    arrived.notify_all()


threading.Thread(target=read_replies, daemon=True).start()
next_id = 0


def call(method, params=None, session=None, timeout=60):
    global next_id
    next_id += 1
    message = {"id": next_id, "method": method, "params": params or {}}
    if session:
        message["sessionId"] = session
    to_chrome.write(json.dumps(message).encode() + b"\0")
    deadline = time.time() + timeout
    with arrived:
        while next_id not in replies:
            if not arrived.wait(max(0, deadline - time.time())):
                fail(f"{method}: no answer from the browser")
        reply = replies.pop(next_id)
    if "error" in reply:
        fail(f"{method}: {reply['error'].get('message')}")
    return reply.get("result", {})


def manifest_linked(session):
    found = call("Runtime.evaluate", {
        "expression": "document.readyState === 'complete' && !!document.querySelector('link[rel=manifest]')",
        "returnByValue": True}, session=session)
    return found.get("result", {}).get("value") is True


try:
    # Installed from the open page: an install from a URL can't follow the
    # redirect a signed-out "/" answers with (to /login).
    page = next(t for t in call("Target.getTargets")["targetInfos"] if t["type"] == "page")
    session = call("Target.attachToTarget", {"targetId": page["targetId"], "flatten": True})["sessionId"]
    call("Page.navigate", {"url": url}, session=session)
    deadline = time.time() + 30
    while not manifest_linked(session):
        if time.time() > deadline:
            fail(f"{url} shows no page with a manifest")
        time.sleep(0.5)
    call("PWA.install", {"manifestId": manifest_id}, session=session, timeout=120)
    # Installed from here it would open as a tab; the app has its window.
    call("PWA.changeAppUserSettings", {"manifestId": manifest_id, "displayMode": "standalone"})
    call("Browser.close")
finally:
    try:
        browser.wait(timeout=30)
    except subprocess.TimeoutExpired:
        browser.kill()

new = sorted(set(os.listdir(apps_dir)) - before) if os.path.isdir(apps_dir) else []
if len(new) == 1:
    print(new[0])
elif not new and len(before) == 1:
    print(next(iter(before)))  # it was installed already
else:
    fail(f"can't tell the app's id ({len(new)} new apps)")
