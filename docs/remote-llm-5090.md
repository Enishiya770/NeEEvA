# RTX 5090 remote LLM deployment

NeEEvA runs qwen3.6 and bge-m3 on the RTX 5090. Unity and the voice stack stay
on the RTX 4090. The Unity endpoints remain unchanged because SSH forwards the
remote loopback ports to the same local ports.

## Network

| Machine | Direct-link address | Role |
| --- | --- | --- |
| RTX 4090 | `192.168.50.1/24` | Unity, VR, voice services, SSH client |
| RTX 5090 | `192.168.50.2/24` | llama.cpp inference |

The cable is connected directly between the Intel I226-V and Marvell AQtion
adapters. No gateway or DNS is configured on this link. The 4090 continues to
use Wi-Fi for Internet access. SSH host alias `neeeva-5090` resolves to
`192.168.50.2`.

## Endpoints

| Local endpoint used by Unity | SSH destination on 5090 | Service |
| --- | --- | --- |
| `127.0.0.1:8080` | `127.0.0.1:8080` | qwen3.6 + mmproj |
| `127.0.0.1:8090` | `127.0.0.1:8090` | bge-m3 embeddings |

The model servers are not exposed directly to the apartment network.

## Normal operation

- `start_all.cmd`: remote LLM/embedding plus all local voice services.
- `start_chat.cmd`: remote LLM/embedding plus local TTS/ASR.
- `start_llm.cmd`: remote LLM/embedding and SSH tunnel only.
- `status.cmd`: local voice state, tunnel health, remote state, and both GPUs.
- `stop_all.cmd`: stop local voice services, tunnel, and remote model servers.

PowerShell control:

```powershell
.\Tools\neeeva_remote_llm.ps1 start
.\Tools\neeeva_remote_llm.ps1 status
.\Tools\neeeva_remote_llm.ps1 restart
.\Tools\neeeva_remote_llm.ps1 stop
```

## Local rollback

The original 4090 models and llama.cpp runtime are intentionally retained.

- `start_local_all.cmd`: stop the remote stack, then run all services locally.
- `start_local_llm.cmd`: stop the remote stack, then run qwen3.6 locally.

Returning to remote mode only requires `start_llm.cmd` or `start_all.cmd`.

## File locations

RTX 5090:

```text
D:\NeEEvA\llamacpp-b8919-cuda131-sm120\  production runtime; GGUF files are hard-linked
D:\NeEEvA\llamacpp\                    retained CUDA 12.4 runtime/model rollback source
D:\NeEEvA\services\                    remote service controller
D:\NeEEvA\logs\                        llama-server stdout/stderr logs
```

RTX 4090 runtime logs, including the SSH tunnel, remain under
`Server\RuntimeLogs\`.

## Verified baseline

- Direct-link negotiation: 2.5 Gbps.
- Measured SCP payload throughput: about 125.7 MiB/s.
- Production llama.cpp: `b8919-dc80c5252`, official CUDA 13.1 Windows build.
- Runtime reports CUDA architectures `750,800,860,890,1200,1210` and
  `BLACKWELL_NATIVE_FP4=1`; the RTX 5090 therefore uses native SM120 kernels.
- qwen3.6 model, mmproj, and bge-m3 load on RTX 5090 CUDA.
- Chat completions, 1024-dimensional embeddings, and image input all pass.
- Streaming cache reuse passed 8 consecutive 15,001-token prompts and 6
  consecutive 21,853-token prompts. Three concurrent long-text/image pairs
  also passed without `MUL_MAT_ID`, CUDA, or empty-response errors.
- Typical loaded 5090 VRAM: about 21.8 GiB used, 10 GiB free.
- Typical idle 4090 VRAM during validation: about 2.5 GiB used, 21 GiB free.

The local controller deliberately cold-starts the two CUDA services in order:
LLM, wait for health, then embeddings. The remote WMI launch command returns
before the local health wait so the server is not tied to the SSH control
session.

## Runtime provenance and rollback

The production files came from the official llama.cpp `b8919` release:

| Asset | SHA-256 |
| --- | --- |
| `llama-b8919-bin-win-cuda-13.1-x64.zip` | `2ceb1a661e31750cb921e8beef04a3efe26823ea552932f3faacf7988c29d687` |
| `cudart-llama-bin-win-cuda-13.1-x64.zip` | `f96935e7e385e3b2d0189239077c10fe8fd7e95690fea4afec455b1b6c7e3f18` |

Do not switch production back to `D:\NeEEvA\llamacpp`: its CUDA 12.4 build
does not contain SM120 kernels and reproduced `MUL_MAT_ID failed` followed by
`CUDA error: invalid argument` during long-prefix cache reuse. It remains only
as a reversible rollback copy. The separate `llamacpp-b10516-sm120` directory
is also non-production because that version changed the multimodal/API and
automatic VRAM-fitting behavior used by this project.

## Troubleshooting

1. Run `status.cmd`.
2. Confirm the cable link and `ping 192.168.50.2`.
3. Confirm `ssh neeeva-5090 hostname` returns `NEVA`.
4. Read `D:\NeEEvA\logs\llm.err.log` and `embed.err.log` on the 5090.
5. Read `Server\RuntimeLogs\remote_llm_tunnel.err.log` on the 4090.
6. Use `start_local_llm.cmd` when a local fallback is needed immediately.
