# GPT-SoVITS api_v2 适配补丁

`GPT-SoVITS/` 整个目录被 `.gitignore` 排除（体积约 12GB），所以对它的改动**不会随仓库走**。
换机器或重装 GPT-SoVITS 后必须重新打这个补丁，否则**实时对话的流式 TTS 会全部静音**。

## 问题

Unity 端 `GPTSoVITSFASTAPI` 把 `streaming_mode` 当作**整数模式**发送：

```csharp
[Header("GPT-SoVITS streaming mode (3 uses fixed, faster chunks)")]
[SerializeField, Range(2, 3)] private int m_StreamingMode = 3;
```

而上游 api_v2 把该字段声明为布尔：

```python
streaming_mode: bool = False
```

pydantic v2（本机 2.8.2）对布尔字段只接受 `true/false/0/1`，收到 `3` 直接判定校验失败，
返回 **422 Unprocessable Entity**。此时响应体是 FastAPI 的错误 JSON，而 Unity 端会把它
当成 WAV 头解析，于是日志里出现这种极具误导性的报错：

```
[TTS流式] 失败(code=422): HTTP/1.1 422 Unprocessable Entity;
unsupported WAV: riff=False, wave=False, format=28514, bits=27682, ...
```

那些乱码数值不是音频损坏，而是 JSON 文本被按二进制头解读的结果。

典型症状：**非流式路径（预热等，代码里硬编码 `streaming_mode = 0`）返回 200 且有声音，
流式路径全部 422 且静音**——服务端日志中两者会同时出现。

## 补丁内容

`api_v2.streaming_mode.patch`，改动三处：

1. `TTS_Request.streaming_mode` 放宽为 `Union[bool, int]`，并显式声明 Unity 会一并发送的
   `min_chunk_length`（上游 TTS 管线没有对应的切分钩子，接受但不生效，仅为避免校验失败）
2. `tts_handle()` 里新增 `_neeeva_stream_level()` 归一化，并在流式生成器中实现模式语义
3. GET 端点签名同步放宽，与 POST 行为一致

模式约定：

| 值 | 行为 |
|---|---|
| `0` / `false` | 非流式，一次性返回整段音频（上游原行为） |
| `1` / `true` | 流式，按 TTS 语义片段逐块下发（上游原行为，向后兼容） |
| `2` | 同 1，对应 Unity 的「语义块」模式 |
| `3` | 流式 + 把 PCM 再切成固定小块（默认 40ms）下发 |

响应格式在所有流式模式下都保持不变，即 Unity `PcmStreamingDownloadHandler` 期望的契约：
**前 44 字节标准 WAV 头（PCM / 16-bit），其后持续追加裸 16-bit PCM。**

## 如何应用

在 GPT-SoVITS 根目录（含 `api_v2.py` 的那一层）执行：

```bash
git apply -p1 <项目路径>/Server/GPT-SoVITS-patch/api_v2.streaming_mode.patch
```

补丁基于 GPT-SoVITS v2pro-20250604。若上游版本不同导致无法自动应用，按上面「补丁内容」
三处改动手工修改即可，逻辑很短。

若本机 git 开了 `core.autocrlf=true`，`git apply` 会把结果写成 CRLF 行尾——对 Python
没有任何影响，无需处理。

## 验证

打完补丁重启服务后，四种模式都应返回合法 WAV 头 + PCM：

| 模式 | 结果 | 首包 | 总耗时 |
|---|---|---|---|
| 3（Unity 当前配置） | PASS | ~0.2s | ~0.8s |
| 2 | PASS | ~0.2s | ~0.8s |
| `true`（旧式） | PASS | ~0.2s | ~0.7s |
| `false`（非流式） | PASS | ~0.7s | — |

实测说明：模式 3 的固定分块在本机**没有测出相对模式 2 的首包优势**（TTS 片段本身
就来得很快），但也没有变慢；保留该模式主要是为了兼容 Unity 端的既有配置。
首次请求会有 2s 左右的冷启动，属正常现象。

## 2026-10-13：混合文字前端与断流诊断

`apply_frontend_diagnostics.py` 在上述整数流模式补丁基础上修正另一个故障：
中文句子包含假名名字（例如 `ユウ`）时，旧前端会强制将该片段作为中文处理，
产生空音素/BERT 列表，合成生成器在 HTTP 200 发出后异常退出。
Unity 只能看到 `200 / Unknown Error`，并可能已播出前半句。

补丁保留含明确假名、韩文字符的检测语言；纯汉字仍服从声明的句子语言。
非空文本若仍没有音素，返回明确的前端错误，不制造静音、不吞掉失败。
API 优先加载当前安装目录，避免附带 Python 的 `users.pth` 将导入重定向到旧安装。
响应生命周期现在显式关闭被取消请求的音频生成器：客户端断开后，等待正在运行的原生生成步骤返回，再关闭本请求的生成器，不依赖垃圾回收，也不取消其他请求。`[TTS/ServerCancellation]` 分别记录断开与生成器关闭时间；`[TTS/ServerPreparedReceipt]` 为非流式预取记录独立合成结果。服务端产出字节数不能作为客户端已播放字节数。
Unity 发送 `X-Request-ID`；服务端 `[TTS/ServerStreamReceipt]` 记录对应请求的
完成、异常或取消及产出字节数。HTTP 成功、完整合成、AudioSource 播放完成分别记账。

从项目根目录应用（参数均需显式指定）：

```powershell
python Server/GPT-SoVITS-patch/apply_frontend_diagnostics.py --root GPT-SoVITS --backup Logs/tts-patch-backup
python Server/GPT-SoVITS-patch/test_frontend_diagnostics.py
& Tools/neeeva_services.ps1 restart -Only tts
```

脚本先验证全部源码锚点，再保存备份、修改并记录 SHA256；可重复执行。
不兼容的源码会拒绝应用。备份目录中的两个原文件可用于回退，回退后需重启 TTS。
本次原句非流式与流式、其他假名名字、日语汉字和英文名字共 5 个实际合成用例
均完整返回非空 PCM；录音存于 `Logs/conversation-repair-20261013/tts-after`。
这些检查验证合成与传输完成，未把它等同于人工听感或完整 Unity 场景验收。
