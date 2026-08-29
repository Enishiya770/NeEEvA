# SailingMoon private room integration

The SailingMoon source assets and generated scenes are intentionally stored
under `Assets/Private/SailingMoon/`, which is ignored by Git. Do not commit,
push, attach, or redistribute that directory or its `.meta` file.

The local integration keeps the original Day scene's baked lighting, removes
VRChat/Udon/video/mirror/visitor-board components, and merges a copy of
`Assets/AIChatTookit/Scene/chatSample.unity` at the room's original spawn point.
The public chat scene is never modified.

After a local rebuild, open:

`Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity`

The reusable menu command is:

`NeEEvA > Private Rooms > Build SailingMoon Day Chat Scene`
