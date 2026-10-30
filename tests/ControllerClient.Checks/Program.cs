using System.Text.Json;
using ControllerClient.ViewModels;
using ControllerClient.Services;
using ControllerClient.Services.Ptt;

// 作为 VoiceBridge 的替身子进程运行，模拟不读取 stdin 的引擎。
if (args is ["-u", var fakeScript] && File.Exists(fakeScript)
    && File.ReadAllText(fakeScript) == "ControllerClient.Checks stalled engine")
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static JsonElement Binding(object data) => JsonSerializer.SerializeToElement(data);

var debugDirectory = Path.Combine(Path.GetTempPath(), "can-debug-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(debugDirectory);
try
{
    var debugPath = Path.Combine(debugDirectory, DebugOptions.FileName);
    Check(!DebugOptions.LoadShowServerEndpoints(debugPath), "调试文件缺失时必须隐藏手动端点");
    foreach (var json in new[] { "{}", "null", "[]", "{", "{\"show_server_endpoints\":false}", "{\"show_server_endpoints\":\"true\"}", "{\"show_server_endpoints\":1}" })
    {
        File.WriteAllText(debugPath, json);
        Check(!DebugOptions.LoadShowServerEndpoints(debugPath), "无效配置或非布尔 true 不能开启手动端点");
    }
    File.WriteAllText(debugPath, "{\"show_server_endpoints\":true}");
    Check(DebugOptions.LoadShowServerEndpoints(debugPath), "独立配置明确为 true 时应显示手动端点");
    var serverSettings = new SettingsViewModel();
    Check(serverSettings.SelectedServer is { } selected && serverSettings.ServerChoices.Contains(selected), "当前服务器必须在服务器选择列表中");
    if (!serverSettings.ShowServerEndpoints)
        Check(SettingsViewModel.IsOfficialServer(serverSettings.ServerHost)
              && !serverSettings.ServerChoices.Contains(SettingsViewModel.CustomServer),
              "未开启调试时只能使用 CAN 的服务器，不加载已保存的自定义地址");
}
finally { Directory.Delete(debugDirectory, recursive: true); }
Console.WriteLine("PASS: 独立调试配置默认关闭、严格布尔开关、无效文件及服务器选择检查");

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

// 服务器目录：照抄 can-api /api/v1/servers 的实际返回
var directory = ServerDirectory.Parse("""
{"data":{"audio":[{"host":"audio.ceruleanavi.net","kind":"audio","location":"Tokyo, Japan","name":"Audio","port":64738},
{"host":"hz.cn.audio.ceruleanavi.net","kind":"audio","location":"Hangzhou, China","name":"CAN - HZ","port":64738},
{"host":"hz.cn.audio.ceruleanavi.net","kind":"audio","name":"重复","port":64738},
{"host":"bad host","kind":"audio","name":"坏地址","port":64738},
{"host":"fsd.example","kind":"fsd","name":"FSD","port":6809}],
"fsd":[{"host":"fsd.ceruleanavi.net","kind":"fsd","name":"FSD","port":6809}]},"status":200}
""");
Check(directory.Count == 2 && directory[1].Host == "hz.cn.audio.ceruleanavi.net", "目录应收下杭州语音服务器，并丢掉重复和坏条目");
var picker = new SettingsViewModel { ServerHost = "audio.ceruleanavi.net", ServerPort = 64738 };
picker.LoadServers(directory);
// 开了调试端点时多一项“自定义”
var customCount = picker.ShowServerEndpoints ? 1 : 0;
Check(picker.ServerChoices.Count == 2 + customCount && picker.SelectedServer?.Server?.Name == "Audio", "加载列表后应选中当前地址对应的服务器");
picker.SelectedServer = picker.ServerChoices.First(c => c.Server?.Location == "Hangzhou, China");
Check(picker.ServerHost == "hz.cn.audio.ceruleanavi.net" && picker.ServerPort == 64738 && picker.IsDirty, "选杭州应填入杭州地址并允许应用");
var offline = new SettingsViewModel { ServerHost = "hz.cn.audio.ceruleanavi.net", ServerPort = 64738 };
offline.LoadServers([]);
Check(offline.SelectedServer?.Server?.Host == "hz.cn.audio.ceruleanavi.net", "列表没拿到时仍应显示已选的 CAN 服务器");
if (picker.ShowServerEndpoints)
{
    picker.ServerHost = "voice.intranet.example";
    Check(picker.SelectedServer == SettingsViewModel.CustomServer, "手改地址应回到自定义");
    picker.SelectedServer = picker.ServerChoices[1];
    picker.SelectedServer = SettingsViewModel.CustomServer;
    Check(picker.ServerHost == "voice.intranet.example", "切回自定义应恢复手填的地址");
}
else
    Check(!picker.ServerChoices.Contains(SettingsViewModel.CustomServer), "未开调试端点时不能选自定义");
Console.WriteLine("PASS: 服务器目录解析与杭州服务器选择");

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
Check(ConnectionStatusViewModel.IsTerminalVoiceState("offline")
      && ConnectionStatusViewModel.IsTerminalVoiceState("error")
      && ConnectionStatusViewModel.IsTerminalVoiceState("stopped")
      && !ConnectionStatusViewModel.IsTerminalVoiceState("reconnecting")
      && !ConnectionStatusViewModel.IsTerminalVoiceState("online"),
      "重连耗尽的 offline 必须清理会话和距离策略，临时重连仍保留会话");
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
// 同一份设置在三个系统上指同一个键：evdev 47 / macOS 9 是 V，右 Ctrl、左 Shift、F1 与 Windows 同名
Check(PttKeys.FromEvdev(47) == "v" && PttKeys.FromEvdev(97) == "ctrl_r" && PttKeys.FromEvdev(42) == "shift"
      && PttKeys.FromEvdev(59) == "f1" && PttKeys.FromEvdev(57) == "space" && PttKeys.FromEvdev(11) == "0"
      && PttKeys.FromEvdev(41) == "`" && PttKeys.FromEvdev(53) == "/" && PttKeys.FromEvdev(194) == "f24", "Linux 键名与 Windows 一致");
Check(PttKeys.FromMac(9) == "v" && PttKeys.FromMac(62) == "ctrl_r" && PttKeys.FromMac(56) == "shift"
      && PttKeys.FromMac(122) == "f1" && PttKeys.FromMac(49) == "space" && PttKeys.FromMac(29) == "0"
      && PttKeys.FromMac(50) == "`" && PttKeys.FromMac(47) == "." && PttKeys.FromMac(92) == "9", "macOS 键名与 Windows 一致");
Check(PttKeys.FromEvdev(240) == "evdev240" && PttKeys.FromMac(10) == "mac10", "无名字的键退回系统键码");
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

// —— 距离静音（range_audio 快照） ——
static NetworkPeer Pilot(string cid, double lat, double lon) => new(cid, "CCA" + cid, false, RangeAudio.Observer, lat, lon);
static NetworkPeer Atc(string cid, int facility, double lat, double lon) => new(cid, cid + "_ATC", true, facility, lat, lon);
const double ZbaaLat = 40.07333, ZbaaLon = 116.59833;
// 正北方向 1 海里 = 1/60 度纬度
static double North(double nm) => ZbaaLat + nm / 60;

var feed = JsonDocument.Parse("""
    {"pilots":[{"cid":"1003","callsign":"CCA1658","latitude":40.07724,"longitude":116.61107}],
     "controllers":[{"cid":"10047","callsign":"ZBAA_TWR","facility":4,"latitude":"40.07333","longitude":"116.59833"},
                    {"cid":"5158","callsign":"LAX_25_CTR","facility":6,"latitude":"33.9425","longitude":"-118.408074"}]}
    """).RootElement;
var feedPeers = feed.GetProperty("pilots").EnumerateArray().Select(e => NetworkDirectory.ParsePeer(e, e.GetProperty("cid").GetString()!, "", false))
    .Concat(feed.GetProperty("controllers").EnumerateArray().Select(e => NetworkDirectory.ParsePeer(e, e.GetProperty("cid").GetString()!, "", true))).ToList();
Check(feedPeers.All(p => p.HasPosition) && feedPeers.Single(p => p.Cid == "10047").Facility == RangeAudio.Tower,
      "数据源里飞行员的数字坐标和管制员的字符串坐标都能读出来");
var live = RangeAudio.BuildPolicy("10047", feedPeers);
Check(live.Count == 2 && live["1003"] && live["5158"] && !live.ContainsKey("10047"),
      "实测数据：本场飞行员放行，万里之外的 LAX CTR 也放行（管制员之间不设限），快照里不含自己");

var d = RangeAudio.DistanceNm(40.08, 116.58, 31.14, 121.80);
Check(d is > 560 and < 600, $"ZBAA 到 ZSPD 约 580 海里，算出 {d:F0}");
Check(RangeAudio.RangeNm(false, 0) == 200 && RangeAudio.RangeNm(true, RangeAudio.Delivery) == 100
      && RangeAudio.RangeNm(true, RangeAudio.Tower) == 100 && RangeAudio.RangeNm(true, RangeAudio.Approach) == 300
      && RangeAudio.RangeNm(true, RangeAudio.Center) == 600 && RangeAudio.RangeNm(true, RangeAudio.Fss) == 2000,
      "收听范围：飞行员 200，TWR 及以下 100，APP 300，CTR 600，FSS 2000 海里");

var near = Pilot("2001", North(90), ZbaaLon);
var far = Pilot("2002", North(150), ZbaaLon);
var tower = RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon), near, far]);
Check(tower["2001"] && !tower["2002"], "TWR：90 海里的飞行员放行，150 海里的静音——同一频率上的两个人可以不同");
var center = RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Center, ZbaaLat, ZbaaLon), near, far, Pilot("2003", North(700), ZbaaLon)]);
Check(center["2001"] && center["2002"] && !center["2003"], "CTR：150 海里放行，700 海里静音");
var both = RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon), Atc("1", RangeAudio.Approach, ZbaaLat, ZbaaLon), far]);
Check(both["2002"], "同一 CID 同时开 TWR 和 APP：按范围大的那个席位算");
var farAtc = RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon), Atc("3001", RangeAudio.Ground, North(1500), ZbaaLon)]);
Check(farAtc["3001"], "管制员之间不设限：1500 海里外的 GND 也放行");
Check(RangeAudio.BuildPolicy("1", [far]).Count == 0, "自己不在数据源里（没坐标）时快照为空，全部默认放行");
var unknown = RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon), new NetworkPeer("2004", "CCA2004", false, 0, null, null)]);
Check(!unknown.ContainsKey("2004"), "没坐标的飞行员不写进快照，交给引擎默认放行，不会被误静音");
var zero = NetworkDirectory.ParsePeer(JsonDocument.Parse("""{"latitude":0,"longitude":0}""").RootElement, "2005", "", false);
Check(!zero.HasPosition, "(0, 0) 当作没有坐标");
foreach (var invalid in new[] { "NaN", "Infinity", "-Infinity", "1e999" })
{
    using var invalidDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { latitude = invalid, longitude = "116.6" }));
    var invalidPeer = NetworkDirectory.ParsePeer(invalidDoc.RootElement, "2006", "", false);
    Check(!invalidPeer.HasPosition, $"非有限坐标 {invalid} 当作没有坐标");
    Check(!RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon), invalidPeer]).ContainsKey("2006"),
          "非法坐标的飞行员默认放行");
    Check(RangeAudio.BuildPolicy("2006", [invalidPeer, near]).Count == 0, "本人坐标非法时不误静音其他人");
}
Check(!Pilot("2006", double.NaN, ZbaaLon).HasPosition, "直接构造的非有限坐标也不能参与距离计算");
var unlocatedAtc = new NetworkPeer("1", "ZBAA_TWR", true, RangeAudio.Tower, null, null);
Check(RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon),
      unlocatedAtc with { Callsign = "ZBAA_APP", Facility = RangeAudio.Approach }, far]).Count == 0,
      "部分本人管制席位缺坐标时不能仅按已知席位拒绝，未知席位默认放行");
