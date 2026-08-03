namespace AppStudio
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;

    public sealed class WinEventRecord
    {
        public long Sequence;
        public DateTimeOffset At;
        public uint EventId;
        public string Type;
        public long Hwnd;
        public int ObjectId;
        public int ChildId;
        public int ThreadId;
        public int ProcessId;
    }

    public sealed class WinEventMonitor : IDisposable
    {
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MENUSTART = 0x0004;
        private const uint EVENT_SYSTEM_MENUEND = 0x0005;
        private const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
        private const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0007;
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_HIDE = 0x8003;
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint EVENT_OBJECT_SELECTION = 0x8006;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
        private readonly ConcurrentQueue<WinEventRecord> queue = new ConcurrentQueue<WinEventRecord>();
        private readonly WinEventDelegate callback;
        private readonly List<IntPtr> hooks = new List<IntPtr>();
        private long sequence;
        private int processId;

        public WinEventMonitor()
        {
            callback = OnWinEvent;
        }

        public void Start(int targetProcessId)
        {
            Stop();
            processId = targetProcessId;
            AddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MENUPOPUPEND);
            AddHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_VALUECHANGE);
        }

        public void Stop()
        {
            for (int index = 0; index < hooks.Count; index++) UnhookWinEvent(hooks[index]);
            hooks.Clear();
        }

        public WinEventRecord[] Drain()
        {
            List<WinEventRecord> items = new List<WinEventRecord>();
            WinEventRecord item;
            while (queue.TryDequeue(out item)) items.Add(item);
            return items.ToArray();
        }

        public void Dispose()
        {
            Stop();
        }

        private void AddHook(uint minimum, uint maximum)
        {
            IntPtr hook = SetWinEventHook(minimum, maximum, IntPtr.Zero, callback, unchecked((uint)Math.Max(0, processId)), 0, WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("SetWinEventHook failed for range 0x" + minimum.ToString("X") + "-0x" + maximum.ToString("X") + ".");
            hooks.Add(hook);
        }

        private void OnWinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
        {
            uint eventProcess;
            NativeMethods.GetWindowThreadProcessId(window, out eventProcess);
            if (processId != 0 && eventProcess != unchecked((uint)processId)) return;
            WinEventRecord item = new WinEventRecord();
            item.Sequence = Interlocked.Increment(ref sequence);
            item.At = DateTimeOffset.Now;
            item.EventId = eventId;
            item.Type = TypeName(eventId);
            item.Hwnd = window.ToInt64();
            item.ObjectId = objectId;
            item.ChildId = childId;
            item.ThreadId = unchecked((int)eventThread);
            item.ProcessId = unchecked((int)eventProcess);
            queue.Enqueue(item);
        }

        private static string TypeName(uint eventId)
        {
            if (eventId == EVENT_SYSTEM_FOREGROUND) return "window.foreground";
            if (eventId == EVENT_SYSTEM_MENUSTART || eventId == EVENT_SYSTEM_MENUPOPUPSTART) return "menu.open";
            if (eventId == EVENT_SYSTEM_MENUEND || eventId == EVENT_SYSTEM_MENUPOPUPEND) return "menu.close";
            if (eventId == EVENT_OBJECT_CREATE) return "window.create";
            if (eventId == EVENT_OBJECT_DESTROY) return "window.destroy";
            if (eventId == EVENT_OBJECT_SHOW) return "window.show";
            if (eventId == EVENT_OBJECT_HIDE) return "window.hide";
            if (eventId == EVENT_OBJECT_FOCUS) return "focus.change";
            if (eventId == EVENT_OBJECT_SELECTION) return "selection.change";
            if (eventId == EVENT_OBJECT_NAMECHANGE) return "name.change";
            if (eventId == EVENT_OBJECT_VALUECHANGE) return "value.change";
            return "winEvent.0x" + eventId.ToString("X");
        }

        private delegate void WinEventDelegate(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWinEvent(IntPtr hook);
    }
}

