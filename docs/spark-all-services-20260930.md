# 全 Spark 服务快照与声音学习迁移（2026-09-30）

发布分支：`spark-all-services-20260930`。Unity 客户端留在电脑；Qwen 位于
`192.168.114.175:8888`，ARDY、动作特征、TTS、ASR、RVC、声音学习位于
`192.168.114.163`。本次补齐了最后仍在 Windows 上运行的声音学习服务。

## 声音学习

- 目录：`/home/spark_root_001/neeeva-voice/learning`。
- 容器：`neeeva-spark-learning`，镜像 `neeeva-spark-learning:local-v1`。
- 已测试镜像 ID：`sha256:c4392f9dfee7b7a9942a728665dae343001e5ac2393de5173418df1838eefb2d`。
- 基础镜像：已有 ARM64 Torch/CUDA 镜像 `ghcr.io/anemll/dspark-vllm-gx10:0.1.1`，
  本机镜像 ID 前缀 `3430d6614a8e`；未更改其他容器或宿主机 Python 环境。
- 独立 venv；默认 CPU、2 线程，服务端口 9884，单进程持有数据目录锁。
- 代码和模型发布目录只读挂载，`data/` 单独可写挂载。
- Spark 外网 DNS 不可用，安装时通过仅监听回环的 SSH 反向代理获取依赖；
  安装完成后隧道已关闭，运行服务不依赖此代理。
- 基础镜像中未使用的 vLLM、FlashInfer、OpenCV 包仍有依赖版本警告。
  验证范围是本服务的实际导入、PANNs 推断与 HTTP 请求，不是基础镜像所有包的兼容性。

## 数据保全与验收

迁移前确认全部队列任务已结束，再停止已识别的 Windows 学习服务进程。
通过 SSH 复制用户档案和任务记录，保留 Windows 原目录作为恢复副本。
原音、标注、候选模型与任务记录共 **468 个文件**，迁移后的逐文件 SHA-256
与源文件全部相同。归档、哈希清单和个人数据只留在本机／Spark，不进入 Git。

真实 HTTP 验证：原用户档案仍有 **35 段录音，数据版本 79**；模型烟测完成
2 秒合成静音的 PANNs 推断；一次已保存录音的候选生成任务成功完成，并继续返回
`requires_confirmation=true`、`execution_authorized=false`。
生成新的候选属于派生记录更新，不修改人工标注或原音。

本机 9884 监听已结束。新的 Unity 默认地址及根目录中文学习启动入口均指向
Spark。已经运行的 Play 实例可能保留旧地址，需退出后重新进入，或在声音学习
面板输入 `http://192.168.114.163:9884` 再连接。旧地址的待上传队列没有被自动改写。

## 发布验证

- `Tools/start_spark_all.ps1 -CheckOnly` 与 `-Action status` 均完成。
- LLM 模型列表、ARDY 健康，以及 9880／9881／9882／9884 HTTP 健康检查通过。
- 新 PowerShell 文件语法解析通过；新 Python 部署文件编译通过。
- 必要资源均小于 50 MiB；发布清单排除了私人数据、私有房间、日志、实验输出和下载缓存。
- 公开 `chatSample.unity` 已配置 Spark ASR、TTS、VC、Linux 参考路径和兼容 LLM 后端。

本次没有重新进行完整 Unity Play 对话、物理听音或动作泛化验收。
声音学习算法的固定评估结论仍为未通过采用检查；迁移没有提升它的算法权限，
也没有部署前一轮讨论的 Jev／多模态新方案。