Check(RangeAudio.BuildPolicy("1", [unlocatedAtc, Pilot("1", ZbaaLat, ZbaaLon), far,
      Atc("3001", RangeAudio.Center, 33.94, -118.4)]).Count == 0,
      "本人管制员缺坐标时默认放行，不能退回飞行员身份静音远处管制员");
var mixedIdentity = RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon),
    Pilot("1", North(150), ZbaaLon), far]);
Check(!mixedIdentity["2002"], "本人有管制员坐标时不借用飞行员位置扩大收听范围");
Check(RangeAudio.SameAs(tower, new Dictionary<string, bool>(tower)) && !RangeAudio.SameAs(tower, center), "快照比较按键和值");
Check(JsonSerializer.Serialize(tower) == """{"2001":true,"2002":false}""", "发给引擎的是 CID → bool 的 JSON 对象");
Console.WriteLine("PASS: 距离静音范围规则、坐标解析与快照内容检查");

// —— 快照过期、坐标新旧、暂停原因、只发静音项 ——
Check(RangeAudio.ToWire(new Dictionary<string, bool> { ["a"] = true, ["b"] = false }) is { Count: 1 } wire && !wire["b"],
      "发给引擎的只有要静音的 CID：没列出的默认放行，大小只跟静音人数有关");
