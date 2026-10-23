using System.Text.Json;
using ControllerClient.ViewModels;
using ControllerClient.Services;
using ControllerClient.Services.Ptt;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static JsonElement Binding(object data) => JsonSerializer.SerializeToElement(data);

var settings = new SettingsViewModel { PttBindings = [] };
settings.MarkSaved();
var keyboard = Binding(new { kind = "keyboard", key = "v" });
var mouse = Binding(new { kind = "mouse", button = "x1" });
var joystick = Binding(new { kind = "joystick", button = 0, device = 0, device_name = "脚踏" });
settings.AddPttBinding(keyboard);
settings.AddPttBinding(mouse);
settings.AddPttBinding(joystick);
Check(settings.PttBindings.Length == 3 && settings.IsDirty, "添加多个 PTT 应保留全部绑定并允许应用");
settings.MarkSaved();
settings.AddPttBinding(Binding(new { kind = "keyboard", key = "V" }));
settings.AddPttBinding(Binding(new { kind = "joystick", button = 0, device = 0, device_name = "新名称" }));
Check(settings.PttBindings.Length == 3 && !settings.IsDirty, "相同按键或相同摇杆按钮不应重复添加");
var saved = JsonSerializer.Serialize(settings.PttBindings);
var restored = new SettingsViewModel { PttBindings = JsonSerializer.Deserialize<JsonElement[]>(saved)! };
Check(restored.PttBindingItems.Select(item => item.Label).SequenceEqual(settings.PttBindingItems.Select(item => item.Label)), "全部绑定应可通过配置 JSON 恢复");
settings.RemovePttCommand.Execute(settings.PttBindingItems[1]);
Check(settings.PttBindings.Length == 2 && settings.PttBindings.All(b => b.GetProperty("kind").GetString() != "mouse"), "移除一项不应清除其他绑定");
settings.IsWaitingForPtt = true;
settings.ClearPttCommand.Execute(null);
Check(settings.PttBindings.Length == 2 && !settings.CanEditPtt, "录制中不应清除绑定");
settings.IsWaitingForPtt = false;
settings.CapturePtt = start =>
{
    if (start)
    {
        Check(settings.IsWaitingForPtt, "发起录制前必须已进入等待状态");
        settings.AddPttBinding(mouse);
        settings.IsWaitingForPtt = false;
    }
    return Task.CompletedTask;
};
await settings.SetPttCommand.ExecuteAsync(null);
Check(settings.PttBindings.Length == 3 && !settings.IsWaitingForPtt, "即时录制结果应追加绑定且正确结束等待");
settings.ClearPttCommand.Execute(null);
Check(settings.PttBindings.Length == 0 && !settings.CanClearPtt, "清空绑定后应允许保存空数组");
Console.WriteLine("PASS: 多 PTT 添加、去重、JSON 恢复、单项删除、录制竞态及清空检查");

var status = new ConnectionStatusViewModel();
status.SetVoiceState("connecting");
status.UpdateVoice(Binding(new { connected = false, running = false, transport = (string?)null, latency_ms = (double?)null }));
Check(status.VoiceText.Contains("连接中"), "启动阶段的空快照不能覆盖连接中状态");
status.UpdateVoice(Binding(new { connected = true, running = true, transport = "UDP", latency_ms = 23.0 }));
Check(status.VoiceText == "语音：已连接 · UDP · 23 ms", "UDP 必须使用当前通道延迟");
status.UpdateVoice(Binding(new { connected = true, running = true, transport = "TCP", latency_ms = (double?)null }));
Check(status.VoiceText.Contains("TCP · -- ms"), "无 Ping 样本不能显示虚构延迟");
status.UpdateVoice(Binding(new { connected = false, running = true, transport = (string?)null, latency_ms = (double?)null }));
Check(status.VoiceText == "语音：重连中", "掉线应清除旧传输和延迟");
status.SetVoiceState("error");
status.UpdateVoice(Binding(new { connected = false, running = false, transport = (string?)null, latency_ms = (double?)null }));
Check(status.VoiceText == "语音：连接错误", "空快照不能抹去连接错误");
status.UpdateFsd(true, new DateTime(2026, 10, 23, 12, 34, 56), 50);
Check(status.FsdText.Contains("已连接（数据源确认）") && status.FeedText.Contains("12:34:56"), "FSD 状态应注明数据源及更新时间");
status.FeedFailed();
Check(status.FsdText.Contains("未知"), "数据源失败不能保留在线状态");
Check(AppLog.FilePath(new DateTime(2026, 10, 23), "voice") != AppLog.FilePath(new DateTime(2026, 10, 24), "voice"), "跨日必须更换日志文件");
Check(AppLog.FilePath(DateTime.Now, "client") != AppLog.FilePath(DateTime.Now, "voice"), "主程序与语音日志应分开");
Console.WriteLine("PASS: 实时连接状态、UDP/TCP 延迟、掉线/错误状态和按日日志路径检查");

