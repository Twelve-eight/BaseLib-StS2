# BaseLib-StS2 DEVLOG

## 2026-10-26 review-fixes-20261021 分支审查(v3.4.8)与实机烟测清单

### 1. 范围与方法

- 对象:`Sts/BaseLib-StS2`,分支 `review-fixes-20261021`(HEAD `6c2f7da`,版本 3.4.8),基线 `master`(`c070755` = v3.4.7,upstream/master)。
- `git diff master..HEAD`:33 文件,+1427/−260,12 个提交。
- 依据:游戏反编译 `G:/omp works/.tmp/sts2-game-decompile/`(v0.107.1 线)、Godot 文档 `Sts/.nuget/godotsharp/4.5.1/lib/net8.0/GodotSharp.xml`、真实日志 `G:/appdata/C-Users-o_Obl/Roaming/SlayTheSpire2/logs/godot.log`(3.4.7 基线)。
- 本轮只做静态审查与本仓改动:**未构建、未启动游戏**,任何"实机通过"必须由主会话按 §6 执行后才能写。

### 2. 分支与提交结构

- 11 个 `fix/*` 分支**没有一个**是 HEAD 的 git 祖先(`git branch --merged` 只有 `master` 与本分支)。每个分支都是从 `master` 起的单提交,内容被手工重新落进 12 个提交里 → 属于"内容已合入、git 未合并"。
- 其中 9 个分支与 HEAD 在各自文件上逐字节相同:`cardmodifier-deprecated`、`cardmodifier-snapshot`、`config-empty-prefix`、`defaultloc-backfill`、`extracombatui-snapshot`、`hook-listener-isolation`、`instructionmatcher-wildcard`、`linked-reward-set-id`、`trygettype-fallback`。
- `fix/optionpools-empty`(`d749861`)只比 HEAD 多一段 1-池构造函数的 `remarks`(不放回抽样语义),代码一致,注释在合并时丢了。
- `fix/spirefield-missing-entry` 与 HEAD 实现不同:HEAD 多了 mod id 命名空间(分支没有),缺项判定用 `found.Value.name == null`,分支用 `.Where(...).Cast<SavedProperty<T>?>()`;两者等价,HEAD 额外拒绝 `value == null`。无回归。
- 结论:分支可以删/不必合并;若日后要保留历史,只能作 reference,不要 `git merge`(会与 HEAD 的手工版本冲突)。

### 3. 六个修复组逐条结论

#### 3.1 附魔存档上下文(PASS)

- 改动:`Patches/Saves/ExtendedSaveHandlers.cs:280` 新增 `AddContext<EnchantmentModel, SerializableEnchantment>(harmony)`。
- 依据:`MegaCritSerializerContext.SerializableEnchantmentPropInit(JsonSerializerOptions)` 存在(`MegaCritSerializerContext.cs:7882`),`AddContext` 用 `AccessTools.DeclaredMethod` 能拿到私有方法;`SerializableEnchantment` 从跑团存档可达(`SerializableCard.cs:24`);To/FromSerializable 与网络读写钩子原本就有(`ExtendedSaveHandlers.cs:533-572`、`ExtendedSaveTypes.cs:86-90`);`EnchantmentModel.ToSerializable` 本来就写 `SavedProperties`(`EnchantmentModel.cs:370-384`)。之前缺的只是 JSON 属性列,所以注册到附魔上的 `RegisterSave`/自定义存值会被静默丢弃。
- 真实日志佐证:3.4.7 的实机日志只有 `Adding custom save data to SerializableCard/Relic/Run/Player/Potion/Reward`,**没有** `SerializableEnchantment`(`godot.log:2015-2054`)→ 修复对应真实缺口。
- 风险:存档 JSON 多出 `save_dict_*` 列;`MegaCritSerializerContext` 用 `UnmappedMemberHandling.Skip`(`:56`、`:439`),旧 BaseLib / 原版读档会忽略这些列,回滚安全。

#### 3.2 自定义消息 id 守卫(PASS)

- 改动:`Patches/Networking/CustomMessagePatches.cs:92-99`,前缀遇到 `id == 0` 直接放行。
- 依据:游戏 id 表是 0 基数组(`NetTypeCache.cs:66-73`),id 0 是真实注册消息;反序列化唯一入口是 `MessageTypes.TryGetMessageType`(`NetMessageBus.cs:58-61`)。`CustomMessageWrapper.WrapperMessageId` 是自动属性 `byte`,在 `AdjustCustomMessageKeys.Fuckery` 分配 ≥128 槽位之前恒为 0(`Abstracts/CustomMessage.cs:13-20`、`CustomMessagePatches.cs:65-84`)。旧代码会把 id 0 的消息当成 wrapper 反序列化并在 `IdToCustomMessage` 抛异常。
- 风险:分配逻辑在 128..255 槽位耗尽(?byte 溢出)时理论上会回绕到低槽位;这是既有隐患,当前 mod 数远未触及。

