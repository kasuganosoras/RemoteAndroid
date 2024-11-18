using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using System.Collections.Specialized;
using System.Configuration;
using System.Net;
using System.Web;
using DarkModeForms;
using System.Drawing.Imaging;
using Microsoft.Win32;
using System.Diagnostics;

namespace QLCloseDoor {
    public partial class QLCloseDoor : Form {

        private AdbClient adbClient;
        private DeviceClient deviceClient;
        private DeviceData deviceData;
        private bool isConnected;
        private Config config;
        private HttpListener httpListener;
        private string apiToken;
        private bool isLooping = true;
        private Thread checkThread, httpThread, restartThread;
        private int closeWait = 2500;
        private int apiPort = 14190;
        private bool autoLockScreen = false;
        private NotifyIcon notifyIcon;
        private ContextMenuStrip contextMenu;

        public QLCloseDoor() {
            // 初始化 NotifyIcon
            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = this.Icon;
            notifyIcon.Text = "QLCloseDoor";
            notifyIcon.Visible = true;

            // 创建右键菜单
            contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("连接设备", null, SwitchConnect_Click);
            // 切换 App
            var switchAppMenu = new ToolStripMenuItem("启动应用", null, SwitchApp_Click);
            switchAppMenu.Enabled = false;
            contextMenu.Items.Add(switchAppMenu);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("显示主窗口", null, Open_Click);
            contextMenu.Items.Add("退出", null, Exit_Click);
            notifyIcon.ContextMenuStrip = contextMenu;

            // 处理窗体最小化事件
            this.Resize += new EventHandler(Form_Resize);

            CheckForIllegalCrossThreadCalls = false;
            InitializeComponent();
            if (IsUsingDarkTheme()) {
                _ = new DarkModeCS(this);
                groupBox1.Paint += groupBox1_Paint;
                //groupBox2.Paint += groupBox1_Paint;
                groupBox3.Paint += groupBox1_Paint;
                groupBox4.Paint += groupBox1_Paint;
                groupBox5.Paint += groupBox1_Paint;
                splitContainer1.BackColor = Color.FromArgb(33, 33, 33);
                // splitContainer1.Panel1.BackColor = Color.FromArgb(33, 33, 33);
                // splitContainer1.Panel2.BackColor = Color.FromArgb(33, 33, 33);
                // splitContainer1.Paint += SplitterPaint;
            } else {
                groupBox1.Paint -= groupBox1_Paint;
                // groupBox2.Paint -= groupBox1_Paint;
                groupBox3.Paint -= groupBox1_Paint;
                groupBox4.Paint -= groupBox1_Paint;
                groupBox5.Paint -= groupBox1_Paint;
            }
            config = new Config();
        }
        private void Form_Resize(object sender, EventArgs e) {
            if (this.WindowState == FormWindowState.Minimized) {
                this.Hide();
                notifyIcon.ShowBalloonTip(1000, "提示", "应用程序已最小化到托盘", ToolTipIcon.Info);
            }
        }

        private void SwitchApp_Click(object sender, EventArgs e) {
            if (!isConnected) {
                notifyIcon.ShowBalloonTip(1000, "错误", "当前还未连接到设备", ToolTipIcon.Error);
                return;
            }
            var isStarted = deviceClient.IsAppRunning("com.qinlin.edoor");
            if (isStarted) {
                stopAppCmd("com.qinlin.edoor");
                notifyIcon.ShowBalloonTip(1000, "提示", "已尝试停止应用", ToolTipIcon.Info);
            } else {
                startAppCmd("com.qinlin.edoor/.MainActivity");
                notifyIcon.ShowBalloonTip(1000, "提示", "已尝试启动应用", ToolTipIcon.Info);
            }
        }

