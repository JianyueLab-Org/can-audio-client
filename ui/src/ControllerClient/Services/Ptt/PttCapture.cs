namespace ControllerClient.Services.Ptt;

/// <summary>
/// 录一条绑定：三个源一起听，谁先动就是谁，只回调一次，回调后自己停。
/// 调用方必须先停掉 <see cref="PttWatcher"/>，否则为录绑定按下的那一下会真的发出去一段语音。
/// </summary>
public sealed class PttCapture : IDisposable
{
    private readonly Action<PttBinding> _onCaptured;
    // 录制开始时就按着的摇杆按钮不算数（用户可能一直压着油门上的某个开关），只在轮询线程上访问
    private readonly Dictionary<int, HashSet<int>> _held = [];
    private int _done;
    private LowLevelHook? _keyboard, _mouse;
    private IDisposable? _joystick;

    public PttCapture(Action<PttBinding> onCaptured) => _onCaptured = onCaptured;

    public void Start()
    {
        _keyboard = LowLevelHook.Keyboard((pressed, key) => { if (pressed) Fire(PttBinding.Keyboard(key)); });
        _mouse = LowLevelHook.Mouse((pressed, button) => { if (pressed) Fire(PttBinding.Mouse(button)); });
        _joystick = Joysticks.Subscribe(() => null, OnJoysticks);
    }

    internal void OnJoysticks(IReadOnlyList<JoystickState> states)
    {
        foreach (var stick in states)
        {
            if (!_held.TryGetValue(stick.Index, out var held))
            {
                _held[stick.Index] = [.. Enumerable.Range(0, stick.Buttons.Length).Where(i => stick.Buttons[i])];
                continue;
            }
            for (var button = 0; button < stick.Buttons.Length; button++)
            {
                if (!stick.Buttons[button]) held.Remove(button);
                else if (!held.Contains(button))
                {
                    Fire(PttBinding.Joystick(button, stick.Index, stick.Name));
                    return;
                }
            }
        }
    }

    internal void Fire(PttBinding binding)
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        AppLog.Info($"录到 PTT 绑定：{binding}");
        try { _onCaptured(binding); }
        catch (Exception ex) { AppLog.Write("client", "WARN", "PTT 录制回调异常：" + ex.Message); }
        // 不在钩子回调里等别的线程收尾：钩子回调被卡住时整个系统的键盘输入都跟着卡住
        ThreadPool.QueueUserWorkItem(_ => Stop());
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _done, 1);
        Interlocked.Exchange(ref _keyboard, null)?.Dispose();
        Interlocked.Exchange(ref _mouse, null)?.Dispose();
        Interlocked.Exchange(ref _joystick, null)?.Dispose();
    }

    public void Dispose() => Stop();
}
