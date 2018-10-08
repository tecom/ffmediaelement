namespace Unosquare.FFME.Primitives
{
    using System;
    using System.Threading;

    /// <summary>
    /// A lightweight Threading Timer that implements advanced control with minimal locking.
    /// Uses ThreadPool threads to execute worker cycle logic.
    /// </summary>
    /// <seealso cref="IDisposable" />
    public abstract class TimerWorkerBase : IDisposable
    {
        private readonly object SyncLock = new object();
        private readonly ManualResetEvent CycledEvent = new ManualResetEvent(false);
        private readonly int CyclePeriodMs = 0;

        private Timer CycleTimer;
        private int m_IsRunningCycle;
        private int m_Interrupt;
        private int m_HasStarted;
        private long m_WorkerState = (long)ThreadState.Unstarted;
        private bool m_IsDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="TimerWorkerBase"/> class.
        /// </summary>
        /// <param name="workerName">Name of the worker.</param>
        /// <param name="period">The period.</param>
        protected TimerWorkerBase(string workerName, TimeSpan period)
        {
            WorkerName = workerName;
            CyclePeriod = period;
            CyclePeriodMs = (int)Math.Round(CyclePeriod.TotalMilliseconds, 0);
        }

        /// <summary>
        /// Gets the cycle period.
        /// </summary>
        public TimeSpan CyclePeriod { get; }

        /// <summary>
        /// Gets the state of the worker.
        /// </summary>
        public ThreadState WorkerState
        {
            get => (ThreadState)Interlocked.Read(ref m_WorkerState);
            private set => Interlocked.Exchange(ref m_WorkerState, (long)value);
        }

        /// <summary>
        /// Gets the worker identifier.
        /// </summary>
        public string WorkerName { get; }

        /// <summary>
        /// Gets a value indicating whether this instance is disposed.
        /// </summary>
        public bool IsDisposed
        {
            get { lock (SyncLock) return m_IsDisposed; }
            private set { lock (SyncLock) m_IsDisposed = value; }
        }

        /// <summary>
        /// Gets a value indicating whether an interrupt has been requested.
        /// </summary>
        protected bool IsInterruptRequested => Interlocked.CompareExchange(ref m_Interrupt, 0, 0) == 1;

        /// <summary>
        /// Gets a value indicating whether the worker is currently executing a cycle.
        /// </summary>
        protected bool IsRunningCycle => Interlocked.CompareExchange(ref m_IsRunningCycle, 0, 0) == 1;

        /// <summary>
        /// Gets or sets a value indicating whether the first timer event has been fired.
        /// </summary>
        private bool HasStarted
        {
            get => Interlocked.CompareExchange(ref m_HasStarted, 0, 0) == 1;
            set => Interlocked.Exchange(ref m_HasStarted, value ? 1 : 0);
        }

        /// <summary>
        /// Starts the worker.
        /// </summary>
        public void Start()
        {
            lock (SyncLock)
            {
                if (WorkerState != ThreadState.Unstarted) return;
                CycleTimer = new Timer(TimerCallback, this, 0, CyclePeriodMs);
                CycledEvent.WaitOne();
                WorkerState = ThreadState.Running;
            }
        }

        /// <summary>
        /// Stops the worker.
        /// </summary>
        public void Stop()
        {
            lock (SyncLock)
            {
                if (CycleTimer == null) return;
                Suspend();
                CycleTimer.Dispose();
                CycleTimer = null;
                WorkerState = ThreadState.Stopped;
                OnWorkerStopped();
            }
        }

        /// <summary>
        /// Suspends worker cycle execution.
        /// </summary>
        public void Suspend()
        {
            lock (SyncLock)
            {
                if (CycleTimer == null) return;
                WorkerState = ThreadState.SuspendRequested;
                Interlocked.Exchange(ref m_Interrupt, 1);
                CycledEvent.WaitOne();
                WorkerState = ThreadState.Suspended;
            }
        }

        /// <summary>
        /// Resumes worker cycle execution.
        /// </summary>
        public void Resume()
        {
            lock (SyncLock)
            {
                if (CycleTimer == null) return;
                Interlocked.Exchange(ref m_Interrupt, 0);
                CycledEvent.WaitOne();
                WorkerState = ThreadState.Running;
            }
        }

        /// <summary>
        /// Waits for the completion of a cycle.
        /// </summary>
        public void WaitOne()
        {
            if (IsDisposed) return;
            CycledEvent.WaitOne();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (SyncLock)
            {
                if (IsDisposed) return;
                Stop();
                WorkerState = ThreadState.Aborted;
                CycledEvent.Dispose();
                OnWorkerDisposed();
                IsDisposed = true;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"{nameof(TimerWorkerBase)}: {WorkerName} ({WorkerState})";

        /// <summary>
        /// Executes the worker cycle.
        /// Do not call worker control methods here.
        /// </summary>
        /// <param name="loop">if set to <c>true</c> executes the worker cycle without waiting for the timer to elapse.</param>
        protected abstract void ExecuteWorkerCycle(out bool loop);

        /// <summary>
        /// Called when a timer cycle completed.
        /// It is safe to call worker control methods here.
        /// </summary>
        protected virtual void OnWorkerCycled() { }

        /// <summary>
        /// Called the first time a timer event is fired.
        /// </summary>
        protected virtual void OnWorkerStarted() { }

        /// <summary>
        /// Called when the worker has stopped and no more timer events will fire.
        /// </summary>
        protected virtual void OnWorkerStopped() { }

        /// <summary>
        /// Called when worker is disposed. Include dispose logic here.
        /// </summary>
        protected virtual void OnWorkerDisposed() { }

        /// <summary>
        /// Begins the cycle.
        /// </summary>
        /// <returns><c>true</c> if the cycle entered.</returns>
        private bool BeginCycle() => Interlocked.CompareExchange(ref m_IsRunningCycle, 1, 0) == 0;

        /// <summary>
        /// Completes the cycle.
        /// </summary>
        private void CompleteCycle() => Interlocked.Exchange(ref m_IsRunningCycle, 0);

        /// <summary>
        /// Executes the logic when the timer event fires.
        /// </summary>
        /// <param name="state">The state.</param>
        private void TimerCallback(object state)
        {
            if (IsInterruptRequested || BeginCycle() == false)
            {
                CycleTimer.Change(1, CyclePeriodMs);
                return;
            }

            if (!HasStarted)
            {
                HasStarted = true;
                CycledEvent.Set();
                OnWorkerStarted();
            }

            try
            {
                CycledEvent.Reset();
                while (!IsInterruptRequested)
                {
                    ExecuteWorkerCycle(out var loop);
                    if (loop == false) break;
                }
            }
            finally
            {
                CycledEvent.Set();
                OnWorkerCycled();
                CompleteCycle();
            }
        }
    }
}