        private void SwitchConnect_Click(object sender, EventArgs e) {
            if (isConnected) {
                var result = adbClient.Disconnect(String.Format("{0}:{1}", adbHost.Text, adbPort.Text));
                PrintLog(LogLevel.Info, "已断开连接：" + result);
                SetAdbConfigEnabled(true);
                isConnected = false;
                notifyIcon.ShowBalloonTip(1000, "提示", "已断开连接", ToolTipIcon.Info);
            } else {
                if (adbClient == null) {
                    adbClient = new AdbClient();
                }
                PrintLog(LogLevel.Info, "正在尝试连接：" + adbHost.Text + ":" + adbPort.Text);
                var result = adbClient.Connect(String.Format("{0}:{1}", adbHost.Text, adbPort.Text));
                PrintLog(LogLevel.Info, "连接到 ADB 服务器：" + result);
                deviceData = adbClient.GetDevices().First();
                if (deviceData != null) {
                    deviceClient = new DeviceClient(adbClient, deviceData);
                    PrintLog(LogLevel.Info, "已连接：" + deviceData.Name);
                    SetAdbConfigEnabled(false);
                    isConnected = true;
                    notifyIcon.ShowBalloonTip(1000, "提示", "已连接：" + deviceData.Name, ToolTipIcon.Info);
                } else {
                    PrintLog(LogLevel.Error, "未找到可用的设备，请检查模拟器是否在运行中！");
                    SetAdbConfigEnabled(true);
                    isConnected = false;
                    notifyIcon.ShowBalloonTip(1000, "错误", "未找到可用的设备，请检查模拟器是否在运行中！", ToolTipIcon.Error);
                }
            }
        }

        private void Open_Click(object sender, EventArgs e) {
            this.Show();
            this.WindowState = FormWindowState.Normal;
        }

        private void Exit_Click(object sender, EventArgs e) {
            notifyIcon.Visible = false;
            Application.Exit();
        }

        private bool IsUsingDarkTheme() {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null) {
                var theme = key.GetValue("AppsUseLightTheme");
                if (theme != null) {
                    return theme.ToString() == "0";
                }
            }
            return false;
        }

