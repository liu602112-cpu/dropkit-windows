using System;
using System.Threading;

namespace DropKitWindows
{
    public sealed class LaunchBroker : IDisposable
    {
        readonly Mutex mutex;
        readonly EventWaitHandle openRequest;
        readonly Semaphore toggleRequest;
        public readonly bool IsPrimary;
        public LaunchBroker(string name)
        {
            // Create the mailbox first: a second launch during loading must survive
            // even before the primary process has created a window/message loop.
            openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Open");
            toggleRequest = new Semaphore(0, int.MaxValue, name + ".Toggle");
            mutex = new Mutex(false, name);
            try { IsPrimary = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsPrimary = true; }
        }
        public void RequestOpen()
        {
            openRequest.Set();
        }
        public bool ConsumeOpen() { return openRequest.WaitOne(0); }
        public void RequestToggle() { toggleRequest.Release(); }
        public bool ConsumeToggle() { return toggleRequest.WaitOne(0); }
        public void Dispose()
        {
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose(); openRequest.Dispose(); toggleRequest.Dispose();
        }
    }
}
