namespace ControllerClient.Services.Ptt;

/// <summary>
/// 把键盘、鼠标侧键、摇杆三个源合成一个"现在按住了没有"：任意一条绑定按住就算按住。
/// onChange 只在状态翻转时调用，在监听线程上（钩子线程或摇杆轮询线程）。
/// 只启用真正绑到的源，三个源各自失败、互不影响。
/// </summary>
public sealed class PttWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<bool> _onChange;
    private readonly Dictionary<PttKind, HashSet<string>> _down = new()
    {
        [PttKind.Keyboard] = [], [PttKind.Mouse] = [], [PttKind.Joystick] = []
    };
    private List<PttBinding> _bindings;
    private bool _state;
    private IDisposable? _keyboard, _mouse;
    private IDisposable? _joystick;

    public PttWatcher(IEnumerable<PttBinding> bindings, Action<bool> onChange)
    {
        _bindings = [.. bindings];
        _onChange = onChange;
    }

    public bool IsRunning { get; private set; }
    public bool IsDown { get { lock (_gate) return _state; } }
    public IReadOnlyList<PttBinding> Bindings { get { lock (_gate) return [.. _bindings]; } }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        SyncSources();
        AppLog.Info($"PTT 监听已启动，{Bindings.Count} 条绑定");
    }

    /// <summary>停掉所有源并落下 PTT：停的时候正按着的话，语音那边会一直以为还按着，麦克风就一直开着。</summary>
    public void Stop()
    {
        var wasRunning = IsRunning;
        IsRunning = false;
        StopSources(keyboard: true, mouse: true, joystick: true);
        bool was;
        lock (_gate)
        {
            foreach (var pressed in _down.Values) pressed.Clear();
            was = _state;
            _state = false;
        }
        if (was) Notify(false);
        if (wasRunning) AppLog.Info("PTT 监听已停止");
    }

    /// <summary>换绑定后要重算：正按着的那条被删掉时得让 PTT 落下。</summary>
    public void SetBindings(IEnumerable<PttBinding> bindings)
    {
        lock (_gate) _bindings = [.. bindings];
        if (IsRunning) SyncSources();
        Recompute();
    }

    internal void Press(PttKind kind, string token)
    {
        lock (_gate)
            if (!_down[kind].Add(token)) return; // 键盘自动重复不算新的按下
        Recompute();
    }

    internal void Release(PttKind kind, string token)
    {
        lock (_gate) _down[kind].Remove(token);
        Recompute();
    }

    /// <summary>一轮摇杆读数。按钮号超出设备范围（换了按钮更少的手柄）就当没按，不能让轮询带着异常死掉。</summary>
    internal void UpdateJoysticks(IReadOnlyList<JoystickState> states)
    {
        lock (_gate)
        {
            var pressed = _down[PttKind.Joystick];
            pressed.Clear();
            foreach (var binding in _bindings.Where(b => b.Kind == PttKind.Joystick))
                if (states.FirstOrDefault(s => s.Index == binding.Device) is { } stick &&
                    binding.Button < stick.Buttons.Length && stick.Buttons[binding.Button])
                    pressed.Add(binding.InputToken);
        }
        Recompute();
    }

    private void Recompute()
    {
        bool state;
        lock (_gate)
        {
            state = _bindings.Any(b => _down[b.Kind].Contains(b.InputToken));
            if (state == _state) return;
            _state = state;
        }
        // 回调放在锁外：界面那边可能回头调 SetBindings，在锁里回调就是死锁
        Notify(state);
    }

    /// <summary>回调是别人的代码，它抛异常时这里必须继续，否则 Stop 断在半路、麦克风留在开着的状态。</summary>
    private void Notify(bool state)
    {
        try { _onChange(state); }
        catch (Exception ex) { AppLog.Write("client", "WARN", "PTT 回调异常：" + ex.Message); }
    }

    private void SyncSources()
    {
        HashSet<PttKind> kinds;
        lock (_gate) kinds = [.. _bindings.Select(b => b.Kind)];
        StopSources(!kinds.Contains(PttKind.Keyboard), !kinds.Contains(PttKind.Mouse), !kinds.Contains(PttKind.Joystick));
        if (kinds.Contains(PttKind.Keyboard))
            _keyboard ??= InputHooks.Keyboard((pressed, key) =>
            {
                if (pressed) Press(PttKind.Keyboard, key);
                else Release(PttKind.Keyboard, key);
            });
        if (kinds.Contains(PttKind.Mouse))
            _mouse ??= InputHooks.Mouse((pressed, button) =>
            {
                if (pressed) Press(PttKind.Mouse, button);
                else Release(PttKind.Mouse, button);
            });
        if (kinds.Contains(PttKind.Joystick))
            _joystick ??= Joysticks.Subscribe(
                () => Bindings.Where(b => b.Kind == PttKind.Joystick).Select(b => b.Device).ToHashSet(),
                UpdateJoysticks);
    }

    private void StopSources(bool keyboard, bool mouse, bool joystick)
    {
        if (keyboard) { _keyboard?.Dispose(); _keyboard = null; }
        if (mouse) { _mouse?.Dispose(); _mouse = null; }
        if (joystick)
        {
            _joystick?.Dispose();
            _joystick = null;
            lock (_gate) _down[PttKind.Joystick].Clear();
        }
    }

    public void Dispose() => Stop();
}