        private void Form1_Load(object sender, EventArgs e) {
            if (!File.Exists(@"adb\adb.exe")) {
                MessageBox.Show("未找到 adb.exe，请检查 adb 目录及 adb.exe 是否存在。", "未找到 adb");
                Application.Exit();
                return;
            }
            if (!AdbServer.Instance.GetStatus().IsRunning) {
                AdbServer server = new AdbServer();
                StartServerResult result = server.StartServer(@"adb\adb.exe", false);
                if (result != StartServerResult.Started) {
                    PrintLog(LogLevel.Error, "无法启动 ADB 服务器");
                } else {
                    PrintLog(LogLevel.Info, "ADB 服务器已启动");
                }
            } else {
                PrintLog(LogLevel.Info, "ADB 服务器已在运行中");
            }
            // config
            btn1X.Text = config.GetConfig("btn1X", "541");
            btn1Y.Text = config.GetConfig("btn1Y", "641");
            btn2X.Text = config.GetConfig("btn2X", "205");
            btn2Y.Text = config.GetConfig("btn2Y", "938");
            btn3X.Text = config.GetConfig("btn3X", "541");
            btn3Y.Text = config.GetConfig("btn3Y", "938");

            // adb config
            adbHost.Text = config.GetConfig("adbHost", "127.0.0.1");
            adbPort.Text = config.GetConfig("adbPort", "62001");

            // api config
            apiToken = config.GetConfig("apiToken", "123456789");
            apiPort = config.GetIntConfig("apiPort", 14190);
            closeWait = config.GetIntConfig("closeWait", 2500);
            autoLockScreen = config.GetBoolConfig("autoLockScreen", false);

            // Check is config file exists
            if (!File.Exists("config.ini")) {
                PrintLog(LogLevel.Info, "配置文件不存在，正在创建...");
                SaveConfigToFile();
            }

            PrintLog(LogLevel.Info, "配置项加载成功");

            // 1 Second Tick
            checkThread = new Thread(() =>
            {
                while (isLooping) {
                    if (isConnected && adbClient != null) {
                        ProcessUpdate();
                    }
                    Thread.Sleep(1000);
                    if (!isLooping) {
                        break;
                    }
                    if (isConnected) {
                        connectStatus.Text = "状态：已连接 ADB";
                        contextMenu.Items[0].Text = "断开连接";
                    } else {
                        connectStatus.Text = "状态：未连接 ADB";
                        contextMenu.Items[0].Text = "连接设备";
                    }
                }
            });
            checkThread.IsBackground = true;
            checkThread.Start();

            // Restart interval
            restartThread = new Thread(() =>
            {
                while (isLooping) {
                    if (isConnected && adbClient != null) {
                        var isStarted = deviceClient.IsAppRunning("com.qinlin.edoor");
                        if (isStarted) {
                            stopAppCmd("com.qinlin.edoor");
                        }
                        Thread.Sleep(1000);
                        startAppCmd("com.qinlin.edoor/.MainActivity");
                    }
                    Thread.Sleep(60000 * 60);
                    if (!isLooping) {
                        break;
                    }
                }
            });
            restartThread.IsBackground = true;
            restartThread.Start();

            // Http Listener
            httpListener = new HttpListener();
            httpListener.Prefixes.Add(String.Format("http://*:{0}/", apiPort));
            httpListener.Start();
            httpThread = new Thread(() =>
            {
                PrintLog(LogLevel.Info, String.Format("API 服务器监听端口：0.0.0.0:{0}", apiPort));
                while (isLooping) {
                    var context = httpListener.GetContext();
                    var request = context.Request;
                    var response = context.Response;
                    var url = request.Url.AbsolutePath;
                    var query = request.Url.Query;
                    // verify token
                    var token = HttpUtility.ParseQueryString(query).Get("token");
                    if (token != apiToken) {
                        response.StatusCode = 403;
                        response.StatusDescription = "Forbidden";
                        response.Close();
                        continue;
                    } else if (url == "/click") {
                        // click button
                        var btnId = HttpUtility.ParseQueryString(query).Get("btn");
                        if (btnId != null) {
                            int id;
                            if (int.TryParse(btnId, out id)) {
                                ClickButton(id);
                                response.StatusCode = 200;
                                response.StatusDescription = "OK";
                            } else {
                                response.StatusCode = 400;
                                response.StatusDescription = "Bad Request";
                            }
                        } else {
                            var x = HttpUtility.ParseQueryString(query).Get("x");
                            var y = HttpUtility.ParseQueryString(query).Get("y");
                            if (x != null && y != null) {
                                int intX, intY;
                                if (int.TryParse(x, out intX) && int.TryParse(y, out intY)) {
                                    deviceClient.ClickAsync(intX, intY);
                                    response.StatusCode = 200;
                                    response.StatusDescription = "OK";
                                } else {
                                    response.StatusCode = 400;
                                    response.StatusDescription = "Bad Request";
                                }
                            } else {
                                response.StatusCode = 400;
                                response.StatusDescription = "Bad Request";
                            }
                        }
                    } else if (url == "/screenshot") {
                        // take screenshot
                        if (deviceClient != null) {
                            var screenshot = adbClient.GetFrameBuffer(deviceData);
                            response.ContentType = "image/png";
                            response.StatusCode = 200;
                            response.StatusDescription = "OK";
                            Image image = screenshot.ToImage();
                            image.Save(response.OutputStream, ImageFormat.Png);
                        }
                    } else if (url == "/restart") {
                        // restart app
                        if (deviceClient != null) {
                            stopAppCmd("com.qinlin.edoor");
                            Thread.Sleep(1000);
                            startAppCmd("com.qinlin.edoor/.MainActivity");
                            response.StatusCode = 200;
                            response.StatusDescription = "OK";
                        } else {
                            response.StatusCode = 404;
                            response.StatusDescription = "Not Found";
                        }
                    } else if (url == "/powerbtn") {
                        if (deviceClient != null) {
                            powerBtn();
                        }
                    } else if (url == "/unlock") {
                        if (deviceClient != null) {
                            unlockScreen();
                        }
                    } else {
                        response.StatusCode = 404;
                        response.StatusDescription = "Not Found";
                    }
                    response.Close();
                }
            });
            httpThread.IsBackground = true;
            httpThread.Start();
        }

        private void ProcessUpdate() {
            try {
                if (adbClient != null && deviceClient != null) {
                    if (isConnected) {
                        var isStarted = deviceClient.IsAppRunning("com.qinlin.edoor");
                        if (isStarted) {
                            appStatus.Text = "应用运行中";
                            startApp.Enabled = false;
                            stopApp.Enabled = true;
                            contextMenu.Items[1].Enabled = true;
                            contextMenu.Items[1].Text = "停止应用";
                        } else {
                            appStatus.Text = "应用未运行";
                            startApp.Enabled = true;
                            stopApp.Enabled = false;
                            contextMenu.Items[1].Enabled = true;
                            contextMenu.Items[1].Text = "启动应用";
                        }
                    } else {
                        appStatus.Text = "应用未运行";
                        startApp.Enabled = false;
                        stopApp.Enabled = false;
                        contextMenu.Items[1].Enabled = false;
                        contextMenu.Items[1].Text = "启动应用";
                    }
                }
            } catch (Exception e) {
                PrintLog(LogLevel.Error, e.Message);
                stopApp.Enabled = false;
                startApp.Enabled = false;
            }
        }