var fed = new DateTime(2026, 10, 24, 0, 0, 0);
Check(!RangeAudio.PolicyExpired(fed, fed.AddSeconds(119)) && RangeAudio.PolicyExpired(fed, fed.AddSeconds(121)),
      "数据源连续读不到 2 分钟以上，快照过期");
static NetworkPeer Aged(string json, bool controller) =>
    NetworkDirectory.ParsePeer(JsonDocument.Parse(json).RootElement, "9", "", controller, 1000);
Check(Aged("""{"latitude":40,"longitude":116,"position_time":970}""", false).HasPosition, "30 秒前的飞行员坐标可用");
Check(!Aged("""{"latitude":40,"longitude":116,"position_time":900}""", false).HasPosition, "100 秒前的飞行员坐标当作没有");
Check(!Aged("""{"latitude":40,"longitude":116,"send_time":999}""", false).HasPosition, "飞行员只认 position_time，send_time 不算");
Check(Aged("""{"latitude":"40","longitude":"116","send_time":980}""", true).HasPosition
      && !Aged("""{"latitude":"40","longitude":"116","send_time":800}""", true).HasPosition, "管制员按 send_time 判新旧");
Check(!Aged("""{"latitude":"40","longitude":"116","send_time":"NaN"}""", true).HasPosition, "时间是 NaN 也当作没有坐标");
RangeAudio.BuildPolicy("1", [far], out var notListed);
RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, double.NaN, ZbaaLon), far], out var noPosition);
RangeAudio.BuildPolicy("1", [Atc("1", RangeAudio.Tower, ZbaaLat, ZbaaLon), far], out var judged);
Check(notListed is not null && noPosition is not null && notListed != noPosition && judged is null,
      "判断不了时给出原因（不在数据源 / 位置未知），能判断时为 null");

