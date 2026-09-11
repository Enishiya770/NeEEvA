# 本次动作试用：保存轨迹审计

8份原始motion trace已按startedUtc确认并逐字节归档（2026-09-10 20:17–20:22 JST）。只读取已保存动作数据；未启动模型、服务或Unity，未重新生成。

24个返回窗口全部HTTP200；每次120帧/20fps/6秒，客户端均记completed。首窗0.539–0.616秒，全三窗0.969–1.166秒。成功返回和播放完成不等于语义达成。

| Turn | Seed | 首窗/全窗秒 | 帧数 | 完成 | 描述 |
|---|---:|---:|---:|---|---|
| response-5 | 0 | 0.560/1.166 | 120 | completed | Antonia bows deeply and gracefully, holding the bow for a moment before rising back up. |
| response-7 | 0 | 0.539/0.969 | 120 | completed | Antonia bows deeply and gracefully, holding the bow for a moment before rising back up. |
| response-17 | 0 | 0.580/1.062 | 120 | completed | Antonia gently brings her hands together in front of her lower abdomen, pauses for a moment, and then performs a slow, deep, and graceful bow, holding the position briefly before rising back to a neutral standing posture. |
| response-19 | 0 | 0.595/1.125 | 120 | completed | Antonia playfully wags her index finger side to side in a 'no-no' gesture, then raises both hands to make bunny ears above her head, smiling. She holds the pose briefly before lowering her arms back to a neutral standing posture. |
| response-21 | 0 | 0.580/1.079 | 120 | completed | Antonia gently brings her hands together in front of her lower abdomen, pauses for a moment, and then performs a slow, deep, and graceful bow, holding the position briefly before rising back to a neutral standing posture. |
| response-23 | 0 | 0.596/1.054 | 120 | completed | Antonia raises both arms, bends elbows, and holds index and middle fingers up in bunny ears above head, smiling. She holds this pose briefly, then lowers arms back to neutral standing posture. |
| response-25 | 0 | 0.569/1.070 | 120 | completed | Antonia raises both arms, bends elbows, and holds index and middle fingers up in bunny ears above head, smiling. She holds this pose briefly, then lowers arms back to neutral standing posture. |
| response-27 | 0 | 0.616/1.144 | 120 | completed | Antonia playfully raises both hands to the sides of her head, forming bunny ears with her index and middle fingers, and tilts her head slightly with a gentle smile. |

返回Core27骨架只有Hand、HandEnd、HandThumb1，没有独立食指/中指链；当前UpperBody播放器只映射13个躯干、头、手臂和整手骨骼，不驱动生成的手指关节。因此独立摇食指和食指＋中指兔耳手型缺少执行通道。

兔耳相关4条使用原始返回旋转与固定源骨长做CPU正向运动学。以下是每段中两腕同时最高时，较低那只手腕相对Head关节的高度；负值代表仍在下方，不能解释为用户实际VRM录像：

| Turn | 最佳同时腕高相对Head | 最近同时两腕中较远一腕距离Head |
|---|---:|---:|
| response-19 | -0.674 m | 0.728 m |
| response-23 | -0.677 m | 0.730 m |
| response-25 | -0.678 m | 0.730 m |
| response-27 | -0.377 m | 0.494 m |

这几段返回轨迹连双腕到头两侧的上身目标也未形成；不仅是细手指手型问题。Head是源关节位置，不是头皮表面，报告不做场景骨架、混合或衣物碰撞推断。

| 相同描述配对 | 16帧输入history平均/最大差 | 返回120帧平均/最大差 |
|---|---:|---:|
| response-5 / response-7 | 1.904° / 4.979° | 1.575° / 6.460° |
| response-17 / response-21 | 0.870° / 4.527° | 1.983° / 12.218° |
| response-23 / response-25 | 0.169° / 0.779° | 1.138° / 5.671° |

三组重复描述都使用seed0、相同模型/契约/adapter，但真实initialHistory不同，返回轨迹也并非相同。除会话标识与history外请求字段相同；因此不是相同完整输入的重放。没有保存精确特征向量，不能把全部差异单因果归为history。

逐窗HTTP、身份回显、种子、provenance、帧SHA、输入history差异及每帧CPU位置见`evidence.json`和其中归档链接。原始文件未改，`archive-manifest.json`保留8份原字节SHA。
