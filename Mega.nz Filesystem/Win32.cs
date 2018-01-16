using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System;

namespace MegaFileSystem
{
    public delegate bool HandlerRoutine(CtrlTypes CtrlType);

    public static class Win32
    {
        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

        [DllImport("shell32.dll")]
        public static extern void SHUpdateImage([MarshalAs(UnmanagedType.LPStr)] string path, int index, uint flags, int imgageindex);
    }

    public enum CtrlTypes
    {
        CTRL_C_EVENT = 0,
        CTRL_BREAK_EVENT,
        CTRL_CLOSE_EVENT,
        CTRL_LOGOFF_EVENT = 5,
        CTRL_SHUTDOWN_EVENT
    }
}