// —— PTT：键名、绑定、监听合成、录制（原 controller/test_ptt.py） ——
Check(PttKeys.Name(0x56, "V") == "v" && PttKeys.Name(0x56, "v") == "v", "可打印键取字符并转小写");
Check(PttKeys.Name(0xA3, null) == "ctrl_r" && PttKeys.Name(0x20, " ") == "space" && PttKeys.Name(0xA0, null) == "shift"
      && PttKeys.Name(0x70, null) == "f1", "功能键沿用旧配置的键名");
Check(PttKeys.Name(0xE9, null) == "vk233" && PttKeys.Name(0xE9, "\u0001") == "vk233", "无名字也无字符时退回虚拟键码");
var stick = PttBinding.Joystick(3, 1, "脚踏");
Check(PttBinding.FromJson(stick.ToJson()) is { Kind: PttKind.Joystick, Button: 3, Device: 1, DeviceName: "脚踏" }, "摇杆绑定往返");
Check(PttBinding.FromJson(Binding(new { kind = "keyboard", key = "V" }))!.Key == "v", "键盘绑定读入时转小写");
Check(PttBinding.FromJson(Binding(new { kind = "joystick", button = "2" })) is { Button: 2, Device: 0 }, "按钮号可为字符串，设备缺省为 0");
foreach (var bad in new object[] { new { kind = "mouse", button = "left" }, new { kind = "joystick", button = -1 },
             new { kind = "joystick" }, new { kind = "keyboard", key = "" }, new { kind = "pedal" } })
    Check(PttBinding.FromJson(Binding(bad)) is null, $"坏绑定应被丢弃：{JsonSerializer.Serialize(bad)}");
Check(stick.Equals(PttBinding.Joystick(3, 1, "换了个名字")) && stick.Token == "3" && PttBinding.Keyboard("v").Token == "V"
      && PttBinding.Mouse("x2").Token == "X2", "设备名不参与比较，键名不含可翻译文字");
Check(PttBinding.Load([Binding(new { kind = "keyboard", key = "v" }), Binding(new { kind = "keyboard", key = "V" }),
          Binding(new { kind = "mouse", button = "x1" }), Binding(42)]).Count == 2, "加载时去重并跳过坏条目");

var changes = new List<bool>();
var watcher = new PttWatcher([PttBinding.Keyboard("v"), PttBinding.Mouse("x1")], changes.Add);
watcher.Press(PttKind.Keyboard, "v");
watcher.Press(PttKind.Keyboard, "v");
watcher.Press(PttKind.Keyboard, "b");
Check(changes.SequenceEqual([true]), "按下只翻转一次，自动重复与未绑定的键不触发");
watcher.Press(PttKind.Mouse, "x1");
watcher.Release(PttKind.Keyboard, "v");
Check(changes.SequenceEqual([true]) && watcher.IsDown, "多条绑定取或：还有一条按着就不松开");
watcher.Release(PttKind.Mouse, "x1");
Check(changes.SequenceEqual([true, false]), "全部松开才落下");
watcher.Press(PttKind.Mouse, "x1");
watcher.SetBindings([PttBinding.Keyboard("v")]);
Check(changes.SequenceEqual([true, false, true, false]), "删掉正按着的绑定要让 PTT 落下");
watcher.Press(PttKind.Keyboard, "v");
watcher.Stop();
Check(changes[^1] == false && !watcher.IsDown, "停止时正按着要落下");
var raising = new PttWatcher([PttBinding.Keyboard("v")], _ => throw new InvalidOperationException("boom"));
raising.Press(PttKind.Keyboard, "v");
raising.Stop();
Check(!raising.IsDown, "回调抛异常不能让监听卡在按下状态");
var joystickChanges = new List<bool>();
var joystickWatcher = new PttWatcher([PttBinding.Joystick(1, 0), PttBinding.Joystick(9, 0)], joystickChanges.Add);
joystickWatcher.UpdateJoysticks([new JoystickState(0, "stick", [false, true])]);
joystickWatcher.UpdateJoysticks([new JoystickState(0, "stick", [false, true])]);
joystickWatcher.UpdateJoysticks([]);
Check(joystickChanges.SequenceEqual([true, false]), "摇杆按钮驱动 PTT，设备消失即松开；超出范围的按钮视为未按");

