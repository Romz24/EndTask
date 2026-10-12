// EndTask — пункт «Завершить задачу» для панели задач Windows 10.
// Ctrl + правая кнопка мыши по программе на панели задач открывает меню
// с пунктом «Завершить задачу», который принудительно завершает процесс.
//
// Сборка: build.bat (нужен только .NET Framework 4, он уже есть в Windows 10).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Accessibility;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("EndTask")]
[assembly: System.Reflection.AssemblyDescription("Завершить задачу с панели задач")]
[assembly: System.Reflection.AssemblyProduct("EndTask")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace EndTask
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();

            // Служебные режимы для сборки и проверки.
            if (args.Length == 2 && args[0] == "/makeicon") { IconMaker.SaveIco(args[1]); return; }
            if (args.Length == 2 && args[0] == "/probe") { File.WriteAllText(args[1], Probe.Report(), Encoding.UTF8); return; }

            bool created;
            using (var mutex = new Mutex(true, @"Local\EndTask_TaskbarHelper", out created))
            {
                if (!created)
                {
                    Native.MessageBox(IntPtr.Zero, "EndTask уже запущен — его значок в области уведомлений.", "EndTask",
                        Native.MB_ICONINFORMATION | Native.MB_TOPMOST);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                int demo = -1;
                if (args.Length == 2 && args[0] == "/demo") int.TryParse(args[1], out demo);
                Application.Run(new TrayApp(Array.IndexOf(args, "/silent") >= 0 || demo >= 0, demo));
            }
        }
    }

    // Окно верхнего уровня, которое может соответствовать кнопке на панели задач.
    sealed class TaskWindow
    {
        public IntPtr Hwnd;
        public string Title;
        public int Pid;
        public bool Eligible;   // окно, которое Windows обычно показывает на панели задач
        public string ExePath;
        public bool CloseOnly;  // не убивать процесс, а только закрыть окно (проводник и т.п.)
        public Image Icon;
    }

    sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "EndTask";

        readonly Control invoker = new Control();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem autostartItem;
        readonly MouseHook hook;
        ContextMenuStrip taskMenu;

        public TrayApp(bool silent, int demo)
        {
            IntPtr forceHandle = invoker.Handle; // нужен дескриптор, чтобы работал BeginInvoke

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Ctrl + ПКМ по программе на панели задач") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            autostartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, delegate { ToggleAutostart(); });
            autostartItem.Checked = IsAutostart();
            menu.Items.Add(autostartItem);
            menu.Items.Add("Выход", null, delegate { ExitThread(); });

            tray.Icon = IconMaker.Make(SystemInformation.SmallIconSize.Width);
            tray.Text = "EndTask — Ctrl + ПКМ по программе на панели задач";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowHint(); };
            tray.Visible = true;

            hook = new MouseHook(invoker);
            hook.CtrlRightClick = ShowTaskMenu;
            hook.MouseDown = CloseMenuIfOutside;
            if (!hook.Start())
            {
                Native.MessageBox(IntPtr.Zero, "Не удалось перехватить мышь (код " + hook.Error + ").", "EndTask",
                    Native.MB_ICONERROR | Native.MB_TOPMOST);
                tray.Visible = false;
                invoker.BeginInvoke((Action)ExitThread);
                return;
            }

            if (demo >= 0)
            {
                // Показать меню для N-й кнопки без щелчка и выйти через несколько секунд (для проверки).
                invoker.BeginInvoke((Action)delegate
                {
                    Rectangle r = Probe.ButtonRect(demo);
                    if (!r.IsEmpty) ShowTaskMenu(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
                });
                var t = new System.Windows.Forms.Timer { Interval = 6000 };
                t.Tick += delegate { t.Stop(); ExitThread(); };
                t.Start();
            }
            else if (!silent) ShowHint();
        }

        void ShowHint()
        {
            tray.ShowBalloonTip(5000, "EndTask работает",
                "Зажмите Ctrl и щёлкните правой кнопкой по программе на панели задач.", ToolTipIcon.Info);
        }

        void ShowTaskMenu(Point p)
        {
            string name = Native.TaskbarButtonNameAt(p);
            if (string.IsNullOrEmpty(name)) return;
            List<TaskWindow> targets = Matcher.Find(name);

            if (taskMenu != null) { taskMenu.Close(); taskMenu.Dispose(); }
            taskMenu = new PopupMenu();

            var header = new ToolStripMenuItem(Shorten(Matcher.StripSuffix(name), 60)) { Enabled = false };
            if (targets.Count > 0) header.Image = targets[0].Icon;
            taskMenu.Items.Add(header);
            taskMenu.Items.Add(new ToolStripSeparator());

            if (targets.Count == 0)
            {
                taskMenu.Items.Add(new ToolStripMenuItem("Не удалось определить программу") { Enabled = false });
            }
            else
            {
                bool closeOnly = targets.TrueForAll(delegate(TaskWindow t) { return t.CloseOnly; });
                var item = new ToolStripMenuItem(closeOnly ? "Закрыть окно" : "Завершить задачу", null,
                    delegate { EndTargets(targets); });
                item.Font = new Font(taskMenu.Font, FontStyle.Bold);
                item.ToolTipText = Describe(targets);
                taskMenu.Items.Add(item);
            }

            taskMenu.Show(p);
            Native.SetForegroundWindow(taskMenu.Handle);
        }

        // Меню не получает фокус, поэтому закрываем его сами по щелчку мимо.
        void CloseMenuIfOutside(Point p)
        {
            if (taskMenu != null && taskMenu.Visible && !taskMenu.Bounds.Contains(p)) taskMenu.Close();
        }

        static string Describe(List<TaskWindow> targets)
        {
            var seen = new HashSet<int>();
            var sb = new StringBuilder();
            foreach (var t in targets)
            {
                if (!seen.Add(t.Pid)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.ExePath != null ? Path.GetFileName(t.ExePath) : "?").Append("  (PID ").Append(t.Pid).Append(')');
            }
            return sb.ToString();
        }

        void EndTargets(List<TaskWindow> targets)
        {
            var denied = new List<TaskWindow>();
            var done = new HashSet<int>();
            foreach (var t in targets)
            {
                if (t.CloseOnly) { Native.PostMessage(t.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); continue; }
                if (!done.Add(t.Pid)) continue;
                try { using (var p = Process.GetProcessById(t.Pid)) p.Kill(); }
                catch (ArgumentException) { }          // процесс уже завершился
                catch (InvalidOperationException) { }
                catch (Win32Exception) { denied.Add(t); } // нет прав (программа запущена от администратора)
            }
            if (denied.Count == 0) return;

            var names = new List<string>();
            foreach (var d in denied) names.Add(d.ExePath != null ? Path.GetFileName(d.ExePath) : "PID " + d.Pid);
            int answer = Native.MessageBox(IntPtr.Zero,
                "Не хватает прав, чтобы завершить " + string.Join(", ", names.ToArray()) +
                ": программа запущена от имени администратора.\n\nЗавершить с правами администратора?",
                "EndTask", Native.MB_YESNO | Native.MB_ICONWARNING | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
            if (answer != Native.IDYES) return;

            var args = new StringBuilder("/F");
            foreach (var d in denied) args.Append(" /PID ").Append(d.Pid);
            try
            {
                Process.Start(new ProcessStartInfo("taskkill.exe", args.ToString())
                {
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Win32Exception) { } // отказ в окне UAC
        }

        static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        void ToggleAutostart()
        {
            bool on = IsAutostart();
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.DeleteValue(RunValue, false);
                else k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" /silent");
            }
            autostartItem.Checked = IsAutostart();
        }

        static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        protected override void ExitThreadCore()
        {
            hook.Stop();
            if (taskMenu != null) taskMenu.Close();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // Меню без владельца WinForms создаёт как обычное окно, и оно появляется на панели задач.
    // Стиль WS_EX_TOOLWINDOW это запрещает.
    sealed class PopupMenu : ContextMenuStrip
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
                return cp;
            }
        }
    }

    // Глобальный перехват мыши в отдельном потоке, чтобы система не ждала основной поток.
    sealed class MouseHook
    {
        public Action<Point> CtrlRightClick;
        public Action<Point> MouseDown;
        public int Error;

        readonly Control invoker;
        Native.LowLevelMouseProc proc;
        IntPtr handle;
        int threadId;
        volatile bool swallowUp;

        public MouseHook(Control invoker) { this.invoker = invoker; }

        public bool Start()
        {
            var ready = new ManualResetEvent(false);
            var thread = new Thread(delegate()
            {
                threadId = Native.GetCurrentThreadId();
                proc = Callback;
                handle = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
                if (handle == IntPtr.Zero) Error = Marshal.GetLastWin32Error();
                ready.Set();
                if (handle == IntPtr.Zero) return;

                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Native.UnhookWindowsHookEx(handle);
            });
            thread.IsBackground = true;
            thread.Name = "MouseHook";
            thread.Start();
            ready.WaitOne();
            return handle != IntPtr.Zero;
        }

        public void Stop()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                var p = new Point(info.pt.x, info.pt.y);

                if (msg == Native.WM_RBUTTONDOWN && (info.flags & Native.LLMHF_INJECTED) == 0
                    && (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0
                    && Native.IsTaskListAt(info.pt))
                {
                    // Проводник не должен увидеть этот щелчок, иначе откроется его обычное меню.
                    swallowUp = true;
                    Post(CtrlRightClick, p);
                    return (IntPtr)1;
                }
                if (msg == Native.WM_RBUTTONUP && swallowUp)
                {
                    swallowUp = false;
                    return (IntPtr)1;
                }
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                    Post(MouseDown, p);
            }
            return Native.CallNextHookEx(handle, code, wParam, lParam);
        }

        void Post(Action<Point> action, Point p)
        {
            if (action == null) return;
            try { invoker.BeginInvoke(action, p); }
            catch (InvalidOperationException) { }
        }
    }

    // Сопоставление кнопки панели задач с окнами по имени, которое кнопка сообщает
    // через MSAA: «Заголовок —1 запущенное окно», «Проводник — запущенных окон: 2»,
    // в английской Windows «Untitled - Notepad - 1 running window».
    static class Matcher
    {
        static readonly Regex Suffix = new Regex(@"^\s*[—–-]\s*[^—–-]*\d[^—–-]*$");
        static readonly Regex WithSuffix = new Regex(@"^(.*?)\s*[—–-]\s*[^—–-]*\d[^—–-]*$");
        static readonly Dictionary<string, Image> IconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        public static string StripSuffix(string name)
        {
            Match m = WithSuffix.Match(name);
            return m.Success ? m.Groups[1].Value.Trim() : name;
        }

        public static List<TaskWindow> Find(string buttonName)
        {
            List<TaskWindow> windows = Native.TopLevelWindows();
            var result = new List<TaskWindow>();

            // 1. Кнопка отдельного окна: имя = заголовок окна + суффикс. Берём самый длинный подходящий заголовок.
            int bestLen = 0;
            foreach (var w in windows)
            {
                string t = w.Title;
                bool match = buttonName == t ||
                    (buttonName.StartsWith(t, StringComparison.Ordinal) && Suffix.IsMatch(buttonName.Substring(t.Length)));
                if (!match) continue;
                if (t.Length > bestLen) { result.Clear(); bestLen = t.Length; }
                if (t.Length == bestLen) result.Add(w);
            }

            // 2. Сгруппированная кнопка: имя = название программы («Google Chrome», «Проводник»).
            if (result.Count == 0)
            {
                string app = StripSuffix(buttonName);
                foreach (var w in windows)
                    if (w.Eligible && AppNameMatches(app, w.Pid)) result.Add(w);
            }

            foreach (var w in result) Resolve(w);
            return result;
        }

        static bool AppNameMatches(string app, int pid)
        {
            string path = Native.ProcessPath(pid);
            if (path == null || app.Length == 0) return false;
            var names = new List<string> { Path.GetFileNameWithoutExtension(path) };
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(vi.FileDescription)) names.Add(vi.FileDescription.Trim());
                if (!string.IsNullOrEmpty(vi.ProductName)) names.Add(vi.ProductName.Trim());
            }
            catch (Exception) { }

            foreach (string n in names)
            {
                if (n.Length < 2) continue;
                if (n.Equals(app, StringComparison.OrdinalIgnoreCase)) return true;
                if (n.StartsWith(app + " ", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.Length >= 3 && app.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void Resolve(TaskWindow w)
        {
            w.ExePath = Native.ProcessPath(w.Pid);
            string exe = w.ExePath != null ? Path.GetFileName(w.ExePath).ToLowerInvariant() : "";

            if (exe == "applicationframehost.exe")
            {
                // Приложение из Microsoft Store: настоящий процесс живёт в дочернем окне рамки.
                int real = Native.ForeignChildPid(w.Hwnd, w.Pid);
                if (real != 0) { w.Pid = real; w.ExePath = Native.ProcessPath(real); }
                else w.CloseOnly = true;
            }
            // Убить explorer.exe значит убить и саму панель задач, поэтому окна проводника просто закрываем.
            if (exe == "explorer.exe" || w.Pid == Process.GetCurrentProcess().Id) w.CloseOnly = true;

            w.Icon = IconFor(w.ExePath);
        }

        static Image IconFor(string path)
        {
            if (path == null) return null;
            Image img;
            if (IconCache.TryGetValue(path, out img)) return img;
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(path))
                    img = new Bitmap(ic.ToBitmap(), SystemInformation.SmallIconSize);
            }
            catch (Exception) { img = null; }
            IconCache[path] = img;
            return img;
        }
    }

    // Отчёт о кнопках панели задач и найденных для них процессах (режим /probe).
    static class Probe
    {
        public static List<KeyValuePair<string, Rectangle>> Buttons()
        {
            var list = new List<KeyValuePair<string, Rectangle>>();
            foreach (IntPtr taskList in Native.TaskLists())
            {
                IAccessible acc;
                Guid iid = Native.IID_IAccessible;
                object o;
                if (Native.AccessibleObjectFromWindow(taskList, Native.OBJID_CLIENT, ref iid, out o) != 0) continue;
                acc = o as IAccessible;
                if (acc == null) continue;
                int n = acc.accChildCount;
                var kids = new object[n];
                int got;
                Native.AccessibleChildren(acc, 0, n, kids, out got);
                for (int i = 0; i < got; i++)
                {
                    if (kids[i] is IAccessible) continue;
                    int x, y, w, h;
                    string name;
                    try
                    {
                        acc.accLocation(out x, out y, out w, out h, kids[i]);
                        name = acc.get_accName(kids[i]);
                    }
                    catch (COMException) { continue; } // кнопка исчезла, пока мы перебирали список
                    if (w > 0 && h > 0 && !string.IsNullOrEmpty(name))
                        list.Add(new KeyValuePair<string, Rectangle>(name, new Rectangle(x, y, w, h)));
                }
            }
            return list;
        }

        public static Rectangle ButtonRect(int index)
        {
            var b = Buttons();
            return index < b.Count ? b[index].Value : Rectangle.Empty;
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            foreach (var b in Buttons())
            {
                Point c = new Point(b.Value.Left + b.Value.Width / 2, b.Value.Top + b.Value.Height / 2);
                sb.Append("Кнопка: ").Append(b.Key).Append("   ").Append(b.Value).AppendLine();
                sb.Append("  по точке: ").Append(Native.TaskbarButtonNameAt(c) ?? "<нет>").AppendLine();
                foreach (var t in Matcher.Find(b.Key))
                    sb.Append("  -> PID ").Append(t.Pid).Append(' ').Append(t.ExePath).Append(" | \"").Append(t.Title)
                      .Append("\"").Append(t.CloseOnly ? " [только закрыть]" : "").AppendLine();
            }
            return sb.ToString();
        }
    }

    static class IconMaker
    {
        // Красный круг с белым крестиком.
        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float pad = size / 16f;
                using (var b = new SolidBrush(Color.FromArgb(214, 48, 49)))
                    g.FillEllipse(b, pad, pad, size - 2 * pad, size - 2 * pad);
                using (var pen = new Pen(Color.White, Math.Max(1.5f, size / 7f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float a = size * 0.33f, z = size * 0.67f;
                    g.DrawLine(pen, a, a, z, z);
                    g.DrawLine(pen, z, a, a, z);
                }
            }
            return bmp;
        }

        public static Icon Make(int size)
        {
            using (Bitmap bmp = Draw(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        // .ico с PNG-кадрами 16/32/48/256 — для значка самого exe.
        public static void SaveIco(string path)
        {
            int[] sizes = { 16, 32, 48, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap bmp = Draw(s))
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] png in pngs) w.Write(png);
            }
        }
    }

    static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_QUIT = 0x0012, WM_CLOSE = 0x0010;
        public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207;
        public const int LLMHF_INJECTED = 0x1;
        public const int VK_CONTROL = 0x11;
        public const uint OBJID_CLIENT = 0xFFFFFFFC;
        public const int ROLE_SYSTEM_WHITESPACE = 0x3B;
        public static readonly Guid IID_IAccessible = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
        public const uint MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40,
                          MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        public const int IDYES = 6;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        const int GWL_EXSTYLE = -20;
        const uint GW_OWNER = 4;
        const int DWMWA_CLOAKED = 14;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(int thread, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc fn, IntPtr lParam);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out IAccessible acc, out object child);
        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object acc);
        [DllImport("oleacc.dll")]
        public static extern int AccessibleChildren(IAccessible acc, int start, int count, [Out] object[] children, out int got);

        static string ClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsTaskListAt(POINT pt)
        {
            return ClassName(WindowFromPoint(pt)) == "MSTaskListWClass";
        }

        // Списки кнопок на основной панели задач и на панелях других мониторов.
        public static List<IntPtr> TaskLists()
        {
            var result = new List<IntPtr>();
            foreach (string trayClass in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr tray = IntPtr.Zero;
                while ((tray = FindWindowEx(IntPtr.Zero, tray, trayClass, null)) != IntPtr.Zero)
                    FindDescendants(tray, "MSTaskListWClass", result);
            }
            return result;
        }

        static void FindDescendants(IntPtr parent, string cls, List<IntPtr> into)
        {
            EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
            {
                if (ClassName(h) == cls) into.Add(h);
                return true;
            }, IntPtr.Zero);
        }

        public static string TaskbarButtonNameAt(Point p)
        {
            try
            {
                IAccessible acc;
                object child;
                if (AccessibleObjectFromPoint(new POINT { x = p.X, y = p.Y }, out acc, out child) != 0 || acc == null) return null;
                object role = acc.get_accRole(child);
                if (role is int && (int)role == ROLE_SYSTEM_WHITESPACE) return null;
                return acc.get_accName(child);
            }
            catch (COMException) { return null; }
            catch (ArgumentException) { return null; }
        }

        public static List<TaskWindow> TopLevelWindows()
        {
            var list = new List<TaskWindow>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                string cls = ClassName(h);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;

                var sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
                list.Add(new TaskWindow
                {
                    Hwnd = h,
                    Title = sb.ToString(),
                    Pid = (int)pid,
                    Eligible = (ex & WS_EX_APPWINDOW) != 0 || ((ex & WS_EX_TOOLWINDOW) == 0 && !owned)
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // PID первого дочернего окна, принадлежащего другому процессу (для приложений Store).
        public static int ForeignChildPid(IntPtr parent, int parentPid)
        {
            int found = 0;
            EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if ((int)pid != parentPid) { found = (int)pid; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }
}
