# 歌唱输出协议与旧提示清理

2026-09-15。本记录只覆盖提示、公开输出协议和对应回归；目标审核、权限审核及真实歌声播放的集成结果由本批总报告另列。

`behavior.txt` 的两块旧歌唱规则已从运行资源中移走，原文保留在 [历史归档](archive/singing-legacy-preclip-20260915.md)。原文件的 92–127、383–596 行原本就被 `LLM.BuildSystemPrompt → StripEmbeddedSkillBlocks` 排除，不能把它们全部算作模型本次已读到的冲突。运行时详细歌唱规则由 `SetActiveSkills` 从唯一的 `Prompts/Skills/singing.txt` 加载。

已有实际发送请求也支持这一点：`Server/ARDY/runtime/response-reliability-20260915/ordinary-final/response-latency/case-1-formal-actual-02.request.json` 包含“已加载技能 singing”和独立技能的“当前事项与独立核对”，没有 SKILL-BEGIN 或旧独立段规则；却仍包含块外“silent 之前的文字会被念出来”这句旧示例。该请求来自此前真实验证，不是最新 Play 的完整请求副本。SHA256 为 `6cea5dd99e1f449ee7923856b16f82c81942111ea490a889d3f1c6c542cc7e5d`，机器可读审计见 `Server/ARDY/runtime/singing-reliability-20260915/prompt-contract/prior-wire-audit.json`。

清理保留了独立技能尚缺的有用原则：歌词、旋律和上下文合看，检索候选不等于身份确认，外部目录未检索到不等于本地没有，未命名素材不编造名称，保存只认真实结果，以及异步轮唱不冒称实时同步合唱。旧 `order/stable`、新请求使用 `hum_back echo/practice`、强制确认来源后才可播放、旧长度限制及裸正文示例不再迁回。基础提示仍保留能力概览、用户自主权及技能申请入口。

新的公开协议统一为：内部判断用闭合的 `thought`，公开台词用前置语言/阶段和 `say`，真实工具在这些块外。`[内心]` 是历史显示，不是当前输出标记；`silent` 是本条的静音尾部，不能靠后续 `say` 恢复声音。原有合法的 `after_action → say → silent → 顶层动作` 仍兼容。修复没有把私有文本或无效输出中的工具提取出来执行。

程序反馈现在会针对 `silent → lang/speech → say` 说明为什么声明未生效，以及应改为闭合 `thought` 后再发声，避免只重复“缺少前置声明”。`singing_policy` 也被列为依赖审核的动作：`independent` 冲突时与演唱请求一起从执行投影撤出；`singing_stop` 仍可直接停止当前歌声。是否真正变更权限仍由独立审核决定。

`SingingOutputContractRegression.RunRecordedOrThrow` 重放冻结日志的九条原始回复及手写规范示例，按每个字符串分割点验证公开与私有边界。五条是公开格式错误、两条是发声阶段冲突；另两条语法本来合法，不能冒称其目标审核问题已被解析器修复。该回归还检查私有唱歌/权限标签不执行、静默动作兼容和政策冲突下仍保留立即停止。

当前 .NET 7 直接编译生产解析器的结果：新回归 11,457 项、既有公开边界回归 18,830 项通过。结果为 `Server/ARDY/runtime/singing-reliability-20260915/prompt-contract/pure-results.json`，没有请求模型或播放音频。

Unity 集成入口 `SingingOutputContractRegression.RunPromptAuditOrThrow(beforeBehaviorPath, outputDirectory)` 会使用真实 `BuildSystemPrompt`、`SetActiveSkills`、`ChatQW.BuildRequestJson` 导出清理前/后、技能开/关、流式/非流式共八份请求，并检查旧块不进入任何请求。此入口只构造请求，不发送 HTTP；应与真实模型验证分别报告。