var captured = new List<PttBinding>();
var capture = new PttCapture(captured.Add);
capture.OnJoysticks([new JoystickState(0, "油门", [true, false])]);
capture.OnJoysticks([new JoystickState(0, "油门", [true, false])]);
Check(captured.Count == 0, "录制开始时就按着的摇杆按钮不算");
capture.OnJoysticks([new JoystickState(0, "油门", [true, true])]);
capture.Fire(PttBinding.Keyboard("v"));
Check(captured.Count == 1 && captured[0].Equals(PttBinding.Joystick(1, 0)) && captured[0].DeviceName == "油门",
      "第一个新按下的按钮胜出且只回调一次");
Console.WriteLine("PASS: PTT 键名、绑定读写、多源合成、松开保护及录制检查");

// —— 电台栈（原 controller/test_radiostack.py） ——
Check(RadioStack.ParseFrequency("118.000") == 118000 && RadioStack.FormatFrequency(121700) == "121.700"
      && RadioStack.ChannelName(118000) == "FREQ_118000", "频率解析、格式和频道名");
foreach (var bad in new[] { "", "abc", "99.000", "200.000" })
{
    var threw = false;
    try { RadioStack.ParseFrequency(bad); } catch (FormatException) { threw = true; }
    Check(threw, $"非法频率应被拒绝：{bad}");
}
var stack = new RadioStack();
var changed = 0;
stack.Changed += () => changed++;
stack.Add("121.700", " zspd_twr ");
stack.Add(118000);
Check(stack.Select(r => r.FrequencyKhz).SequenceEqual([118000, 121700]) && stack.SelectedKhz == 121700
      && stack.Get(121700)!.Callsign == "ZSPD_TWR" && changed == 2, "添加后按频率排序，首个成为主频率，呼号规范化");
var duplicate = false;
try { stack.Add("118.000"); } catch (InvalidOperationException) { duplicate = true; }
Check(duplicate, "重复频率应被拒绝");
stack.SetTx(118000, true);
Check(stack.Get(118000) is { Rx: true, Tx: true }, "打开 TX 自动打开 RX");
stack.SetXc(121700, true);
Check(stack.Get(121700) is { Rx: true, Tx: true, Xc: true }, "打开 XC 自动打开 RX 和 TX");
stack.SetTx(121700, false);
Check(stack.Get(121700) is { Rx: true, Tx: false, Xc: false, TxManuallyDisabled: true }, "关掉 TX 一并关 XC 但保留 RX");
stack.SetXc(121700, true);
stack.SetRx(121700, false);
Check(stack.Get(121700) is { Rx: false, Tx: false, Xc: false }, "关掉 RX 一并关 TX 和 XC");
stack.ToggleRx(121700);
Check(stack.Get(121700)!.Rx && stack.RxFrequencies.SequenceEqual([118000, 121700]) && stack.TxFrequencies.SequenceEqual([118000]),
      "开关切换与频率集合");
stack.SetTransmitAllowed(false);
stack.SetTx(121700, true);
stack.SetXc(121700, true);
Check(stack.TxFrequencies.Length == 0 && stack.XcFrequencies.Length == 0 && !stack.Get(118000)!.TxManuallyDisabled,
      "未上席位时落下已开的 TX，不许再开 TX / XC，也不算手动关闭");