#### 3.3 静态字段 IL(PASS)

- 改动:`Patches/Features/ModInteropPatch.cs:325-330、342-347`,`Stfld`/`Ldfld` → `Stsfld`/`Ldsfld`。
- 依据:`Stfld` 需要 (objref, value) 两个栈槽,而静态互操作属性 setter 的 `Ldarg_0` 只有 value → 旧 IL 非法。插入点由 `QuickTranspiler` 的 `Match(ret).Step(-1).Insert(Insert)` 决定(`ModInteropPatch.cs:386-391`),`Step`/`Insert` 实现在 `InstructionPatcher.cs:158-166`、`:475-486`;此时原 setter 体已执行、求值栈为空,`Ldsfld`/`Stsfld` 前后类型匹配(`targetField.FieldType == property.PropertyType` 已断言)。
- 风险:互操作属性仍要求 getter 体自己先压一个值(被 `Pop` 丢掉),这是既有约定,文档未变。

#### 3.4 枚举隔离(PASS,含本轮修补)

- 改动:`Patches/Content/CustomEnums.cs`,把逐类型 `GetFields()`/`Attribute.IsDefined`、逐字段声明校验、逐字段生成(`GenerateForField`)各自包 try/catch,失败按 assembly 名报 Error 后跳过;`continue`→`return` 的改写保持了原来的控制流(`CardKeyword` 段和 `CustomReward` 早退原本就结束该字段处理)。
- 依据:`Logger.Error(string, int skipFrames = 1)` 存在(`Logger.cs:186`);`KeyGenerator` 的 `Incrementers`/`TypeHalfSizes` 明确覆盖 byte、sbyte、short、ushort、int、uint、long、ulong(`CustomEnums.cs:113-148`)。
- 本轮修的真实 bug:`dict.Add((int) key, ...)` 是**拆箱**,而 `KeyGenerator` 生成的值保留枚举底层类型 → byte/short 等非 int 底层枚举在这里抛 `InvalidCastException`,隔离后会变成"值已设置但反查表缺项"的静默错误。改为 `Convert.ToInt32` + `OverflowException` 警告(新 `CustomEnums.cs:334-349`)。
- 风险:同一 `[CustomEnum]` 静态字段出现在多个派生类型时(`GetFields()` 的 FlattenHierarchy)会重复生成,旧版直接崩,现在变成多条 Error 日志。可接受,但会把"mod 作者写错"降级为日志。

#### 3.5 DefaultLoc 探测(PASS,含本轮修补)

- 改动:`Patches/Localization/DefaultLoc.cs:63-77`,改为列 `res://<mod>/localization/` 根目录、只收以 `/` 结尾的子目录名并去掉斜杠。
- 依据:Godot `ResourceLoader.ListDirectory` 返回的子目录名带尾随 `/`(GodotSharp.xml:7078-7080);旧代码列的 `localization/<当前语言>/` 里只有 `.json` 文件,`existingLang` 恒为空 → 探测是死代码。目标方法参数名 `language`/`file` 与 Harmony 绑定一致(`ModManager.cs:966`);`DirAccess.DirExistsAbsolute` 对 `res://` 生效(游戏自身 `LocManager.cs:421`、`ActModel.cs:321` 都这么用)。
- 本轮修的真实 bug:语言选择是按**目录**而不是按**文件**。`existingLang.Contains("eng")` 只要 mod 有任何英文目录就整表跳过;偏好循环取"第一个有任意文件的目录"。探测修好后这两条第一次真正生效,于是 `eng/cards.json` + `zhs/powers.json` 的 mod 会丢掉 `powers.json`,`zhs/cards.json` + `jpn/powers.json` 的 mod 也会因为 zhs 排序靠前而丢掉 `powers.json`。改为对候选语言逐个 `ResourceLoader.Exists(.../<lang>/<file>)`(`DefaultLoc.cs:78-99`)。
- 风险:release 构建不落 Debug 日志(实机日志无 `[DEBUG]` 行),所以 `Found N default loc files` 这条 Debug 只能本地调试看到,烟测要用"合并条数/文本可读"间接判断(见 §6 S6b)。