        private void PrintLog(LogLevel l, string message) {
            Console.WriteLine(message);
            var time = DateTime.Now;
            string hour = time.Hour < 10 ? "0" + time.Hour : time.Hour.ToString();
            string mins = time.Minute < 10 ? "0" + time.Minute : time.Minute.ToString();
            string secs = time.Second < 10 ? "0" + time.Second : time.Second.ToString();
            var msgs = String.Format("[{0}:{1}:{2}][{3}] {4}\r\n", hour, mins, secs, l.ToString(), message);
            logTextbox.AppendText(msgs);
        }

        private void SetAdbConfigEnabled(bool enabled) {
            adbHost.Enabled = enabled;
            adbPort.Enabled = enabled;
            connectBtn.Enabled = enabled;
            disconnectBtn.Enabled = !enabled;
        }

        private void connectBtn_Click(object sender, EventArgs e) {
            if (adbClient == null) {
                adbClient = new AdbClient();
            }
            PrintLog(LogLevel.Info, "正在尝试连接：" + adbHost.Text + ":" + adbPort.Text);
            var result = adbClient.Connect(String.Format("{0}:{1}", adbHost.Text, adbPort.Text));
            PrintLog(LogLevel.Info, "连接到 ADB 服务器：" + result);
            deviceData = adbClient.GetDevices().First();
            if (deviceData != null) {
                deviceClient = new DeviceClient(adbClient, deviceData);
                PrintLog(LogLevel.Info, "已连接：" + deviceData.Name);
                SetAdbConfigEnabled(false);
                isConnected = true;
            } else {
                PrintLog(LogLevel.Error, "未找到可用的设备，请检查模拟器是否在运行中！");
                SetAdbConfigEnabled(true);
                isConnected = false;
            }
        }

        private void startApp_Click(object sender, EventArgs e) {
            startAppCmd("com.qinlin.edoor/.MainActivity");
            PrintLog(LogLevel.Info, "已尝试启动应用");
        }

        private void stopApp_Click(object sender, EventArgs e) {
            stopAppCmd("com.qinlin.edoor");
            PrintLog(LogLevel.Info, "已尝试停止应用");
        }

        private void startAppCmd(string packageName) {
            executeAdbCmd(String.Format("shell am start {0}", packageName));
        }

        private void stopAppCmd(string packageName) {
            executeAdbCmd(String.Format("shell am force-stop {0}", packageName));
        }

        private void powerBtn() {
            executeAdbCmd("shell input keyevent 26");
        }

        private void unlockScreen() {
            executeAdbCmd("shell input swipe 300 1000 300 500");
        }

        private int getScreenState() {
            string result = executeAdbCmd("shell dumpsys window policy");
            int state = 0;
            if (result.Contains("showing=true")) {
                state = 1;
                if (result.Contains("screenState=2")) {
                    state = 2;
                }
            }
            return state;
        }

        private void executeAdbCmd(string cmd, bool waitForExit =  false) {
            if (!isConnected) return;

            // 创建一个新的进程启动信息
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "adb\\adb.exe";
            startInfo.Arguments = cmd;
            startInfo.RedirectStandardOutput = false;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            // 启动进程
            Process process = Process.Start(startInfo);
            if (waitForExit && process != null && process.Id > 0) {
                process.WaitForExit();
            }
        }

        private string executeAdbCmd(string cmd) {
            if (!isConnected) return "";

            // 创建一个新的进程启动信息
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "adb\\adb.exe";
            startInfo.Arguments = cmd;
            startInfo.RedirectStandardOutput = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            string result = "";
            // 启动进程并读取输出
            using (Process process = Process.Start(startInfo)) {
                using (System.IO.StreamReader reader = process.StandardOutput) {
                    result += reader.ReadToEnd();
                }
            }

            return result;
        }

        private void disconnectBtn_Click(object sender, EventArgs e) {
            var result = adbClient.Disconnect(String.Format("{0}:{1}", adbHost.Text, adbPort.Text));
            PrintLog(LogLevel.Info, "已断开连接：" + result);
            SetAdbConfigEnabled(true);
            isConnected = false;
        }