var quietSender = new RangePolicySender(_ => Task.FromException(new ObjectDisposedException("VoiceBridge")));
quietSender.Submit(new() { ["x"] = false });
await quietSender.Idle.WaitAsync(TimeSpan.FromSeconds(5));
Check(!quietSender.Failing, "程序关闭时引擎已收摊，不算发送失败");
Console.WriteLine("PASS: 快照过期、坐标新旧、暂停原因、只发静音项与关闭时不报错检查");

// 用未完成的回执模拟引擎卡住，验证提交不等待、合并积压和断开清理。
var rangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var rangeReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var rangeSent = new List<Dictionary<string, bool>>();
var rangeSender = new RangePolicySender(async policy =>
{
    rangeSent.Add(new(policy));
    if (rangeSent.Count == 1)
    {
        rangeStarted.SetResult();
        await rangeReply.Task;
    }
});
rangeSender.Submit(new() { ["first"] = true });
await rangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(!rangeSender.Idle.IsCompleted, "回执未到时发送仍在进行，但 Submit 已返回");
rangeSender.Submit(new() { ["intermediate"] = true });
rangeSender.Submit(new() { ["latest"] = false });
rangeReply.SetResult();
await rangeSender.Idle.WaitAsync(TimeSpan.FromSeconds(5));
Check(rangeSent.Count == 2 && rangeSent[1].ContainsKey("latest"), "引擎恢复后只补发最新快照");
rangeSender.Resend();
await rangeSender.Idle.WaitAsync(TimeSpan.FromSeconds(5));
Check(rangeSent.Count == 3 && rangeSent[2].ContainsKey("latest"), "重连重发最新快照");

var clearStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var clearReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var clearSendCount = 0;
var clearSender = new RangePolicySender(async _ =>
{
    clearSendCount++;
    clearStarted.TrySetResult();
    await clearReply.Task;
});
clearSender.Submit(new() { ["first"] = true });
await clearStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
clearSender.Submit(new() { ["pending"] = false });
clearSender.Clear();
clearReply.SetResult();
await clearSender.Idle.WaitAsync(TimeSpan.FromSeconds(5));
clearSender.Resend();
Check(clearSendCount == 1 && clearSender.Latest is null, "断开丢弃积压快照，重连前不再重发");

var retryCount = 0;
var retrySender = new RangePolicySender(_ => ++retryCount == 1
    ? Task.FromException(new TimeoutException("模拟引擎回执超时")) : Task.CompletedTask);
retrySender.Submit(new() { ["first"] = true });
await retrySender.Idle.WaitAsync(TimeSpan.FromSeconds(5));
Check(retrySender.Failing, "发送超时记录失败但不会卡住发送器");
retrySender.Submit(new() { ["next"] = true });
await retrySender.Idle.WaitAsync(TimeSpan.FromSeconds(5));
Check(retryCount == 2 && !retrySender.Failing, "下一次刷新可重试并恢复失败状态");
Console.WriteLine("PASS: 距离策略非阻塞发送、最新快照合并、重连重发、断开清理及超时恢复检查");