#### 3.6 OptionPools 空池/零权重(PASS)

- 改动:`Utils/OptionPools.cs:37-93`,抽出 `TakeOrThrow`:先判空,再判总权重,消息点名古代 id。
- 依据:`AncientOption.Weight` 是 `int`(`Utils/AncientOption.cs:12`);`WeightedList<T>` 的 `Add(T)` 用 `IWeighted.Weight`(显式权重会被 `[..pool.Where(...)]` 展开重建重置),所以 `pool.Sum(o => (long)o.Weight)` 恰好等于 `rollPool._totalWeight`;`GetRandom` 在空表抛 `IndexOutOfRangeException`、总权重 0 抛 "Roll 0 failed…"(`Utils/WeightedList.cs:41-64`)。`long` 求和不会自溢出。
- 风险:仍是**抛异常**(不是降级);异常发生在 `GenerateInitialOptions`(`CustomAncientModel.cs:75`),古代事件会失败。这是刻意的:少于 3 个可选项无法凭空补足,只把失败信息变得更可诊断。

### 4. 本轮改动

1. `Patches/Localization/DefaultLoc.cs`:DefaultLoc 自动探测按文件而非按目录判定(eng 快捷路径 + 偏好语言循环)。
2. `Patches/Content/CustomEnums.cs`:生成值反查登记由 `(int)key` 拆箱改为 `Convert.ToInt32(key)`,并对超出 int 的值显式告警。

两处都不改公开 API、不改存档/网络格式。

### 5. 剩余风险

- 枚举隔离把"mod 作者误用 `[CustomEnum]`"从启动崩溃降级为 Error 日志;发布说明里要提醒作者 grep `[CustomEnums] Swallowed`。
- `SavedSpireField` 的 mod id 命名空间依赖 `WhatMod.FindMod` 的注册时序;拿不到 owner 时会退回旧键并 Warn(`Utils/SpireField.cs` `TryResolveModId`)。同一 assembly 被多个 mod 复用时前缀相同,需 `WithModId` 显式指定。
- `HookUtils` 吞掉监听器异常后,一个坏 listener 只会留下一条 Error;它造成的"少加了一个 buff"不会被上报,排错只能靠日志。
- 关联奖励消息(`CustomLinkedRewardChoiceMessage`)缓存上限 256 是本地取值(`MaxBufferedChoices`),不是文档契约。
- 静态互操作属性仍依赖"getter 体压一个值再被 Pop"的写法,文档未覆盖 `Stsfld` 路径。

### 6. 实机烟测清单(live smoke checklist)

> BaseLib 是所有 mod 的依赖,所以判据分两层:**BaseLib 自身启动行** + **每个依赖方的启动行**。
> 所有副本共用一份用户目录,日志固定为 `G:\appdata\C-Users-o_Obl\Roaming\SlayTheSpire2\logs\godot.log`(轮转为 `godot<时间戳>.log`),因此**每一步都必须先用 `Found mod manifest file <副本路径>\mods\...` 确认本次日志来自哪个副本**。

#### 6.1 测试副本与前置

