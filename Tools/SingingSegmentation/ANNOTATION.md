# 离线人工标注工具

本批修复版可直接双击打开：`work/singing-subsystem-20260929/segmentation/annotation/annotate-v2.html`。这是单个自包含 HTML，约 4 MB，内嵌七份未经修改的原始 WAV 和真实 PCM min/max 波形；不需要服务、联网或额外安装。浏览器只在用户点击播放器时播放，没有自动播放。页面不加载模型候选、旧裁剪位置或对话提示，避免预先诱导人工判断。

2026-09-29 补丁版为同目录 **`annotate-v2.html`**。旧 `annotate.html` 保留不变，不能覆盖或刷新用户正在标注的页面。新文件保留原 dataset ID、manifest SHA 和导入格式，可导入已有 JSON。首次提交的 c9 秒数差异没有发现乘除 100 的换算 bug，用户对 3–14 秒的更正由独立修正记录管理，工具不偷偷更改导入标签。

只读审查另外发现并用纯合成音频复现了一个保存缺陷：旧版在合法选区 3–14 秒之后输入无效的 15–14 秒会报错，但保存仍读取旧 `selection`，有可能保存旧范围。不能据此推断用户当时采用了哪种操作或认定它造成了 c9 差异。v2 在任何数字输入变化时立即撤销旧可保存/播放选区；保存前重新验证当前单位的两端字段，拒绝空白、反向、负值和超出原录音的秒数；只有合法完整字段才转换为整数样本。中间无效的双字段编辑保留原文本及 `editIndex`，后续填完第二端可正常提交、更新同一行。校验成功会显示秒数与样本数，避免旧错误提示滞留。

补丁验证脚本 `test_annotation_invalid_draft.js` 使用独立合成静音 WAV 和独立 dataset ID，在无声 headless 页面复现旧缺陷、验证修复及编辑流程；不会给用户原录音写人工标签。修改前源文件已保留于 `work/singing-subsystem-20260929/human-evaluation-20260929/tool-before/`。

操作顺序：

1. 选择录音，手动播放。拖动波形选择范围；可放大查看或用原采样率的整数样本坐标精调。
2. 选择 `speech`、`singing` 或 `uncertain`。两端分别明确填写允许误差（毫秒），可附备注。空白误差带不能保存；0 表示标注者确认精确到样本，不会自动预设。
3. 保存标注。名义区间不可重叠；尚未覆盖的范围会列出。标注不确定/其他声音时可显式使用 `uncertain`，未标注空白不会自动变成任何标签。
4. 全部原样本覆盖后，手动确认本段已复核。仅覆盖完整不会自动标记复核完成。
5. 导出 JSON。可以导出未完成进度并稍后导入；页面没有云端保存，关闭前应保存文件。

“播放选区”在内存中从原 PCM 精确复制 `[start_sample,end_sample)` 构造临时 WAV，因此播放结束不依赖可能延迟的 `timeupdate` 暂停事件。源文件不修改，不写出裁剪音频。浏览器底部播放器此时显示这段临时预览的相对时间；页面旁边的时间会映射回原音频。

导出 `annotation_schema=neeeva.speech-singing-human.v1`，包含整批清单 SHA、每份原 WAV 的 `audio_sha256 / sample_rate / sample_count`、原点 0、整数半开样本范围、边界误差带、未覆盖区间、人工复核状态及可选标注者代号。边界误差是人工容许范围，**不是统计置信区间**。`complete=true` 要求七份录音均完整覆盖且逐份手动确认。空页面导出只有未标注记录，不生成伪人工标签。导入拒绝数据集/源 hash/采样坐标错配、重叠、越界、声称复核却未覆盖以及标明曾展示模型候选的文件。

生成和验证：

```powershell
python Tools/SingingSegmentation/build_annotation_tool.py --manifest work/singing-subsystem-20260929/segmentation/evaluation-manifest.json --output work/singing-subsystem-20260929/segmentation/annotation/annotate.html
node Tools/SingingSegmentation/test_annotation.js work/singing-subsystem-20260929/segmentation/annotation/annotate.html
```

模板、应用逻辑和纯坐标/schema 核心分别在 `annotation_template.html`、`annotation_app.js`、`annotation_core.js`。生成器验证全部 WAV 哈希，16-bit PCM 解码和波形使用同一字节快照，生成独立 artifact manifest。没有使用自动分段作为默认标签。

本批 11 项静态/纯逻辑验证包括 JS 语法、七个内嵌 WAV 哈希/原坐标、真实 PCM 波形抽查、样本精确预览、空白/完整复核语义、误差带、重叠及异源导入拒绝。测试采用合成标注并只在内存运行；没有为用户录音创建人工标签。

另使用已安装的 Edge 154.0.4258.37 和本机 Playwright，在全新隔离的 headless 会话完成 **5 组真实页面验证**：七份录音和波形初始渲染；无选区/无误差带时拒绝保存；样本数与秒数往返、放大与鼠标拖选；真实浏览器下载七份未完成且零标签的 JSON；重新导入后切换录音的源信息一致。控制台页面错误 0、外部网络请求 0、`play()` 调用 0。测试强制静音，并拦截 `play()` 作为额外保护，绝不点击播放或保存成功标签。

结果和完整截图位于 `work/singing-subsystem-20260929/segmentation/annotation/browser-acceptance/`：`browser-test-results.json`、`ui-initial.png`、`ui-after-empty-roundtrip.png`。`ui-export-unreviewed.json` 是界面验收下载的**空白进度文件**，不是已完成的人类标注数据。没有下载新浏览器，没有操控既有浏览器窗口。真实发声播放和人工试听仍未验收。

无声 UI 验证脚本为 `test_annotation_browser.js`，参数依次为生成 HTML、输出目录、现有 Playwright 包路径、现有浏览器可执行文件路径。它不安装或下载组件。
