using System;
using System.Threading;

namespace AliCloudDynamicDNS.Threading
{
    /// <summary>
    /// 基于 <see cref="Timer"/> 实现的健壮计时器。
    /// </summary>
    public class StrongTimer
    {
        /// <summary>
        /// 计时器的执行周期，单位是毫秒。
        /// </summary>
        public int Period { get; set; }

        /// <summary>
        /// 调用 Start 之后就开始运行。
        /// </summary>
        public bool RunOnStart { get; set; }

        /// <summary>
        /// 需要执行的事件。
        /// </summary>
        public event EventHandler Elapsed;

        private readonly Timer _taskTimer;
        private volatile bool _performingTasks;
        private volatile bool _isRunning;

        public StrongTimer()
        {
            _taskTimer = new Timer(Callback, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (Period <= 0)
            {
                return;
            }

            lock (_taskTimer)
            {
                _taskTimer.Change(RunOnStart ? 0 : Period, Timeout.Infinite);
                _isRunning = true;
            }
        }

        public void Stop()
        {
            lock (_taskTimer)
            {
                _taskTimer.Change(Timeout.Infinite, Timeout.Infinite);
                while (_performingTasks)
                {
                    Monitor.Wait(_taskTimer);
                }

                _isRunning = false;
            }
        }

        private void Callback(object? state)
        {
            lock (_taskTimer)
            {
                if (!_isRunning || _performingTasks)
                {
                    return;
                }

                _taskTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _performingTasks = true;
            }

            try
            {
                Elapsed?.Invoke(this, new EventArgs());
            }
            catch
            {

            }
            finally
            {
                lock (_taskTimer)
                {
                    _performingTasks = false;
                    if (_isRunning)
                    {
                        _taskTimer.Change(Period, Timeout.Infinite);
                    }
                    
                    Monitor.Pulse(_taskTimer);
                }
            }
        }
    }
}