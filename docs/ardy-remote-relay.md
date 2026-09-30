# Native ARDY relay

Unity keeps using `http://127.0.0.1:8093`. The relay on that port must point to
an authenticated **native ARDY** HTTP service. That service must implement
`GET /health`, `POST /v1/motion/generate`, `POST /v1/motion/cancel`, and the
room-locomotion routes. A chat endpoint or a Gradio endpoint is not a relay
target.

Configure the origin once for the current PowerShell session, or pass it on the
start command. Run these lines from the Unity project root:

```powershell
$env:NEEEVA_ARDY_UPSTREAM = 'http://<native-ardy-host>:<port>'
# 只有 remote-token.dpapi 尚不存在时才执行 configure；它会交互式读取 token。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\neeeva_remote_ardy.ps1 configure
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\neeeva_remote_ardy.ps1 start -Upstream $env:NEEEVA_ARDY_UPSTREAM
```

The token is stored as a DPAPI-protected file for the current Windows user. It
is never put in the process command line or relay log. Later checks are:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\neeeva_remote_ardy.ps1 status
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\neeeva_remote_ardy.ps1 stop
```

Cold startup accepts `features.verified=false`. The native service sets that
flag after the first successful motion-feature request; the relay does not
create a fake motion request just to pass a health check. A successful real
Unity motion request should make the next status check report verified
features.

The two currently observed Spark services are not native ARDY origins:

- `192.168.114.175:8888` is the OpenAI-compatible Qwen chat API.
- `192.168.114.163:9550` is the old Gradio service, and `192.168.114.163:8890`
  is a separate feature endpoint that advertises `shared_model=false`.

Neither exposes the native ARDY motion protocol required by Unity. Do not set
`NEEEVA_ARDY_UPSTREAM` to either address until a native ARDY service is
actually deployed there. The launcher intentionally has no stale default
origin; starting without `-Upstream` or the environment variable fails with a
configuration message.