        private void CloseAd() {
            Thread thread = new Thread(() =>
            {
                Thread.Sleep(closeWait);
                stopAppCmd("com.qinlin.edoor");
                Thread.Sleep(300);
                startAppCmd("com.qinlin.edoor/.MainActivity");
                if (autoLockScreen) {
                    Thread.Sleep(1000);
                    powerBtn();
                }
            });
            thread.Start();
        }

        private void ClickButton(int BtnId) {
            if (deviceClient == null) {
                PrintLog(LogLevel.Error, "当前未连接到设备。");
                return;
            }
            ScreenPos pos = null;
            switch (BtnId) {
                case 1:
                    pos = GetBtn1Pos();
                    break;
                case 2:
                    pos = GetBtn2Pos();
                    break;
                case 3:
                    pos = GetBtn3Pos();
                    break;
            }
            if (pos != null) {
                int state = getScreenState();
                if (state == 1) {
                    powerBtn();
                    Thread.Sleep(500);
                    unlockScreen();
                    Thread.Sleep(1000);
                } else if (state == 2) {
                    unlockScreen();
                    Thread.Sleep(1000);
                }
                deviceClient.ClickAsync(pos.X, pos.Y);
                PrintLog(LogLevel.Info, "发送屏幕点击请求：" + BtnId);
                CloseAd();
            } else {
                PrintLog(LogLevel.Error, "无效的按钮：" + BtnId + "（无法获取坐标）");
            }
        }

        private ScreenPos GetBtn1Pos() {
            var x = btn1X.Text;
            var y = btn1Y.Text;
            var intX = 0;
            var intY = 0;
            if (int.TryParse(x, out intX) && int.TryParse(y, out intY)) {
                return new ScreenPos(intX, intY);
            }
            return null;
        }

        private ScreenPos GetBtn2Pos() {
            var x = btn2X.Text;
            var y = btn2Y.Text;
            var intX = 0;
            var intY = 0;
            if (int.TryParse(x, out intX) && int.TryParse(y, out intY)) {
                return new ScreenPos(intX, intY);
            }
            return null;
        }

        private ScreenPos GetBtn3Pos() {
            var x = btn3X.Text;
            var y = btn3Y.Text;
            var intX = 0;
            var intY = 0;
            if (int.TryParse(x, out intX) && int.TryParse(y, out intY)) {
                return new ScreenPos(intX, intY);
            }
            return null;
        }

        private void button1_Click_1(object sender, EventArgs e) {
            ClickButton(1);
        }

        private void button2_Click(object sender, EventArgs e) {
            ClickButton(2);
        }

        private void button3_Click(object sender, EventArgs e) {
            ClickButton(3);
        }

        private void saveBtn_Click(object sender, EventArgs e) {
            SaveConfigToFile();
            PrintLog(LogLevel.Info, "配置文件已保存");
        }

        private void SaveConfigToFile() {
            // button config
            config.SetConfig("btn1X", btn1X.Text);
            config.SetConfig("btn1Y", btn1Y.Text);
            config.SetConfig("btn2X", btn2X.Text);
            config.SetConfig("btn2Y", btn2Y.Text);
            config.SetConfig("btn3X", btn3X.Text);
            config.SetConfig("btn3Y", btn3Y.Text);
            // adb config
            config.SetConfig("adbHost", adbHost.Text);
            config.SetConfig("adbPort", adbPort.Text);
            // api config
            config.SetConfig("apiToken", apiToken);
            config.SetConfig("apiPort", apiPort.ToString());
            config.SetConfig("closeWait", closeWait.ToString());
            config.SetConfig("autoLockScreen", autoLockScreen.ToString());

            config.SaveConfig();
        }

