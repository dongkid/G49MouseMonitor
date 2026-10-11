# 独立对抗复核报告（task-4 / verify-highrisk）

复核对象：W1~W5 五条高风险候选问题。方法：只读源码 + 在 %TEMP% 里另建独立实验工程（`g49verify`，WinForms 控制台）复现关键时序；项目内除本文件外未改动任何东西。

## 结论速览

| 编号 | 判定 | 严重度 |
| --- | --- | --- |
| W1 单实例/唤出 | 部分坐实：(a)(c) 证伪；(b)「信号被吃」证伪、但发现「句柄未建时唤出被静默丢弃」；(d) 坐实 | 低 |
| W2 关停竞态 | 坐实（需 Join 超时这一前提）；其中「重复释放同一指针」证伪、「Join 必然超时」证伪 | 低 |
| W3 不会漏提醒 | 坐实（条件性：系统/策略屏蔽通知时） | 低-中 |
| W4 信号丢失 | **证伪** | 无 |
| W5 预警阶梯 | 坐实（仅「重置迟滞」一条）；跳档/往复/改阈值大部分证伪 | 低 |

---

## W1 单实例 / 二次双击唤出

**判定：部分坐实。** (a) 工作；(b) 事件不会被吃，但句柄未建时唤出会被静默丢弃；(c) 工作；(d) 坐实（低危）。

判定依据：
- (a)(c) 实测（w1host + w1guest 两进程，复刻 Program.cs:32-38/63-83 与 MainForm.cs:311-337）：隐藏启动时 `Visible=False, HandleCreated=True`（Application.Run 确实只建句柄不显示），第二实例 Set 事件后下一拍 `Visible=True, AllowVisible=True`。即「双击唤出」与「--background 后被双击唤出」都正常。
- (b) 实测 T2：AutoReset 事件在**无人等待时** Set，随后 WaitOne 立即返回 True（信号保留）；三次 Set 只折算成一个待处理信号。所以「AutoReset 把事件吃掉」这一具体指控不成立。**但**实验 w1early 发现另一条真实路径：在窗口句柄创建之前收到唤醒时，`RequestShow()` 里的 `BeginInvoke` 抛 InvalidOperationException，被 MainForm.cs:325 的空 catch 吞掉 → 这次唤出**永久丢失**（实测 BeginInvokeFailures=1，之后窗口一直 Visible=False，事件已被消费不会重试）。真实窗口只有 waiter.Start()（Program.cs:81）到 Application.Run（:83）内部建句柄之间的亚毫秒级，命中概率极低；我把窗口人为放大到 120ms 才稳定复现。
- (d) 坐实：Program.cs:29 解析了 background，但第二实例分支（:34-38）**完全不看这个变量**，无条件 `Set()`。实测 run A（无 --background）与 run B（带 --background）行为逐拍一致，隐藏窗口都被弹了出来。
- 附带：真正退出期间（mainform 已 Dispose、进程还持着 Mutex）第二个实例只会 Set 后退出，第一实例的 RequestShow 因 IsDisposed 直接 return → 这次双击什么也不会发生。

危害场景：(d) 需要有人用 `--background` 再启一个实例——启动项正是 `"路径" --background`（AutoStart.cs:39），正常每次登录只跑一次；若启动项重复、或第三方启动器再拉一次，开机后窗口会自己弹出来。

证伪尝试：把「信号丢失」当主诉逐条打——Set 早于 waiter 线程、Set 早于事件创建、连续多次 Set，全部保留（T2）。结果：信号层面证伪，改为「处理层在句柄未建时会丢一次唤出」。

## W2 关停竞态

**判定：坐实（前提是 Join(1500) 超时）；「对同一指针重复释放」证伪；「FreeHGlobal 后继续使用」坐实但仅限退出瞬间。**

