using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using System.Threading;
using System.IO;
using System.ComponentModel;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Xml.Linq;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace SystemUpgrade
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        List<UserConnInfo> listConnInfo;
        UserConnInfo currentConnInfo;

        // for file trans
        SftpClient m_sftpClient;
        List<UserTxRxInfo> listTxRxFile;
        UserTxRxInfo currentUploadItem;
        Thread uploadThread;
        Thread connectThread;
        ulong uploadFileLength;
        string remoteDirPath;

        // for SSH command
        List<UserCommand> listSshCommand_Pre;
        List<UserCommand> listSshCommand;
        SshClient m_sshCommand;
        ShellStream m_sshShell;
        Thread sshRecvthread;
        Thread sshCommandthread;

        // resize
        bool windowMove;
        Point lastPoint;

        static Regex prompt = new Regex("[a-zA-Z0-9_.-]*\\@[a-zA-Z0-9_.-]*\\:\\~[#$] ", RegexOptions.Compiled);
        static Regex pwdPrompt = new Regex("password for .*\\:", RegexOptions.Compiled);
        static Regex promptOrPwd = new Regex(prompt + "|" + pwdPrompt);

        public MainWindow()
        {
            InitializeComponent();

            m_sshCommand = null;
            m_sshShell = null;
            m_sftpClient = null;

            listConnInfo = new List<UserConnInfo>();
            listConnInfo.Add(new UserConnInfo("192.168.20.192", "mik21", "nvidia"));
            listConnInfo.Add(new UserConnInfo("192.168.20.192", "mik21", "roqkfdmfwkfgkwk!"));
            listConnInfo.Add(new UserConnInfo("192.168.20.192", "mik21", "roqkfdmfwkfgkwk!"));
            listConnInfo.Add(new UserConnInfo("192.168.0.100",  "sujin", "aprtmdnpf05!"));

            listTxRxFile = new List<UserTxRxInfo>();
            listLocalFile.ItemsSource = listTxRxFile;
            remoteDirPath = "";
            currentUploadItem = null;

            listSshCommand_Pre = new List<UserCommand>();
            listSshCommand = new List<UserCommand>();

            windowMove = false;

            expenderDebug.VerticalAlignment = VerticalAlignment.Center;
            DeactivateUI();
        }

        /**************************/
        //     User Function
        /**************************/
        private void DeactivateUI()
        {
            stackButtons.IsEnabled = false;
            listTxRxFile.Clear();
            listSshCommand.Clear();
            listSshCommand_Pre.Clear();

            foreach(UserTxRxInfo info in listTxRxFile)
            {
                info.Progress = 0;
            }
            listLocalFile.Items.Refresh();
            listRemoteFile.ItemsSource = new List<UserFileInfo>();

            txtSshLog.Clear();

            btnConnect.Content = "Connect";
        }

        private void ActivateUI()
        {
            stackButtons.IsEnabled = true;
            btnLoadDatas.IsEnabled = true;
            btnCheckUpgrade.IsEnabled = false;
            btnUpgradeDatas.IsEnabled = false;

        }

        private bool LoadConfig(string config_path)
        {
            if((config_path==null) || (config_path == ""))
            {
                return false;
            }

            try
            {
                if (File.Exists(config_path))
                {
                    int file_not_exists = 0;

                    string local_dir = System.IO.Path.GetDirectoryName(config_path);
                    lblLocalPath.Text = local_dir;

                    var xdoc = XDocument.Load(config_path);

                    // get library list
                    var xelements = xdoc.Root.Elements("libs");

                    listTxRxFile.Clear();
                    foreach (var xList in xelements)
                    {
                        XElement element;

                        element = xList.Element("lib");
                        if (element == null)
                        {
                            continue;
                        }

                        string file_name = element.Value;
                        string local_path = local_dir + "/" + file_name;
                        string remote_path = m_sftpClient.WorkingDirectory + "/"+ file_name;
                        
                        int expected_size = 0;
                        element = xList.Element("size");
                        if (element != null)
                        {
                            expected_size = Convert.ToInt32(element.Value);
                        }

                        UserTxRxInfo info = new UserTxRxInfo(local_path, remote_path, expected_size);
                        listTxRxFile.Add(info);

                        if(!info.valid_file)
                        {
                            file_not_exists++;
                        }
                    }
                    listLocalFile.Items.Refresh();

                    // get command list
                    var cmd_elements = xdoc.Root.Elements("cmds");
                    listSshCommand.Clear();
                    foreach (var xList in cmd_elements)
                    {
                        XElement cmd_element = xList.Element("cmd");
                        if (cmd_element == null)
                        {
                            continue;
                        }

                        XElement sudo_element = xList.Element("sudo");
                        if (sudo_element != null)
                        {
                            bool sudo = Convert.ToBoolean(sudo_element.Value);
                            listSshCommand.Add(new UserCommand(cmd_element.Value, sudo));
                        }
                        else
                        {
                            listSshCommand.Add(new UserCommand(cmd_element.Value, false));
                        }
                    }

                    // get command list
                    var precmd_elements = xdoc.Root.Elements("pre_cmds");
                    listSshCommand_Pre.Clear();
                    foreach (var xList in precmd_elements)
                    {
                        XElement cmd_element = xList.Element("cmd");
                        if (cmd_element == null)
                        {
                            continue;
                        }

                        XElement sudo_element = xList.Element("sudo");
                        if (sudo_element != null)
                        {
                            bool sudo = Convert.ToBoolean(sudo_element.Value);
                            listSshCommand_Pre.Add(new UserCommand(cmd_element.Value, sudo));
                        }
                        else
                        {
                            listSshCommand_Pre.Add(new UserCommand(cmd_element.Value, false));
                        }
                    }

                    return (file_not_exists == 0);
                }
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
            }


            return false;
        }

        private void StartStopWait(bool wait_enable)
        {
            LoadingAdorner.IsAdornerVisible = wait_enable;
            listRemoteFile.IsEnabled = !wait_enable;
        }

        private UserConnInfo get_current_conn()
        {
            UserConnInfo info = listConnInfo.ElementAt(0);

            if (rbFpd3Sel1.IsChecked == true) info = listConnInfo.ElementAt(2);
            if (rbFpd4Sel2.IsChecked == true) info = listConnInfo.ElementAt(1);
            if (rbTest.IsChecked == true) info = listConnInfo.ElementAt(3);

            currentConnInfo = info;
            return info;
        }

        private bool loadRemoteDirList()
        {
            if ((m_sftpClient == null) || (!m_sftpClient.IsConnected))
            {
                return false;
            }

            try
            {
                List<UserFileInfo> user_Items = new List<UserFileInfo>();
                user_Items.Add(new UserFileInfo(".."));

                List<UserFileInfo> dir_Items = new List<UserFileInfo>();
                foreach (var entry in m_sftpClient.ListDirectory(remoteDirPath))
                {
                    if (entry.IsDirectory)
                    {
                        String FileNameOnly = entry.Name;

                        if (FileNameOnly.Equals("."))
                        {
                            continue;
                        }
                        if (FileNameOnly.Equals(".."))
                        {
                            continue;
                        }
                        dir_Items.Add(new UserFileInfo(FileNameOnly));
                    }
                }
                dir_Items = dir_Items.OrderBy(a => a.Name).ToList();

                List<UserFileInfo> file_Items = new List<UserFileInfo>();
                foreach (var entry in m_sftpClient.ListDirectory(remoteDirPath))
                {
                    if (entry.IsRegularFile)
                    {
                        String FileNameOnly = entry.Name;
                        String FileSize = entry.Length.ToString("#,##0") + "B";

                        file_Items.Add(new UserFileInfo(FileNameOnly, FileSize, true));
                    }
                }
                file_Items = file_Items.OrderBy(a => a.Name).ToList();

                user_Items.AddRange(dir_Items);
                user_Items.AddRange(file_Items);
                listRemoteFile.ItemsSource = user_Items;
                return true;
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
                return false;
            }
        }

        private void updateProgressLog(string text, string color)
        {
            Dispatcher.Invoke(new Action(delegate () {
                string new_text = DateTime.Now.ToString("[hh:mm:ss:fff] ") + text + Environment.NewLine;
                TextRange tr = new TextRange(txtProgressLog.Document.ContentEnd, txtProgressLog.Document.ContentEnd);
                tr.Text = new_text;
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, color);
                txtProgressLog.ScrollToEnd();
            }));
        }

        private void updateProgressLog_UI(string text, string color)
        {
            string new_text = DateTime.Now.ToString("[hh:mm:ss:fff] ") + text + Environment.NewLine;
            TextRange tr = new TextRange(txtProgressLog.Document.ContentEnd, txtProgressLog.Document.ContentEnd);
            tr.Text = new_text;
            tr.ApplyPropertyValue(TextElement.ForegroundProperty, color);
            txtProgressLog.ScrollToEnd();
        }

        private string get_parent_dir_path(string path)
        {
            // notice that i used two separators windows style "\\" and linux "/" (for bad formed paths)
            // We make sure to remove extra unneeded characters.
            string trim = path.TrimEnd('/', '\\');
            int index = trim.LastIndexOfAny(new char[] { '\\', '/' });

            // now if index is >= 0 that means we have at least one parent directory, otherwise the given path is the root most.
            if (index >= 0)
            {
                if (path.Remove(index).Last() == ':')
                {
                    return path.Remove(index) + "/";
                }
                else
                {
                    return path.Remove(index);
                }
            }
            else
            {
                return path;
            }
        }

        /**************************/
        //       Thread
        /**************************/
        private void UpdateUploadProgresBar(ulong uploaded)
        {
            int percent = (int)((uploaded * 100) / uploadFileLength);

            if (currentUploadItem != null)
            {
                currentUploadItem.Progress = percent;
            }
        }

        private void connectThreadFunc()
        {
            Dispatcher.Invoke(new Action(delegate () { StartStopWait(true); }));

            try
            {
                m_sftpClient.Connect();
                if (m_sftpClient.IsConnected)
                {
                    Dispatcher.Invoke(delegate () { 
                        m_sftpClient.ChangeDirectory(m_sftpClient.WorkingDirectory + "/Desktop");
                        remoteDirPath = m_sftpClient.WorkingDirectory;

                        ActivateUI();
                        loadRemoteDirList();
                    });

                    m_sshCommand.Connect();
                    if (m_sshCommand.IsConnected)
                    {
                        m_sshShell = m_sshCommand.CreateShellStream("vt100", 80, 60, 800, 600, 65536);

                        if((sshRecvthread != null) && (sshRecvthread.IsAlive))
                        {
                            Dispatcher.Invoke(new Action(delegate () { StartStopWait(false); }));
                            return;
                        }
                        sshRecvthread = new Thread(() => recvCommSSHData());
                        sshRecvthread.IsBackground = true;
                        sshRecvthread.Start();

                        Dispatcher.Invoke(new Action(delegate () { btnConnect.Content = "Close"; }));
                    }
                    else
                    {
                        updateProgressLog("ssh connect failed", "Red");
                    }
                }
                else
                {
                    updateProgressLog("sftp connect failed", "Red");
                }
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
                Dispatcher.Invoke(new Action(delegate () {
                    btnConnect.Content = "Connect";
                }));
            }
            finally
            {
                Dispatcher.Invoke(new Action(delegate () { 
                    StartStopWait(false); 
                }));
            }
        }

        private void uploadThreadFunc()
        {
            try
            {
                int loop_count = listTxRxFile.Count;
                bool retry = false;
                do
                {
                    updateProgressLog(String.Format("upload {0} files", listTxRxFile.Count), "Green");
                    loop_count = listTxRxFile.Count;
                    retry = false;
                    foreach (UserTxRxInfo item in listTxRxFile)
                    {
                        currentUploadItem = null;
                        if (item.Progress == 0 && item.valid_file)
                        {
                            currentUploadItem = item;
                            string localPath = item.LocalPath;
                            string remotePath = item.RemotePath;
                            Stream fileStream = new FileStream(localPath, FileMode.Open);

                            updateProgressLog(String.Format("  {0}", localPath), "Black");

                            uploadFileLength = (ulong)fileStream.Length;
                            m_sftpClient.UploadFile(fileStream, remotePath, UpdateUploadProgresBar);
                            fileStream.Close();

                             Dispatcher.Invoke(new Action(delegate () { progressStatus.Value++; }));
                        }
                        if (loop_count != listTxRxFile.Count)
                        {
                            retry = true;
                            break;
                        }
                    }
                    updateProgressLog("upload --> Done!!", "Green");
                } while (retry);
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
            }

            Dispatcher.Invoke(new Action(delegate () { 
                loadRemoteDirList();
                if ((sshCommandthread != null) && (sshCommandthread.IsAlive))
                {
                    return;
                }
                sshCommandthread = new Thread(() => upgradeExecuteCommand());
                sshCommandthread.IsBackground = true;
                sshCommandthread.Start();
            }));
        }

        private void recvCommSSHData()
        {
            while (true)
            {
                try
                {
                    if (m_sshCommand != null && m_sshCommand.IsConnected)
                    {
                        if (m_sshShell != null && m_sshShell.DataAvailable)
                        {
                            string strData = m_sshShell.Read();
                            string str = new Regex(@"\x1B\[[^@-~]*[@-~]").Replace(strData, "");
                            string pattern = String.Format("[sudo] password for {0}: ", currentConnInfo.UserName);
                            Dispatcher.Invoke(delegate () {
    //                                string new_text = DateTime.Now.ToString("[hh:mm:ss:fff] ") + str + Environment.NewLine;
                                if(str == "\b")
                                {
                                    string s = txtSshLog.Text;

                                    if (s.Length > 1)
                                    {
                                        s = s.Substring(0, s.Length - 1);
                                    }
                                    else
                                    {
                                        s = "";
                                    }

                                    txtSshLog.Text = s;
                                }
                                else
                                {
                                    txtSshLog.AppendText(str);
                                    txtSshLog.ScrollToEnd();
                                }
                            });

                            if (str.Contains(pattern))
                            {
                                m_sshShell.WriteLine(currentConnInfo.Password);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    updateProgressLog(ex.Message, "Red");
                }
            }
        }

        private void upgradeExecuteCommand()
        {
            try
            {
                if ((m_sshCommand != null) && (m_sshCommand.IsConnected))
                {
                    updateProgressLog("execute user command", "Green");

                    foreach (UserCommand cmd in listSshCommand)
                    {
                        m_sshShell.WriteLine(cmd.Cmd);
                        updateProgressLog(String.Format("  {0}", cmd.Cmd), "Black");
                        
                        if (cmd.IsSudo)
                        {
                            Thread.Sleep(500);
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }

                        Dispatcher.Invoke(new Action(delegate () { progressStatus.Value++; }));
                    }

                    updateProgressLog("execute --> Done!!", "Green");
                }
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
            }
        }

        /**************************/
        //       UI Event
        /**************************/
        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {

            if (((m_sftpClient != null) && m_sftpClient.IsConnected) &&
                ((m_sshCommand != null) && m_sshCommand.IsConnected))
            {
                m_sshCommand.Disconnect();
                m_sftpClient.Disconnect();

                if (connectThread != null && connectThread.IsAlive)
                {
                    connectThread.Abort();
                }
                if (sshRecvthread != null && sshRecvthread.IsAlive)
                {
                    sshRecvthread.Abort();
                }
                if (sshCommandthread != null && sshCommandthread.IsAlive)
                {
                    sshCommandthread.Abort();
                }
                if (uploadThread != null && uploadThread.IsAlive)
                {
                    uploadThread.Abort();
                }

                DeactivateUI();
            }
            else
            {
                UserConnInfo info = get_current_conn();

                try
                {
                    if ((connectThread != null) && (connectThread.IsAlive))
                    {
                        return;
                    }

                    // ssh
                    m_sshCommand = new SshClient(info.Host, 22, info.UserName, info.Password);
                    m_sshCommand.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);

                    // sftp
                    m_sftpClient = new SftpClient(info.Host, 22, info.UserName, info.Password);
                    m_sftpClient.KeepAliveInterval = TimeSpan.FromSeconds(60);
                    m_sftpClient.ConnectionInfo.Timeout = TimeSpan.FromMinutes(180);
                    m_sftpClient.OperationTimeout = TimeSpan.FromMinutes(180);

                    connectThread = new Thread(() => connectThreadFunc());
                    connectThread.IsBackground = true;
                    connectThread.Start();
                }
                catch (Exception ex)
                {
                    updateProgressLog(ex.Message, "Red");
                }
            }
        }

        private void btnLoadDatas_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Config File(*.xml)|*.xml";
            if (dialog.ShowDialog() == true)
            {
                if (LoadConfig(dialog.FileName))
                {
                    btnCheckUpgrade.IsEnabled = true;
                    btnUpgradeDatas.IsEnabled = true;
                }
            }

            if ((m_sftpClient == null) || (!m_sftpClient.IsConnected))
            {
                DeactivateUI();
                return;
            }
        }

        private void btnUpgradeDatas_Click(object sender, RoutedEventArgs e)
        {
            progressStatus.Value = 0;
            progressStatus.Maximum = listTxRxFile.Count + listSshCommand_Pre.Count + listSshCommand.Count;

            if ((m_sshCommand != null) && (m_sshCommand.IsConnected))
            {
                updateProgressLog_UI("(pre-process) execute user command", "Green");

                foreach (UserCommand cmd in listSshCommand_Pre)
                {
                    m_sshShell.WriteLine(cmd.Cmd);
                    updateProgressLog_UI(String.Format("  {0}", cmd.Cmd), "Black");

                    if (cmd.IsSudo)
                    {
                        Thread.Sleep(500);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }

                    progressStatus.Value++;
                }

                updateProgressLog_UI("pre-process --> Done!!", "Green");
            }

            if ((m_sftpClient != null) & (m_sftpClient.IsConnected))
            {
                if ((connectThread != null) && connectThread.IsAlive)
                {
                    return;
                }

                try
                {
                    progressStatus.Value = 0;
                    uploadThread = new Thread(() => uploadThreadFunc());
                    uploadThread.IsBackground = true;
                    uploadThread.Start();
                }
                catch (Exception ex)
                {
                    updateProgressLog(ex.Message, "Red");
                }
            }
            else
            {
                DeactivateUI();
                return;
            }
        }

        private void btnCheckUpgrade_Click(object sender, RoutedEventArgs e)
        {

        }

        private void listRemoteFileContextMenu_OnDoubleClick(object sender, RoutedEventArgs e)
        {
            ListViewItem item = sender as ListViewItem;
            if (item == null || !item.IsSelected)
            {
                return;
            }

            UserFileInfo file_item = item.Content as UserFileInfo;
            if (!file_item.is_directory)
            {
                return;
            }

            if (file_item.Name == "..")
            {
                remoteDirPath = get_parent_dir_path(remoteDirPath);
                loadRemoteDirList();
            }
            else
            {
                remoteDirPath += "/" + file_item.Name;
                loadRemoteDirList();
            }
        }

        private void listRemoteFileContextMenu_OnDelete(object sender, RoutedEventArgs e)
        {
            if (listRemoteFile.SelectedItems.Count == 0)
            {
                return;
            }

            foreach (var selected_item in listRemoteFile.SelectedItems)
            {
                UserFileInfo file_item = selected_item as UserFileInfo;
                if(file_item.Name == "..")
                {
                    continue;
                }
                if (file_item.is_directory)
                {
                    m_sftpClient.DeleteDirectory(remoteDirPath + "/" + file_item.Name);
                }
                else
                {
                    m_sftpClient.DeleteFile(remoteDirPath + "/" + file_item.Name);
                }
            }

            loadRemoteDirList();
        }

        private void mainWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            windowMove = true;
            lastPoint = e.GetPosition(this);
            CaptureMouse();
        }

        private void mainWindow_MouseMove(object sender, MouseEventArgs e)
        {
            if(windowMove)
            {
                Point current = e.GetPosition(this);
                this.Left += (current.X - lastPoint.X);
                this.Top += (current.Y - lastPoint.Y);
            }
        }

        private void mainWindow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            windowMove = false;
            ReleaseMouseCapture();
        }

        private void mainWindow_MouseLeave(object sender, MouseEventArgs e)
        {
            if (windowMove)
            {
                Point current = e.GetPosition(this);
                this.Left += (current.X - lastPoint.X);
                this.Top += (current.Y - lastPoint.Y);
            }
        }

        private void btnWindowClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void expenderDebug_Collapsed(object sender, RoutedEventArgs e)
        {
            ColumnDefinitionCollection columns = gridMainControls.ColumnDefinitions;
            columns[1].Width = new GridLength(1, GridUnitType.Auto);
            expenderDebug.VerticalAlignment = VerticalAlignment.Center;
        }

        private void expenderDebug_Expanded(object sender, RoutedEventArgs e)
        {
            ColumnDefinitionCollection columns = gridMainControls.ColumnDefinitions;
            columns[1].Width = new GridLength(400, GridUnitType.Star);
            expenderDebug.VerticalAlignment = VerticalAlignment.Stretch;
        }
        
        private void txtSshLog_TextInput(object sender, TextCompositionEventArgs e)
        {
            if ((m_sshCommand != null) && (m_sshCommand.IsConnected))
            {
                m_sshShell.Write(e.Text);
            }
        }

        private void txtSshLog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((m_sshCommand != null) && (m_sshCommand.IsConnected))
            {
                if (e.Key == Key.Space)
                {
                    m_sshShell.Write(" ");
                }
                else if (e.Key == Key.Back)
                {
                    m_sshShell.Write("\b");
                }
            }
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            txtSshLog.Clear();
        }

        private void MenuItem_Click_1(object sender, RoutedEventArgs e)
        {
            txtProgressLog.Document.Blocks.Clear();
        }
        
    }

    public class UserConnInfo
    {
        public UserConnInfo(string host, string user_name, string password)
        {
            Host = host;
            UserName = user_name;
            Password = password;
        }
        public string Host { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
    }

    public class UserCommand
    {
        public UserCommand(string cmd, bool sudo)
        {
            Cmd = cmd;
            IsSudo = sudo;
        }
        public string Cmd { get; set; }
        public bool IsSudo { get; set; }
    }

    public class UserFileInfo
    {
        public UserFileInfo(string name)
        {
            Image = new BitmapImage(new Uri("pack://application:,,,/Resources/folder.png"));
            Name = name;
            Size = "";
            is_directory = true;
        }
        public UserFileInfo(string name, string size, bool is_valid)
        {
            Name = name;
            Size = size;
            is_directory = false;
            valid_file = is_valid;
            if(is_valid)
            {
                Image = new BitmapImage(new Uri("pack://application:,,,/Resources/file.png"));
            }
            else
            {
                Image = new BitmapImage(new Uri("pack://application:,,,/Resources/folder.png"));
            }
        }
        public string Name { get; set; }
        public string Size { get; set; }
        public BitmapImage Image { get; }
        public bool is_directory { get; }
        public bool valid_file { get; }
    }

    public class UserTxRxInfo : INotifyPropertyChanged
    {
        //  <div>Icons made by <a href="https://www.freepik.com" title="Freepik">Freepik</a> from <a href="https://www.flaticon.com/" title="Flaticon">www.flaticon.com</a></div>
        public UserTxRxInfo(string local, string remote, int expected_size)
        {
            LocalPath = local;
            RemotePath = remote;
            _progress = 0;
            Time = DateTime.Now.ToString("yyyy.MM.dd ") + DateTime.Now.ToString("HH:mm:ss");

            FileInfo file = new FileInfo(LocalPath);
            if (file.Exists)
            {
                if (expected_size == file.Length)

                {
                    Image = new BitmapImage(new Uri("pack://application:,,,/Resources/checked.png"));
                    Name = file.Name;
                    Size = file.Length.ToString("#,##0") + "B";
                    valid_file = true;
                }
                else
                {
                    Image = new BitmapImage(new Uri("pack://application:,,,/Resources/warning.png"));
                    Name = file.Name;
                    Size = file.Length.ToString("#,##0") + "B";
                    valid_file = false;
                    Tooltip = "wrong file size";
                }
            }
            else
            {
                Image = new BitmapImage(new Uri("pack://application:,,,/Resources/cancel.png"));
                Name = file.Name;
                Size = "";
                valid_file = false;
                Tooltip = "no file exists";
            }
        }
        public string LocalPath { get; set; }
        public string RemotePath { get; set; }
        public string Time { get; set; }
        private int _progress;
        public int Progress
        {
            get
            {
                return _progress;
            }

            set
            {
                if (value != _progress)
                {
                    _progress = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string Name { get; set; }
        public string Size { get; set; }
        public BitmapImage Image { get; }
        public bool valid_file { get; }
        public string Tooltip { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        // This method is called by the Set accessor of each property.
        // The CallerMemberName attribute that is applied to the optional propertyName
        // parameter causes the property name of the caller to be substituted as an argument.
        private void NotifyPropertyChanged(String propertyName = "")
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }

    }
}