stack.SetTransmitAllowed(true);
stack.SetTx(121700, true);
Check(stack.TxFrequencies.SequenceEqual([121700]), "重新上席位后可以发话");
stack.SetLocked([121700]);
Check(!stack.Remove(121700) && stack.SelectedKhz == 121700, "值守频率不能删，并成为主频率");
stack.Select(118000);
Check(stack.SelectedKhz == 121700, "值守检查后只能选本人席位作主频率");
stack.SetLocked([]);
Check(stack.SelectedKhz is null && stack.Remove(121700), "确认未值守时没有主频率，解锁后可删除");
stack.SetLocked(null);
stack.Select(118000);
Check(stack.SelectedKhz == 118000 && stack.LockedKhz.Count == 0, "未完成值守检查时可任选主频率");
stack.SetVolume(118000, 150);
stack.SetMuted(118000, true);
Check(stack.Get(118000) is { Volume: 100, EffectiveVolume: 0 }, "音量钳在 0-100，静音时有效音量为 0");
stack.SetCurrentlyRx(118000, true, "CES2345");
stack.Add(121700);
stack.SetTx(118000, true);
stack.SetCurrentlyTx(true);
Check(stack.Get(118000) is { CurrentlyRx: true, LastReceivedCallsign: "CES2345", CurrentlyTx: true }
      && stack.Get(121700) is { CurrentlyTx: false }, "运行时收发状态：只点亮开了 TX 的电台");
stack.Load([new Radio(126750) { Rx = false }, new Radio(119100) { Rx = true }, new Radio(119100)]);
Check(stack.Count == 2 && stack.SelectedKhz == 119100, "恢复时去重，主频率优先选开着 RX 的");

var stations = new[]
{
    new StationViewModel("ZSSS_APP", "120.300") { TransmitAllowed = true, IsRx = true, IsTx = true },
    new StationViewModel("ZSPD_TWR", "118.000", isPrimary: true) { TransmitAllowed = true, IsRx = true, IsCrossCoupled = true },
    new StationViewModel("ZSPD_GND", "121.700") { TransmitAllowed = true, IsRx = true, IsCrossCoupled = true },
};
var plan = MainWindowViewModel.BuildSyncPlan(stations, allowed: true);
Check(plan.Rx.SequenceEqual([118000, 120300, 121700]) && plan.Tx.SequenceEqual([118000, 120300, 121700])
      && plan.Xc.SequenceEqual([118000, 121700]) && plan.Selected == 118000 && plan.Locked.SequenceEqual([118000])
      && plan.Volumes["120300"] == 100, "同步计划：按频率排序，主频率为本人席位");
var listenOnly = MainWindowViewModel.BuildSyncPlan(stations, allowed: false);
Check(listenOnly.Tx.Length == 0 && listenOnly.Xc.Length == 0 && listenOnly.Rx.Length == 3, "未上席位时计划里没有 TX / XC");
Check(JsonSerializer.Serialize(plan.Volumes).Contains("\"118000\":100"), "音量表的键是频率字符串，语音引擎按 int 解析");
Console.WriteLine("PASS: 电台栈联动、值守闸门、主频率选择及同步计划检查");

// —— 版本号（原 controller/version.py 的规则） ——
Check(AppVersion.BumpPatch("1.0.9") == "1.0.10" && AppVersion.BumpPatch("1.0") == "1.1", "补丁号按数值进位，不按字符串");
Check(AppVersion.Format("1.0.3", release: true, "1", null) == "1.0.3"
      && AppVersion.Format("1.0.3", release: true, "1", "71.667d9b4") == "v1.0.3 (build 71.667d9b4)", "正式包报它被打成的标签");
Check(AppVersion.Format("1.0.3", release: false, "2", null) == "1.0.4 (Dev Build 2)"
      && AppVersion.Format("1.0.3", release: false, "2", "dev") == "v1.0.4 (Dev Build 2; build dev)", "测试包报下一个补丁号并标出 Dev Build");
Check(AppVersion.ReleaseVersion.Split('.').Length == 3 && AppVersion.Build.Length > 0, "程序集里带着版本号和 build 号");
Console.WriteLine($"PASS: 版本号规则（当前 {AppVersion.Full}）");
