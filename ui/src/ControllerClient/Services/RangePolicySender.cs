namespace ControllerClient.Services;

/// <summary>
/// 把距离静音快照送给语音引擎，**不让调用方等回执**。
///
/// 原来数据源刷新流程里直接 await range_audio 的回复，而引擎按顺序处理命令（connect 会阻塞到连上
/// 为止），回执最长要等到 90 秒的命令超时——这段时间 _refreshing 一直占着，席位、值守和下一份
/// 快照全部停住。现在 <see cref="Submit"/> 立即返回；正在发的时候来的新快照只留最新一份，
/// 发完一条再发它，中间那些反正会被整份替换的快照直接丢掉。
/// </summary>
public sealed class RangePolicySender(Func<IReadOnlyDictionary<string, bool>, Task> send)
{
    private readonly object _gate = new();
    private Dictionary<string, bool>? _latest;
    private bool _dirty;
    private bool _running;
    private Task _pump = Task.CompletedTask;

    /// <summary>最近一次要求送出的快照；断开后为 null。</summary>
    public Dictionary<string, bool>? Latest { get { lock (_gate) return _latest; } }

    /// <summary>上一次发送失败了（只记一次日志，成功后复位）。</summary>
    public bool Failing { get; private set; }

    /// <summary>当前这一轮发送全部结束（测试用）。</summary>
    public Task Idle { get { lock (_gate) return _pump; } }

    public void Submit(Dictionary<string, bool> policy)
    {
        lock (_gate)
        {
            _latest = policy;
            _dirty = true;
            if (_running) return;   // 正在发的那一轮会接着发这份
            _running = true;
            _pump = PumpAsync();
        }
    }

    /// <summary>重连后再推一次最近的快照。</summary>
    public void Resend()
    {
        if (Latest is { } policy) Submit(policy);
    }

    /// <summary>断开时清空：还没发出去的那份也不要了。</summary>
    public void Clear()
    {
        lock (_gate) { _latest = null; _dirty = false; }
    }

    private async Task PumpAsync()
    {
        await Task.Yield();         // Submit 永远不在调用方线程上等发送
        while (true)
        {
            Dictionary<string, bool> policy;
            lock (_gate)
            {
                if (!_dirty || _latest is null) { _running = false; return; }
                _dirty = false;
                policy = _latest;
            }
            try
            {
                await send(policy);
                if (Failing) AppLog.Info("距离静音策略已送达语音引擎");
                Failing = false;
            }
            catch (Exception ex)
            {
                // 引擎不认这条命令、或者正忙着别的超时了：只记一次，下次刷新会再交一份
                if (!Failing) AppLog.Error("距离静音策略发送失败，下次刷新时重试", ex);
                Failing = true;
            }
        }
    }
}