| 副本 | 现有 mods(2026-10-26 快照) | 用途 |
|---|---|---|
| `E:\Slay the Spire 2` | 40+ mod;`mods\BaseLib\` 三件套齐全 | 主测:全部依赖方(Spire1 / ActsFromThePast / HeartShake / MpConfigSync / HoldYourTurn / intentgraph2 / MintySpire2 …) |
| `E:\sts2smokeA` | BaseLib、Clawd、SmokeLog、STS2-RitsuLib、DirectConnectIP | 快速启动 + Clawd(SmokeLog 观察器) |
| `E:\sts2smokeB` | 以上 + Forms、Watcher、ClawdAutoSlay | Forms / Watcher 依赖方、双人联机 |
| `G:\omp works\Sts\_runtime\sts2-test-client-B` | Watcher 0.10.2 + BaseLib | Forms 会话常用;备用 |

前置:

1. 游戏必须已退出;杀进程只用 `powershell -File "G:\omp works\.tmp\kill-test-game.ps1"`(先 `-DryRun` 看清单),**禁止**按进程名杀。
2. 备份共享用户目录:`G:\appdata\C-Users-o_Obl\Roaming\SlayTheSpire2\mod_configs\` 与 `...\steam\<steamid>\settings.save` → `G:\omp works\.tmp\<日期>-baselib-smoke\backup\`,并记 `hashes-before.csv`。
3. 部署:`BaseLib.dll`、`BaseLib.pck`、`BaseLib.json`(manifest 必须已是 `"version": "v3.4.8"`)→ `<副本>\mods\BaseLib\`。
4. 启动:`Start-Process "<副本>\SlayTheSpire2.exe" -WorkingDirectory "<副本>" -ArgumentList "--rendering-driver","opengl3"`。
5. 说明:release 构建**不落** Debug/VeryDebug 行(实测日志无 `[DEBUG]`),清单只依赖 Info/Warn/Error。
6. 版本仲裁:同一 mod id 同时存在于 `mods\` 与创意工坊时,引擎取版本高者、禁用另一个(Steam 副本实测:`Mod with ID BaseLib is loaded both via Steam and local mods directory. Steam version (v3.4.7) is greater than local version (v3.4.5), so we are disabling the local version.`)。所以 S1 必须确认赢的是本地 3.4.8。
7. `G:\steam\steamapps\common\Slay the Spire 2` 是用户游玩副本,**不得写入**;烟测只用上表四个副本。
8. `sts2smokeA/B` 装有 `SmokeLog`(观察器):每次启动会在 `G:\omp works\.tmp\smokelog\<session>.jsonl` 写 `session` 行,进战斗后写 `card_played`。用它交叉确认"进过战斗、打了几张牌",不必只依赖 godot.log。

#### 6.2 需要的 mod 与依赖方启动行

必需:BaseLib 3.4.8。其余按副本现装即可。逐条 grep(全部必须出现,且不得伴随 `[ERROR]`):

| mod id | 观察行(grep 片段) |
|---|---|
| BaseLib | `[BaseLib] Starting PatchAll for assembly BaseLib, Version=3.4.8.0` |
| BaseLib | `[BaseLib] Registered config for mod BaseLib` |
| Spire1 | `[Spire1] [Spire1] powers gate: r8c ModelDb registration fuse verified installed.` |
| ActsFromThePast | `Finished mod initialization for 'Acts from the Past' (ActsFromThePast).` |
| ActsFromThePastMultiplayerBalance | `Finished mod initialization for` + `(ActsFromThePastMultiplayerBalance)` |
| HeartShake | `[HeartShake] HeartShake initialized (StS1 heartbeat shake` |
| MpConfigSync | `[MpConfigSync] MpConfigSync initialized (send patches registered=True` |
| HoldYourTurn | `[BaseLib] Registered config for mod HoldYourTurn` |
| intentgraph2 | `[BaseLib] Registered config for mod intentgraph2` |
| MintySpire2 / ActToggler2 / STS2_Things | `[BaseLib] Registered config for mod <id>` |
| Watcher(仅 smokeB / _runtime) | `[Watcher] 已初始化观者模组。` |
| Clawd(仅 smokeA/B) | `Finished mod initialization for '<显示名>' (Clawd).` |
| Forms(仅 smokeB) | `Finished mod initialization for` + `(Forms)` |

注:共享用户目录的 3.4.7 实机日志里,`Perfect`、`AutoAnthony`、`AutoAnthonyWatcher`、`ViolaSnakeBite` 的加载结果是 `state_after=Disabled`(mod_list 在共享 `settings.save` 里,各副本同源)。要覆盖这些依赖方,需先在 mod 列表里启用,测完恢复原状。

#### 6.3 步骤

**S1 版本门禁(硬)**

- 操作:按 §6.1 部署并启动到主菜单。
- 观察:`Select-String -Path <log> -Pattern 'Starting PatchAll for assembly BaseLib, Version=3.4.8.0|Steam version \(v3\.4\.7\) is greater'`。
- 通过:`Version=3.4.8.0` 出现,且"Steam version … is greater than local version"**不**出现(出现说明本地 3.4.8 没赢,测的是 3.4.7,直接判 FAIL 停)。
- 可见:主菜单正常出现,`Loaded <N> mods` 行。

**S2 枚举隔离 + 枚举键类型**

- 观察:`Starting custom enum generation for <N> modded types`;`Select-String -Pattern '\[CustomEnums\]'`。
- 通过:N 与该副本上一版自身基线同量级(Steam 全量副本 3.4.7 实测 `15083`;smokeA/B 只有几个 mod,数字会小得多,按同副本对比);`[CustomEnums] Swallowed an exception` = 0 行、`does not fit the int-keyed` = 0 行。
- 失败处理:若出现 `Swallowed`,行内已带字段全名与 assembly/mod 名 → 记录为**该 mod 的作者错误**,不算 BaseLib 回归;但若同一字段在 3.4.7 下是崩溃点,需在报告里点名。

**S3 附魔存档上下文(本版新增可观测行,硬)**

- 观察:`Select-String -Pattern 'custom save data to SerializableEnchantment|new properties to SerializableEnchantment'`。
- 通过:两行各出现一次(`Adding custom save data to SerializableEnchantment.` / `Added <N> new properties to SerializableEnchantment`)。3.4.7 基线只有 Card/Relic/Potion/Reward/Player/Run 六种,没有 Enchantment。
- 追加功能验证:开一局,给一张牌附魔(原版或 mod 卡),`保存并退出`→ 重新读档;附魔仍在、读档日志无 `ModelNotFoundException`、无 `Dropping card modifier`。

**S4 SavedSpireField / 消息 id**

- 观察:`[BaseLib] Found <N> SavedSpireFields.`、`[BaseLib] Using IDs <A> and <B> for custom message wrappers.`。
- 通过:A、B 均 ≥128;全程无 `KeyNotFoundException`、无 `IdToCustomMessage` 相关堆栈。
- 说明:id==0 守卫本身只在"包装器 id 尚未分配"时产生差异,日志侧只能看"没异常",真正的功能判据在 S9 联机。

**S5 依赖方启动行(硬)**

- 操作:照 §6.2 表格逐条 grep 本次日志。
- 通过:表内每个 id 的启动行都在,且不带 `[ERROR]`;新增断言 `Select-String -Pattern 'Mod config file collision'` = 0 行(这是本分支新增的检测)。
- 失败处理:若出现 `Mod config file collision: mods 'A' and 'B' both use <path>`,记录两个 mod id 与路径 —— 说明两者共用同一个 cfg 文件,后写覆盖前者。

**S6 配置界面 / 迁移**

- S6a:设置 → Mods 标签逐个 mod 看标题与开关。
  - 通过:标题都有文字、页面不崩、无 `ArgumentOutOfRangeException`;除语言表确实缺失的语种外不应出现 "Unknown mod name"。
- S6b:语言回退(DefaultLoc)。
  - 操作:把游戏语言切到 mod 通常**不提供**的语种(如 `ita`/`kor`),重启,进一局看 mod 卡牌/遗物文本。
  - 观察:`Select-String -Pattern 'Found loc table from mod:'` 的行数与本轮改动前同语种基线比较;以及卡面/遗物描述是否为可读文本而不是原始 key。
  - 通过:无 `LocException`/`Path does not exist`;mod 文本要么是目标语言、要么是回退语言的可读文本;行数不低于基线(改动只会**增加**回退路径,不会减少)。
- S6c:配置文件名迁移。
  - 观察:`Select-String -Pattern 'Migrating config for'`;以及 `mod_configs\` 下新增的 `<Assembly>.<Namespace>.cfg`。
  - 通过:出现迁移 Info 行,旧文件保留为备份,设置值不丢(对照迁移前截图/数值)。

**S7 钩子隔离**

- 操作:打 2~3 场带多 mod 能力/遗物的战斗(Spire1 角色 + AFTP 敌人)。
- 观察:`Select-String -Pattern '\[HookUtils\] Swallowed an exception'`。
- 通过:0 行;回合能正常推进、不卡死、无 `combat is stuck`。
- 失败处理:若出现,行内带 hook 接口名 + listener 全名 + mod id → 记录为该 mod 的 listener bug,同时确认战斗继续(这正是隔离的目的)。

**S8 OptionPools**

- 操作:触发一次自定义古代(AFTP/Spire1 的古代房即可)。
- 观察:`Select-String -Pattern "has fewer eligible options|total weight of 0"`。
- 通过:三个遗物选项正常出现,上述两串 0 行。若出现,消息已点名 `Ancient '<id>'` → 判定该 mod 选项池不足(≥3 个可选、总权重 >0),记录后交给对应 mod。

**S9 多人(可选,需两副本)**

- 操作:主机 `E:\sts2smokeA`、客机 `E:\sts2smokeB`,DirectConnectIP 直连,进一局并开一次奖励选择。
- 观察:`Select-String -Pattern 'CustomLinkedRewardChoiceMessage|cannot be applied: set is not on the stack'`。
- 通过:两端奖励选择一致,无上述告警/错误。说明:3.4.8 的关联奖励改动只影响使用 `CustomLinkedRewardSet` 的 mod;本工作区没有确认使用者,故此步主要验证"不崩、不乱序"。

**S10 收尾**

- 退出游戏(按路径杀),还原 `mod_configs` 与 `settings.save` 备份并用 `hashes-before.csv` 比对。
- 把本次 `logs\godot.log`(或轮转副本)路径、每个 S 步骤的 grep 结果、以及任何 `[ERROR]` 行摘录回填到本节下方。

#### 6.4 总判据

- 硬门禁(启动期,任一失败即 v3.4.8 不可发):S1、S2、S3、S5。
- 运行期:S4、S6、S7、S8。
- 可选:S9(有第二客户端时必做)。
- 只有 S1–S8 全部通过、且日志里没有 BaseLib 自身抛出的异常,才可以把"3.4.8 实机通过"写进报告。

### 7. 待办

- [ ] 主会话构建 3.4.8 并按 §6 执行烟测(本轮只做静态审查,未构建、未运行)。
- [ ] S3 的附魔存档往返需要在有附魔来源的局内做(原版附魔或 mod 附魔均可)。
- [ ] S9 需要第二客户端;若只做单机,S9 记为未验证。
- [ ] 若要覆盖 `Perfect`/`AutoAnthony` 依赖方,先在 mod 列表启用再测,测完恢复。
- [ ] 分支清理:`fix/*` 11 个分支内容已全部在 HEAD,可删;不要 `git merge`。

## 2026-10-27 实机烟测 BL348A（后台 autoslay，AGENTS §2e-2）

- 构建：`G:/dotnet10/dotnet.exe build BaseLib.csproj -c Release -p:CopyToModsFolderOnBuild=false`，0 错误（1130 条 CS1591 文档注释告警）。系统 dotnet 只有 9.0，LangVersion 14 需要 `G:/dotnet10`。
- 部署：`E:/sts2smokeB/mods/BaseLib/` 换 `publish/BaseLib.dll` + `BaseLib.json`（v3.4.8）；pck 沿用 3.4.7 字节（未重建，本分支只改 `.cs`）。旧件备份 `.tmp/20261027-baselib-smoke/backup/smokeB-BaseLib-3.4.7/`。
- 启动：`--rendering-driver vulkan --autoslay --autoslay-seed=BL348A`（ClawdAutoSlay 提供 autoslay，引擎自带分支被 `IsReleaseGame()` 关闭，`NGame.cs:694`）。日志存 `.tmp/20261027-baselib-smoke/godot-smokeB-BL348A.log`。
- 结果：
  - S1 通过：`Starting PatchAll for assembly BaseLib, Version=3.4.8.0`；无 "Steam version … is greater"；`Loaded 8 mods (8 total)`；`Applied 283 patches successfully, 0 failed`。
  - S2 通过：`Starting custom enum generation for 9330 modded types`；`[CustomEnums] Swallowed` 0 行、`does not fit the int-keyed` 0 行。
  - S3 通过（启动部分）：`Adding custom save data to SerializableEnchantment.` + `Added 0 new properties to SerializableEnchantment` 首次出现。附魔读档往返未做（需人工）。
  - autoslay 跑到 Act 2 Floor 4，共 21 个房间，无 BaseLib 异常。
  - 第 1 分钟守卫误报后停止测试（`kill-test-game.ps1`，按 exe 路径）。原因：bash 比较 PowerShell 输出 `"0\r"` 与 `"0"` 不等；实际没有 Steam 游戏在跑（`G:/steam/logs/console_log.txt` 当时无任何启动记录）。守卫已改为 `.tooling/sts2-steam-guard.ps1` 退出码。
  - 测试身份存档（`steam/76561199520000012/modded/profile1/saves/`、`replays/latest.mcr`）未在运行前备份，autoslay 写入了它们；只影响 smokeB 测试身份。
- 其它发现（不属 BaseLib）：3 条 `Found end tag b, expected 跑一个` 等 BBCode 异常，来自 `Sts/sts2-clawd/mod/Clawd/localization/zhs/events.json`（文本里的方括号被当成标签）。
- 恢复：`mod_configs` 与 `settings.save` 还原，SHA256 比对 mismatch=0。存档只写到 smokeB 的 Goldberg 测试身份 `76561199520000012`（`E:/sts2smokeB/data_sts2_windows_x86_64/steam_settings/configs.user.ini`），未动真实账号。
- 未做：S4 之后的人工步骤（附魔存读档、DefaultLoc 多语言表、OptionPools 空池）。