var fakeRepo = Path.Combine(Path.GetTempPath(), "can-bridge-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(fakeRepo, "controller"));
File.WriteAllText(Path.Combine(fakeRepo, "controller", "bridge.py"), "ControllerClient.Checks stalled engine");
var oldAudioDir = Environment.GetEnvironmentVariable("CAN_AUDIO_DIR");
var oldPython = Environment.GetEnvironmentVariable("CAN_PYTHON");
Environment.SetEnvironmentVariable("CAN_AUDIO_DIR", fakeRepo);
Environment.SetEnvironmentVariable("CAN_PYTHON", Environment.ProcessPath!);
try
{
    var largePolicy = Enumerable.Range(1, 10000).ToDictionary(i => i.ToString(), _ => false);
    var blockedBridge = new VoiceBridge();
    var blocked = blockedBridge.SendAsync("range_audio", largePolicy, logFailure: false, timeout: TimeSpan.FromMilliseconds(300));
    try { await blocked.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("管道背压必须超时"); }
    catch (TimeoutException) { Check(blocked.IsCompleted, "超时必须覆盖管道写入，而不是只覆盖回执"); }
    await blockedBridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    var closingBridge = new VoiceBridge();
    var writing = closingBridge.SendAsync("range_audio", largePolicy, logFailure: false);
    var queuedPtt = closingBridge.SendAsync("ptt", new { active = false }, logFailure: false);
    await closingBridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    foreach (var task in new[] { writing, queuedPtt })
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        Check(task.IsCompleted, "关闭必须结束正在写入和等待写锁的命令");
    }
    try { await closingBridge.StartAsync(); throw new Exception("关闭后不能重启引擎"); }
    catch (ObjectDisposedException) { }
    Console.WriteLine("PASS: 真实子进程管道背压超时、在途写入关闭、排队 PTT 取消和关闭后禁止重启检查");

    // 距离策略的写入不设超时：引擎暂时不读 stdin 时只能等，绝不能把引擎结束掉
    var patientBridge = new VoiceBridge();
    var patient = patientBridge.SendAsync("range_audio", largePolicy, logFailure: false,
        timeout: TimeSpan.FromMilliseconds(300), boundWrite: false);
    await Task.Delay(TimeSpan.FromSeconds(1.5));
    Check(!patient.IsCompleted, "写不进去时距离策略等着，不会因为 300 ms 的回执超时把引擎结束掉");
    await patientBridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    try { await patient.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    Check(patient.IsCompleted, "关闭时仍然能结束这条在途写入");
    Console.WriteLine("PASS: 距离策略写入不受回执超时影响、不结束引擎检查");
}
finally
{
    Environment.SetEnvironmentVariable("CAN_AUDIO_DIR", oldAudioDir);
    Environment.SetEnvironmentVariable("CAN_PYTHON", oldPython);
    Directory.Delete(fakeRepo, recursive: true);
}

// —— 版本号（原 controller/version.py 的规则） ——
Check(AppVersion.BumpPatch("1.0.9") == "1.0.10" && AppVersion.BumpPatch("1.0") == "1.1", "补丁号按数值进位，不按字符串");
Check(AppVersion.Format("1.0.3", release: true, "1", null) == "1.0.3"
      && AppVersion.Format("1.0.3", release: true, "1", "71.667d9b4") == "v1.0.3 (build 71.667d9b4)", "正式包报它被打成的标签");
Check(AppVersion.Format("1.0.3", release: false, "2", null) == "1.0.4 (Dev Build 2)"
      && AppVersion.Format("1.0.3", release: false, "2", "dev") == "v1.0.4 (Dev Build 2; build dev)", "测试包报下一个补丁号并标出 Dev Build");
Check(AppVersion.ReleaseVersion.Split('.').Length == 3 && AppVersion.Build.Length > 0, "程序集里带着版本号和 build 号");
Console.WriteLine($"PASS: 版本号规则（当前 {AppVersion.Full}）");

// —— 音频设备：同名设备不能落到 WDM-KS 上（-9999 Unanticipated host error） ——
// 旧引擎的样子：同一个耳机按 MME、DirectSound、WASAPI、WDM-KS 各出现一次，名字相同，MME 排在最前。
var oldEngine = AudioDevices.Parse(JsonSerializer.SerializeToElement(new object[]
{
    new { index = 1, name = "麦克风 (HyperX Cloud III Wireless)", input = true, output = false },
    new { index = 3, name = "耳机 (HyperX Cloud III Wireless)", input = false, output = true },
    new { index = 4, name = "数字输出 (High Definition Audio Dev", input = false, output = true },
    new { index = 8, name = "麦克风 (HyperX Cloud III Wireless)", input = true, output = false },
    new { index = 11, name = "数字输出 (High Definition Audio Device)", input = false, output = true },
    new { index = 18, name = "麦克风 (HyperX Cloud III Wireless)", input = true, output = false },
    new { index = 22, name = "耳机 (HyperX Cloud III Wireless)", input = false, output = true },
    new { index = 23, name = "麦克风 (HyperX Cloud III Wireless)", input = true, output = false },
}));
Check(oldEngine.Input("麦克风 (HyperX Cloud III Wireless)") == 1 && oldEngine.Output("耳机 (HyperX Cloud III Wireless)") == 3,
      "同名设备取排在最前的 MME，不能被后面的 WDM-KS 覆盖");
Check(oldEngine.InputNames.Count == 1 && oldEngine.OutputNames.Count == 3, "下拉框里同名设备只出现一次");
Check(oldEngine.Input("系统默认") is null && oldEngine.Input(null) is null && oldEngine.Output("已拔掉的耳机") is null,
      "系统默认或找不到的设备交给引擎用默认设备");
var mme = AudioDevices.Parse(JsonSerializer.SerializeToElement(new object[]
{
    new { index = 4, name = "数字输出 (High Definition Audio Dev", input = false, output = true },
    new { index = 5, name = "Speakers", input = false, output = true },
}));
Check(mme.Output("数字输出 (High Definition Audio Device)") == 4
      && mme.OutputName("数字输出 (High Definition Audio Device)") == "数字输出 (High Definition Audio Dev",
      "旧版本存的完整名字能对上 MME 截到 31 个字符的名字");
Check(mme.Output("Speakers (USB Audio)") is null, "没被截断的短名字不按前缀匹配");
Console.WriteLine("PASS: 音频设备按名字回查 index");
