# Remote mock

A stand-in for `bellboard-remote.exe` so the phone app (`src/Bellboard.Remote/wwwroot/`) can be worked on
without Windows. It implements the REST and WebSocket contract in
[`src/Bellboard.Remote/PROTOCOL.md`](../../src/Bellboard.Remote/PROTOCOL.md) with fake data: two agents waiting
(one with a structured permission prompt, one with a numbered menu on screen), one working, a feed, and a plain
shell workspace with a browser pane. Terminals stream fake output with sequence numbers.

```sh
node tests/remote-mock/mock.mjs                    # http://127.0.0.1:8787, full access
node tests/remote-mock/mock.mjs --level respond    # or view
node tests/remote-mock/mock.mjs --host 0.0.0.0     # reach it from a phone on the same network
```

Pair with the code `ABCD-EFGH`, or open `http://127.0.0.1:8787/#pair=ABCD-EFGH`. Node 18+ and no npm
packages. State is in memory: restart the mock to reset it. Answering a prompt brings a new one back after
20 seconds.

Push notifications aren't sent: the mock accepts subscriptions and logs them. Over plain HTTP from another
device, browsers don't allow service workers or push; use `localhost`, or put the mock behind HTTPS.