判定依据：
- 重复释放：G49Device.Dispose 有 `_disposed` 守卫（G49Device.cs:256-262），MonitorService.Dispose 只由 OnFormClosed 调一次（MainForm.cs:434）→ **证伪**。
- Join(1500) 能否超时：实测（T4，真实挂起的 overlapped 读 + CancelIoEx）一次超时读的真实成本 ≈ 11~17ms，CancelIoEx 成功后事件 0ms 置位 → **不是** timeout+500ms；只有 CancelIoEx 失败（实测返回 false / ERROR_NOT_FOUND）且事件不置位时才会白等 500ms（实测 500ms）。Drain 在队列空时第一次 ReadRaw 返回 null 就 break（G49Device.cs:160-169），所以常态一次查询 ≈ 15ms(Drain 空读超时)+数 ms，与项目自身证据吻合（docs/evidence-idle-gap.txt 往返 3~6ms）。用 dist 的温度副本实测一次完整重扫（8 个接口 + 3 次查询）整进程 660ms（含 .NET 启动），即单次带重扫的 Poll ≈ 0.4~0.5s。**结论：常态余量 50 倍以上；理论最坏（WriteRaw 500+500、读循环 200+500、Drain 515、两次尝试再叠一次 8 接口重扫）可达 2~10s 级——即在设备异常（句柄失效/被拔、CancelIoEx 失败）时可构造超时，但非常态。**
- 超时后的并发访问：MonitorService.cs:243-246 在 Join 超时后直接 `_device?.Dispose()`，而监控线程可能正停在 WriteRaw/ReadRaw 里用同一个 `_overlapped/_event/_handle`（G49Device.cs:190-253、255-262），**没有任何同步**。此时 Marshal.FreeHGlobal(_overlapped) 后内核/驱动仍可能往这块内存写完成状态（use-after-free），CloseHandle 掉在途 IO 用着的 event/handle → 代码层面坐实；但该路径只在退出时可达，进程本来就要结束，用户可见后果≈0。
- 更确定的后果在 `_wake.Dispose()`（MonitorService.cs:246）：T1 实测 MRE 在 Dispose 后 Wait/Reset **都抛 ObjectDisposedException**；w2thread 子进程实测后台线程未捕获该异常 → 进程以 0xE0434352 终止（exit=-532462766），「程序退出」日志与后续清理被跳过，AppDomain.UnhandledException 会多写一条 crash.log。
- 附带：Dispose 置 `_device=null`（:245）之后监控线程可能再赋一个新的 G49Device（Poll 内 :121），造成句柄/事件/HGlobal 泄漏，但进程即将退出。

危害场景：点「退出」的瞬间监控线程正卡在异常 IO（设备已被拔/句柄失效）> 1.5s → 进程被后台线程异常终止，退出日志不完整 + crash.log 多一条；理论上的内存破坏发生在正在死亡的进程里。

证伪尝试：(1) 「重复释放」→ 被 _disposed 守卫证伪；(2) 「每次超时都要多等 500ms」→ 实测证伪（CancelIoEx 成功时 0ms）；(3) 「正常设备也会超时」→ 实测常态 20~30ms、重扫 0.4~0.5s，证伪，仅异常 IO 下成立。（附带说明：我把原始 OVERLAPPED 挂到 .NET 自己的异步管道句柄上做实验时，进程在收尾阶段出现了 System.AccessViolationException——那是我实验装置的副作用（.NET 把该句柄挂在 IOCP 上），不能外推到 HID 句柄，特此声明避免误导。）

## W3 「不会漏提醒」

**判定：坐实（条件性）。严重度 低-中。**

判定依据：Notifier.cs:138-146 只有在 `SystemToast.Show(...)` **返回 false**（即 catch 到异常）时才置 `_toastBroken` 并回退卡片。而 SystemToast.Show（Notifier.cs:42-68）返回 true 的含义只是 `CreateToastNotifier(...).Show(toast)` 没抛异常。旁证：代码自己在 Notifier.cs:52 注册了 `toast.Failed`（日志写「系统通知被系统拒收」）——作者知道存在异步拒收，但该回调**只写日志，不触发卡片兜底**。另外 `ToastNotifier.Setting` 从未被检查（微软定义：该属性告诉你是否存在 app/user/system 级别的屏蔽，取值 DisabledForApplication/DisabledForUser/DisabledByGroupPolicy/DisabledByManifest）；本机只读探测 `CreateToastNotifier("G49BatteryMonitor").Setting = Enabled`，说明这项检查可用但没用。

危害场景：用户在 设置→系统→通知 里关掉本程序的通知（或被组策略统一关闭）→ `Show()` 不抛异常 → 不弹卡片 → 除了提示音（可关）没有任何可见提醒，而 README.md:30 的承诺是无条件的「不会出现该提醒却没提醒」。

证伪尝试：想证伪「Show()=true 不等于送达」——项目自带 dist/notify-test.txt 证明本机通知中心确实收得到（条数 0→1），但这只说明「没被屏蔽时能送达」，不能说明「被屏蔽时会走卡片」；因此证伪失败。另：专注助手/勿扰属于较轻情形——通知仍会进通知中心（README 承诺的落点就是那里），不算违背。

## W4 Wait 之后紧跟 Reset 会丢信号

**判定：证伪。**（本条是本次最明确的过度解读）