        private void DrawGroupBox(GroupBox box, Graphics g, Color textColor, Color borderColor) {
            if (box != null) {
                Brush textBrush = new SolidBrush(textColor);
                Brush borderBrush = new SolidBrush(borderColor);
                Pen borderPen = new Pen(borderBrush);
                SizeF strSize = g.MeasureString(box.Text, box.Font);
                Rectangle rect = new Rectangle(box.ClientRectangle.X,
                                               box.ClientRectangle.Y + (int)(strSize.Height / 2),
                                               box.ClientRectangle.Width - 1,
                                               box.ClientRectangle.Height - (int)(strSize.Height / 2) - 1);

                // Clear text and border
                g.Clear(Color.FromArgb(33, 33, 33));

                // Draw text
                g.DrawString(box.Text, box.Font, textBrush, box.Padding.Left, 0);

                // Drawing Border
                //Left
                g.DrawLine(borderPen, rect.Location, new Point(rect.X, rect.Y + rect.Height));
                //Right
                g.DrawLine(borderPen, new Point(rect.X + rect.Width, rect.Y), new Point(rect.X + rect.Width, rect.Y + rect.Height));
                //Bottom
                g.DrawLine(borderPen, new Point(rect.X, rect.Y + rect.Height), new Point(rect.X + rect.Width, rect.Y + rect.Height));
                //Top1
                g.DrawLine(borderPen, new Point(rect.X, rect.Y), new Point(rect.X + box.Padding.Left, rect.Y));
                //Top2
                g.DrawLine(borderPen, new Point(rect.X + box.Padding.Left + (int)(strSize.Width), rect.Y), new Point(rect.X + rect.Width, rect.Y));
            }
        }

        private void groupBox1_Paint(object sender, PaintEventArgs e) {
            GroupBox box = sender as GroupBox;
            DrawGroupBox(box, e.Graphics, Color.FromArgb(200, 200, 200), Color.FromArgb(80, 150, 150, 150));
        }

        private void SplitterPaint(object sender, PaintEventArgs e) {
            SplitContainer s = sender as SplitContainer;
            if (s != null) {
                int top = 5;
                int bottom = s.Height - 5;
                int left = s.SplitterDistance;
                int right = left + s.SplitterWidth - 1;
                e.Graphics.DrawLine(Pens.Silver, left, top, left, bottom);
                e.Graphics.DrawLine(Pens.Silver, right, top, right, bottom);
            }
        }

        private void FuckQL_FormClosing(object sender, FormClosingEventArgs e) {
            isConnected = false;
            isLooping = false;
            if (adbClient == null || adbHost == null || adbPort == null) { return; }
            try {
                adbClient.Disconnect(String.Format("{0}:{1}", adbHost.Text, adbPort.Text));
            } catch { }
            try {
                AdbServer.Instance.StopServer();
            } catch { }
        }
    }

    class Config {
        private static string configFilePath = "config.ini";
        private NameValueCollection settings;

        public Config() {
            LoadConfig();
        }

        public string this[string key] {
            get { return settings[key]; }
            set { settings[key] = value; }
        }

        public string GetConfig(string key, string defaultVal) {
            return settings[key] ?? defaultVal;
        }

        public int GetIntConfig(string key, int defaultVal) {
            var val = settings[key];
            if (val != null) {
                int result;
                if (int.TryParse(val, out result)) {
                    return result;
                }
            }
            return defaultVal;
        }

        public bool GetBoolConfig(string key, bool defaultVal) {
            var val = settings[key];
            if (val != null) {
                bool result;
                if (bool.TryParse(val, out result)) {
                    return result;
                }
            }
            return defaultVal;
        }

        public void SetConfig(string key, string value) {
            settings[key] = value;
        }

        private void LoadConfig() {
            if (File.Exists(configFilePath)) {
                settings = new NameValueCollection();
                foreach (var row in File.ReadAllLines(configFilePath)) {
                    if (!string.IsNullOrEmpty(row)) {
                        var index = row.IndexOf('=');
                        if (index > 0)
                            settings.Add(row.Substring(0, index), row.Substring(index + 1));
                    }
                }
            } else {
                settings = ConfigurationManager.AppSettings;
            }
        }

        public void SaveConfig() {
            using (StreamWriter writer = new StreamWriter(configFilePath)) {
                foreach (var key in settings.AllKeys) {
                    writer.WriteLine($"{key}={settings[key]}");
                }
            }
        }
    }

    class LogLevel {
        public static LogLevel Info = new LogLevel("INFO");
        public static LogLevel Error = new LogLevel("ERROR");
        public static LogLevel Debug = new LogLevel("DEBUG");
        public static LogLevel Warning = new LogLevel("WARNING");

        private string level;

        private LogLevel(string level) {
            this.level = level;
        }

        public override string ToString() {
            return level;
        }
    }

    class ScreenPos {

        private int x;
        private int y;

        private ScreenPos() { }

        public ScreenPos(int x, int y) {
            this.x = x;
            this.y = y;
        }

        public int X {
            get { return x; }
            set { x = value; }
        }

        public int Y {
            get { return y; }
            set { y = value; }
        }
    }
}