判定依据：`_wake.Reset()`（MonitorService.cs:99）之后紧接着就是 while 条件判断与下一次 `Poll()`，不存在 Reset 之后重新长睡的路径。所以 Set 落在 Wait 之前/期间 → Wait 立即返回；落在 Wait 返回与 Reset 之间 → 会被 Reset 清掉，但**紧随其后的那次 Poll 一定立刻执行**，该请求与已经生效的「立刻轮询」等价（RequestRescan 的 `_forceRescan` 还是粘性标志，落到窗口里也会被下一次 Poll 消费）。唯一 Reset 后不再轮询的分支是 `_stopping=true`（退出），此时丢不丢都无意义。

实测：T3a 在 2 秒内被高频 Set 打了 68 次轮询，最坏一次「Set→下一次 Poll 开始」延迟 47ms（若真被吞掉会看到整整 300ms 的等待）；T3b 强制把 Set 塞进 Wait/Reset 窗口 20 次，紧随其后的 Poll 都在 15~32ms 内发生。因此「点立即刷新没反应」「插拔后最长等 5 分钟」不成立。

## W5 预警阶梯

**判定：坐实（仅「重置迟滞」一条，严重度低）；跳档、往复、改阈值的多数担心证伪。**（用逐句复刻 MonitorService.cs:187-237 的状态机做序列仿真）

- 跳档 40%→8%→4%：只发 tier2、tier3，跳过 tier1——这是更紧急的提醒覆盖了较轻的，不构成漏提醒（tier3 一定会发）。
- **真实缺口（坐实）**：重置条件是 `_stage>0 && percent > WarnNotice+5`（:214，严格大于）。stage=3 之后，只要电量在**没有 Charging=true** 的情况下回升到 (Critical, Notice+5] 即默认 6%~25%，stage 就永远卡在 3，之后掉到 20%、10%、1% **全程静默**。仿真 S2：3%→22%→20%→10%→1% 一条提醒都没有；S5（改小阈值到 3/2/1）同样静默。
  触发路径：回升 ≥6 点且程序没看到充电。这台鼠标是内置电池（selftest 读数 4309mV、界面提示「可以拔线了」），所谓「换电池」基本不存在；现实路径是「接收器没插/鼠标不在线时用别处补了电，回来只有 ≤25%」。窄，但真实存在；而且它恰是代码注释里写明要处理的「换了电池/补过电」场景。
- 充放电往复：每次 Charging=true 都把 stage 归零（:194），所以 19% 拔线后再次低于阈值会**再提醒一次**（S3/S7 都复现）；接触不良的线抖动会重复弹窗/响铃（同 Tag 的通知互相替换，不会堆积）。属设计取舍，最多算烦人。
- 改阈值：改大不会漏掉更高档提醒（S4：18%→tier1，改 Notice=50 后 12/8/3 仍按序触发）；改小只会复现上面的静默。UI（MainForm.cs:246-256）强制 Critical≤Low≤Notice；三档取等时最低档因 else-if 顺序永远走不到，属退化配置；三档都设 100% 会在 100% 直接报「严重不足」然后永远沉默（S6b），UI 允许。
- 单调放电（最常见情形）下「每档只提醒一次」成立。

---

## 哪几条最可能被过度解读

1. **W4 全部**——结构性上不可能丢：Reset 后面永远跟着一次 Poll，实测最坏延迟 47ms。应直接撤下。
2. **W2 的两个子指控**：「对同一指针重复释放」（_disposed 守卫已证伪）、「Join(1500) 会超时导致并发」作为**常态**风险（实测常态 20~30ms、重扫 0.4~0.5s，余量 50 倍以上）。真正值得留的只有两点：超时后无同步的 Dispose（低）、以及 `_wake.Dispose()` 会让后台线程以未捕获异常终止进程（有 crash.log 为证）。
3. **W1(b)「AutoReset 吃掉事件」**——自动重置事件在无人等待时保留信号，实测证伪；但它顺带暴露的「句柄未建时 BeginInvoke 抛异常被吞、唤出丢失」是真缺口（概率很低）。
4. **W5 的「跳档/往复/改阈值就是漏提醒」**——正常单调放电不漏关键档位；往复只是重复提醒（烦人）。真正能坐实的只有 `+5` 迟滞导致的静默区间。
5. 相对地，**W1(d)** 和 **W3** 是这五条里更难辩护的两条：W1(d) 是代码层面确定的行为错位（--background 被忽略），W3 是对 README 无条件承诺的实质削弱。

## 未落盘说明

按 task-4 的 write scope 只创建了本文件 `docs/review/verifier-highrisk.md`；`src/`、`dist/`、配置一律未改（重扫计时是把 dist 复制到 %TEMP% 后跑的，自检输出写在副本目录里）。
