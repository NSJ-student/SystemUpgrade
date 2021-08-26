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

//<div>Icons made by<a href="https://www.flaticon.com/authors/flat-icons" title="Flat Icons"> Flat Icons</a> from<a href="https://www.flaticon.com/" title="Flaticon">www.flaticon.com</a></div>

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
        string baseDirPath;

        // for SSH command
        SshClient m_sshCommand;
        ShellStream m_sshShell;
        List<UserCommand> listSshCommand_Pre;
        List<UserCommand> listSshCommand;
        List<UserCommand> listSshCheck;
        Thread sshRecvthread;
        Thread sshPreCommandthread;
        Thread sshCommandthread;
        Thread sshCheckThread;

        // resize
        bool windowMove;
        Point lastPoint;
        public static RoutedCommand DebugOnOff = new RoutedCommand();

        public MainWindow()
        {
            InitializeComponent();

            m_sshCommand = null;
            m_sshShell = null;
            m_sftpClient = null;

            listConnInfo = new List<UserConnInfo>();
            listConnInfo.Add(new UserConnInfo("192.168.20.192", "mik21", "roqkfdmfwkfgkwk!"));
            listConnInfo.Add(new UserConnInfo("192.168.20.192", "mik21", "nvidia"));
            listConnInfo.Add(new UserConnInfo("192.168.0.100",  "sujin", "sujin1234"));

            listTxRxFile = new List<UserTxRxInfo>();
            listLocalFile.ItemsSource = listTxRxFile;
            remoteDirPath = "";
            baseDirPath = "";
            currentUploadItem = null;

            listSshCommand_Pre = new List<UserCommand>();
            listSshCommand = new List<UserCommand>();
            listSshCheck = new List<UserCommand>();

            windowMove = false;
            expenderDebug.IsEnabled = false;
            DebugOnOff.InputGestures.Add(new KeyGesture(Key.G, ModifierKeys.Control));
            CommandBindings.Add(new CommandBinding(DebugOnOff, DebugOnOffExecuted));

            StateDisconnected_ClearAll();
        }

        /**************************/
        //     State control
        /**************************/
        private void StateConnected()
        {
            stackButtons.IsEnabled = true;
            btnLoadDatas.IsEnabled = true;
            btnCheckUpgrade.IsEnabled = false;
            btnUpgradeDatas.IsEnabled = false;

            rbGmSimDevSel.IsEnabled = false;
            rbGmSimSel.IsEnabled = false;
            rbTest.IsEnabled = false;

            btnConnect.Content = "Close";

            updateProgressLog_UI("ssh/sftp connected", "Blue");
        }

        private void StateDisconnected()
        {
            // clear SFTP
            listTxRxFile.Clear();
            foreach (UserTxRxInfo info in listTxRxFile)
            {
                info.Progress = 0;
            }
            listLocalFile.Items.Refresh();
            listRemoteFile.ItemsSource = new List<UserFileInfo>();

            // clear SSH
            txtSshLog.Clear();
            listSshCommand.Clear();
            listSshCommand_Pre.Clear();
            listSshCheck.Clear();

            // control UI
            rbGmSimDevSel.IsEnabled = true;
            rbGmSimSel.IsEnabled = true;
            rbTest.IsEnabled = true;

            stackButtons.IsEnabled = false;
            btnConnect.Content = "Connect";
            StartStopWait(false);
        }

        private void StateDisconnected_ClearAll()
        {
            if ((m_sftpClient != null) && (m_sftpClient.IsConnected))
            {
                m_sftpClient.Disconnect();
            }
            if ((m_sshCommand != null) && (m_sshCommand.IsConnected))
            {
                m_sshCommand.Disconnect();
            }

            StateDisconnected();

            // connect
            if ((connectThread != null) && (connectThread.IsAlive))
            {
                connectThread.Abort();
            }

            // stop SFTP
            if ((uploadThread != null) && (uploadThread.IsAlive))
            {
                uploadThread.Abort();
            }

            // stop SSH
            if ((sshRecvthread != null) && (sshRecvthread.IsAlive))
            {
                sshRecvthread.Abort();
            }
            if ((sshPreCommandthread != null) && (sshPreCommandthread.IsAlive))
            {
                sshPreCommandthread.Abort();
            }
            if ((sshCommandthread != null) && (sshCommandthread.IsAlive))
            {
                sshCommandthread.Abort();
            }
            if ((sshCheckThread != null) && (sshCheckThread.IsAlive))
            {
                sshCheckThread.Abort();
            }
        }

        private void StateActive()
        {
            stackButtons.IsEnabled = true;

            btnUpgradeDatas.IsEnabled = (listSshCommand_Pre.Count > 0 || listSshCommand.Count > 0 || listTxRxFile.Count > 0);
            btnCheckUpgrade.IsEnabled = (listSshCheck.Count>0);
        }

        private void StateDeActive()
        {
            stackButtons.IsEnabled = false;
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

        /**************************/
        //     User Function
        /**************************/
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
                    updateProgressLog_UI(String.Format("    base dir: {0}", local_dir), "Black");

                    var xdoc = XDocument.Load(config_path);

                    var config_elements = xdoc.Root.Element("config");
                    if(config_elements == null)
                    {
                        updateProgressLog_UI("    no info", "Red");
                        return false;
                    }

                    updateProgressLog_UI(String.Format("    config: {0}", config_elements.Value), "Black");

                    var base_dir = xdoc.Root.Element("base_dir");
                    if (base_dir != null)
                    {
                        baseDirPath = base_dir.Value;
                        if (m_sftpClient.Exists(baseDirPath))
                        {
                            m_sftpClient.ChangeDirectory(baseDirPath);
                            remoteDirPath = m_sftpClient.WorkingDirectory;
                        }
                    }

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
                            updateProgressLog_UI(String.Format("    {0}: {1}", info.Tooltip, file_name), "Red");
                            file_not_exists++;
                        }
                    }
                    listLocalFile.Items.Refresh();

                    // get command list
                    var cmd_elements = xdoc.Root.Elements("cmds");
                    listSshCommand.Clear();
                    foreach (var xList in cmd_elements)
                    {
                        string descrition = "";
                        XElement desc_element = xList.Element("cmd_desc");
                        if (desc_element != null)
                        {
                            descrition = desc_element.Value;
                        }

                        XElement cmd_element = xList.Element("cmd");
                        if (cmd_element == null)
                        {
                            continue;
                        }

                        XElement sudo_element = xList.Element("sudo");
                        if (sudo_element != null)
                        {
                            bool sudo = Convert.ToBoolean(sudo_element.Value);
                            listSshCommand.Add(new UserCommand(cmd_element.Value, descrition, sudo));
                        }
                        else
                        {
                            listSshCommand.Add(new UserCommand(cmd_element.Value, descrition));
                        }
                    }

                    // get pre-command list
                    var precmd_elements = xdoc.Root.Elements("pre_cmds");
                    listSshCommand_Pre.Clear();
                    foreach (var xList in precmd_elements)
                    {
                        string descrition = "";
                        XElement desc_element = xList.Element("cmd_desc");
                        if (desc_element != null)
                        {
                            descrition = desc_element.Value;
                        }

                        XElement cmd_element = xList.Element("cmd");
                        if (cmd_element == null)
                        {
                            continue;
                        }

                        XElement sudo_element = xList.Element("sudo");
                        if (sudo_element != null)
                        {
                            bool sudo = Convert.ToBoolean(sudo_element.Value);
                            listSshCommand_Pre.Add(new UserCommand(cmd_element.Value, descrition, sudo));
                        }
                        else
                        {
                            listSshCommand_Pre.Add(new UserCommand(cmd_element.Value, descrition));
                        }
                    }

                    // get check list
                    var check_elements = xdoc.Root.Elements("check");
                    listSshCheck.Clear();
                    foreach (var xList in check_elements)
                    {
                        XElement type_element = xList.Element("type");
                        if (type_element == null)
                        {
                            continue;
                        }

                        string descrition = "";
                        XElement desc_element = xList.Element("cmd_desc");
                        if (desc_element != null)
                        {
                            descrition = desc_element.Value;
                        }

                        int type = Convert.ToInt32(type_element.Value);
                        if(type == (int)UserCommand.CommandType.CHECK_SIZE)
                        {
                            XElement file_element = xList.Element("file");
                            if (file_element == null)
                            {
                                continue;
                            }
                            XElement size_element = xList.Element("size");
                            if (size_element == null)
                            {
                                continue;
                            }

                            string remote_path = m_sftpClient.WorkingDirectory + "/" + file_element.Value;
                            listSshCheck.Add(new UserCommand(remote_path, Convert.ToInt32(size_element.Value)));
                        }
                        else if ((type == (int)UserCommand.CommandType.CMD_PASS_EXIT_ZERO) || 
                                (type == (int)UserCommand.CommandType.CMD_PASS_EXIT_POSITIVE) ||
                                (type == (int)UserCommand.CommandType.CMD_PASS_EXIT_NEGATIVE))
                        {
                            XElement cmd_element = xList.Element("cmd");
                            if (cmd_element == null)
                            {
                                continue;
                            }

                            listSshCheck.Add(new UserCommand(cmd_element.Value, (UserCommand.CommandType)type, descrition));
                        }
                        else if (type == (int)UserCommand.CommandType.CMD_PASS_EXIT_VALUE)
                        {
                            XElement cmd_element = xList.Element("cmd");
                            if (cmd_element == null)
                            {
                                continue;
                            }

                            XElement exit_element = xList.Element("exit_status");
                            if (exit_element == null)
                            {
                                continue;
                            }

                            int exit_status = Convert.ToInt32(exit_element.Value);
                            listSshCheck.Add(new UserCommand(cmd_element.Value, UserCommand.CommandType.CMD_PASS_EXIT_VALUE, descrition, exit_status));
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

            if (rbGmSimDevSel.IsChecked == true) info = listConnInfo.ElementAt(1);
            if (rbTest.IsChecked == true) info = listConnInfo.ElementAt(2);

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
            try
            {
                Dispatcher.Invoke(new Action(delegate () { StartStopWait(true); }));

                m_sftpClient.Connect();
                if (!m_sftpClient.IsConnected)
                {
                    throw new Exception("sftp connect failed");
                }

                Dispatcher.Invoke(delegate () {
                    if(m_sftpClient.Exists(m_sftpClient.WorkingDirectory + "/Desktop"))
                    {
                        m_sftpClient.ChangeDirectory(m_sftpClient.WorkingDirectory + "/Desktop");
                    }
                    remoteDirPath = m_sftpClient.WorkingDirectory;
                    loadRemoteDirList();
                });

                m_sshCommand.Connect();
                if (!m_sshCommand.IsConnected)
                {
                    throw new Exception("ssh connect failed");
                }
                m_sshShell = m_sshCommand.CreateShellStream("vt100", 80, 60, 800, 600, 65536);

                if((sshRecvthread != null) && (sshRecvthread.IsAlive))
                {
                    updateProgressLog("connect: ssh recv thread working", "Red");
                    Dispatcher.Invoke(new Action(delegate () { StartStopWait(false); }));
                    return;
                }
                sshRecvthread = new Thread(() => recvCommSSHData());
                sshRecvthread.IsBackground = true;
                sshRecvthread.Start();

                Dispatcher.Invoke(new Action(delegate () { StateConnected(); }));
              
                Dispatcher.Invoke(new Action(delegate () { StartStopWait(false); }));
            }
            catch (Exception ex)
            {
                updateProgressLog("connect: " + ex.Message, "Red");
                Dispatcher.Invoke(new Action(delegate () {
                    StateDisconnected_ClearAll();
                }));
            }
        }

        private void recvCommSSHData()
        {
            while (true)
            {
                try
                {
                    if (m_sshCommand != null)
                    {
                        if (m_sshShell != null && m_sshShell.DataAvailable)
                        {
                            if (!m_sshCommand.IsConnected)
                            {
                                throw new Exception("ssh disconnected");
                            }

                            string strData = m_sshShell.Read();
                            string str = new Regex(@"\x1B\[[^@-~]*[@-~]").Replace(strData, "");
                            string pattern = String.Format("[sudo] password for {0}: ", currentConnInfo.UserName);
                            Dispatcher.Invoke(delegate () {
//                                string new_text = DateTime.Now.ToString("[hh:mm:ss:fff] ") + str + Environment.NewLine;
                                if(str == "\b")
                                {
                                    if (txtSshLog.Text.Length > 1)
                                    {
                                        txtSshLog.Text = txtSshLog.Text.Remove(txtSshLog.Text.Length - 1);
                                        txtSshLog.CaretIndex = txtSshLog.Text.Length;
                                        txtSshLog.ScrollToEnd();
                                    }
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
                    updateProgressLog("ssh_recv: " + ex.Message, "Red");
                    if (!m_sshCommand.IsConnected)
                    {
                        Dispatcher.Invoke(new Action(delegate () {
                            StateDisconnected_ClearAll();
                        }));
                    }
                }
            }
        }

        private void upgradeExecutePreCommand()
        {
            try
            {
                if (m_sshCommand != null)
                {
                    updateProgressLog("(pre-process) execute user command", "Green");

                    int count = 0;
                    foreach (UserCommand cmd in listSshCommand_Pre)
                    {
                        count++;
                        if (!m_sshCommand.IsConnected)
                        {
                            throw new Exception("ssh disconnected");
                        }

                        m_sshShell.WriteLine(cmd.Cmd);
                        if (cmd.CmdDescription != "")
                        {
                            updateProgressLog(String.Format("    {0}", cmd.CmdDescription), "Black");
                        }
                        else
                        {
                            updateProgressLog(String.Format("    cmd: {0}/{1}", count, listSshCommand_Pre.Count), "Black");
                        }

                        if (cmd.IsSudo)
                        {
                            Thread.Sleep(500);
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }

                        Dispatcher.Invoke(new Action(delegate () {
                            progressStatus.Value++;
                            lblProgressStatus.Text = String.Format("Upgrade {0}/{1}", progressStatus.Value, progressStatus.Maximum);
                        }));
                    }

                    updateProgressLog("pre-process --> Done!!", "Green");

                    // start upload work
                    if (!m_sftpClient.IsConnected)
                    {
                        updateProgressLog("upload: sftp not connected", "Red");
                        Dispatcher.Invoke(new Action(delegate () {
                            StateDisconnected_ClearAll();
                        }));
                        return;
                    }
                    if ((uploadThread != null) && (uploadThread.IsAlive))
                    {
                        return;
                    }
                    uploadThread = new Thread(() => uploadThreadFunc());
                    uploadThread.IsBackground = true;
                    uploadThread.Start();
                }
            }
            catch (Exception ex)
            {
                updateProgressLog("pre-process: " + ex.Message, "Red");
                imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                if (!m_sshCommand.IsConnected)
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        StateDisconnected_ClearAll();
                    }));
                }
                else
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        StateActive();
                    }));
                }
            }
        }

        private void uploadThreadFunc()
        {
            try
            {
                updateProgressLog(String.Format("upload {0} files", listTxRxFile.Count), "Green");

                foreach (UserTxRxInfo item in listTxRxFile)
                {
                    if (!m_sftpClient.IsConnected)
                    {
                        throw new Exception("sftp disconnected");
                    }

                    currentUploadItem = null;
                    if (item.valid_file)
                    {
                        currentUploadItem = item;
                        string localPath = item.LocalPath;
                        string remotePath = item.RemotePath;
                        Stream fileStream = new FileStream(localPath, FileMode.Open);

                        updateProgressLog(String.Format("    {0}", localPath), "Black");

                        uploadFileLength = (ulong)fileStream.Length;
                        m_sftpClient.UploadFile(fileStream, remotePath, UpdateUploadProgresBar);
                        fileStream.Close();

                        Dispatcher.Invoke(new Action(delegate () {
                            currentUploadItem.Progress = 100;
                            progressStatus.Value++;
                        }));
                    }
                    else
                    {
                        updateProgressLog(String.Format("upload: invalid file - {0}", item.LocalPath), "Red");
                    }

                    Dispatcher.Invoke(new Action(delegate () {
                        lblProgressStatus.Text = String.Format("Upgrade {0}/{1}", progressStatus.Value, progressStatus.Maximum);
                    }));
                }
                updateProgressLog("upload --> Done!!", "Green");

                Dispatcher.Invoke(new Action(delegate () {
                    loadRemoteDirList();
                }));

                // start command work
                if ((sshCommandthread != null) && (sshCommandthread.IsAlive))
                {
                    return;
                }
                sshCommandthread = new Thread(() => upgradeExecuteCommand());
                sshCommandthread.IsBackground = true;
                sshCommandthread.Start();
            }
            catch (Exception ex)
            {
                updateProgressLog("upload: " + ex.Message, "Red");
                if (!m_sftpClient.IsConnected)
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                        StateDisconnected_ClearAll();
                    }));
                }
                else
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                        StateActive();
                    }));
                }
                return;
            }
        }

        private void upgradeExecuteCommand()
        {
            try
            {
                if (m_sshCommand != null)
                {
                    updateProgressLog("execute user command", "Green");

                    int count = 0;
                    foreach (UserCommand cmd in listSshCommand)
                    {
                        count++;
                        if (!m_sshCommand.IsConnected)
                        {
                            throw new Exception("ssh disconnected");
                        }

                        m_sshShell.WriteLine(cmd.Cmd);
                        if (cmd.CmdDescription != "")
                        {
                            updateProgressLog(String.Format("    {0}", cmd.CmdDescription), "Black");
                        }
                        else
                        {
                            updateProgressLog(String.Format("    cmd: {0}/{1}", count, listSshCommand.Count), "Black");
                        }

                        if (cmd.IsSudo)
                        {
                            Thread.Sleep(500);
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }

                        Dispatcher.Invoke(new Action(delegate () {
                            progressStatus.Value++;
                            lblProgressStatus.Text = String.Format("Upgrade {0}/{1}", progressStatus.Value, progressStatus.Maximum);
                        }));
                    }

                    updateProgressLog("execute --> Done!!", "Green");
                    updateProgressLog("upgrade complete!!", "Blue");
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/checked.png"));
                        StateActive();
                    }));
                }
            }
            catch (Exception ex)
            {
                updateProgressLog("upgrade: " + ex.Message, "Red");
                if (!m_sshCommand.IsConnected)
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                        StateDisconnected_ClearAll();
                    }));
                }
                else
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                        StateActive();
                    }));
                }
            }
        }
        
        private void upgradeCheckCommand()
        {
            try
            {
                updateProgressLog("check upgrade", "Green");

                int failed = 0;
                foreach (UserCommand cmd in listSshCheck)
                {
                    if (cmd.Type == UserCommand.CommandType.CHECK_SIZE)
                    {
                        if (m_sftpClient != null)
                        {
                            if (!m_sftpClient.IsConnected)
                            {
                                throw new Exception("sftp disconnected");
                            }

                            if(!m_sftpClient.Exists(cmd.RemotePath))
                            {
                                updateProgressLog(String.Format("    file: no such file - {0}", System.IO.Path.GetFileName(cmd.RemotePath)), "Red");
                                failed++;
                            }
                            else
                            {
                                SftpFileAttributes file_info = m_sftpClient.GetAttributes(cmd.RemotePath);
                                if (file_info.Size == cmd.FileSize)
                                {
                                    updateProgressLog(String.Format("    file: {0}", System.IO.Path.GetFileName(cmd.RemotePath)), "Black");
                                    Dispatcher.Invoke(new Action(delegate () {
                                        progressStatus.Value++;
                                    }));
                                }
                                else
                                {
                                    updateProgressLog(String.Format("    file size check: size error - {0}", System.IO.Path.GetFileName(cmd.RemotePath)), "Red");
                                    failed++;
                                }
                            }
                        }
                        else
                        {
                            updateProgressLog("  sftp not connected", "Red");
                            failed++;
                        }
                    }
                    else if (cmd.Type != UserCommand.CommandType.CMD_EXEC)
                    {
                        if (m_sshCommand != null)
                        {
                            if (!m_sshCommand.IsConnected)
                            {
                                throw new Exception("ssh disconnected");
                            }

                            SshCommand command = m_sshCommand.CreateCommand(cmd.Cmd);
                            string result = command.Execute();

                            if(((cmd.Type == UserCommand.CommandType.CMD_PASS_EXIT_ZERO) && command.ExitStatus == 0) ||
                                ((cmd.Type == UserCommand.CommandType.CMD_PASS_EXIT_POSITIVE) && command.ExitStatus > 0) ||
                                ((cmd.Type == UserCommand.CommandType.CMD_PASS_EXIT_NEGATIVE) && command.ExitStatus < 0))
                            {
                                updateProgressLog(String.Format("    cmd: {0}", cmd.CmdDescription), "Black");
                                Dispatcher.Invoke(new Action(delegate () {
                                    progressStatus.Value++;
                                }));
                            }
                            else if ((cmd.Type == UserCommand.CommandType.CMD_PASS_EXIT_VALUE) && command.ExitStatus == cmd.ExitStatus)
                            {
                                updateProgressLog(String.Format("    cmd: {0}", cmd.CmdDescription), "Black");
                                Dispatcher.Invoke(new Action(delegate () {
                                    progressStatus.Value++;
                                }));
                            }
                            else
                            {
                                updateProgressLog(String.Format("    cmd: {0}", cmd.CmdDescription), "Red");
                                updateProgressLog(String.Format("      exitStatus: {0}", command.ExitStatus), "Red");
                                if (command.Error != "") updateProgressLog(String.Format("      error: {0}", command.Error), "Red");
                                if (result!="") updateProgressLog(String.Format("      result: {0}",  result), "Red");
                                failed++;
                            }
                        }
                        else
                        {
                            updateProgressLog("  ssh not connected", "Red");
                            failed++;
                        }
                    }

                    Dispatcher.Invoke(new Action(delegate () {
                        lblProgressStatus.Text = String.Format("Check {0}/{1}", progressStatus.Value, progressStatus.Maximum);
                    }));
                }

                updateProgressLog("check --> Done!!", "Green");
                Dispatcher.Invoke(new Action(delegate () {
                    if(failed>0)
                    {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                    }
                    else
                    {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/checked.png"));
                    }
                    StateActive();
                }));
            }
            catch (Exception ex)
            {
                updateProgressLog("check: " + ex.Message, "Red");
                if (!m_sshCommand.IsConnected || !m_sftpClient.IsConnected)
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                        StateDisconnected_ClearAll();
                    }));
                }
                else
                {
                    Dispatcher.Invoke(new Action(delegate () {
                        imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                        StateActive();
                    }));
                }
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

                StateDisconnected_ClearAll();
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
                updateProgressLog_UI(String.Format("config: {0}", dialog.SafeFileName), "Blue");
                if (LoadConfig(dialog.FileName))
                {
                    updateProgressLog_UI(String.Format("    upgrade: pre_process {0}, cmd {1}, files {2}",
                        listSshCommand_Pre.Count, listSshCommand.Count,  listTxRxFile.Count), "Black");
                    updateProgressLog_UI(String.Format("    check: {0}", listSshCheck.Count), "Black");
                    StateActive();
                }
            }

            if ((m_sftpClient == null) || (!m_sftpClient.IsConnected))
            {
                StateDisconnected_ClearAll();
                return;
            }
        }

        private void btnUpgradeDatas_Click(object sender, RoutedEventArgs e)
        {
            progressStatus.Value = 0;
            progressStatus.Maximum = listTxRxFile.Count + listSshCommand_Pre.Count + listSshCommand.Count;
            imageResult.Source = new BitmapImage();

            if ((m_sshCommand == null) || (!m_sshCommand.IsConnected))
            {
                StateDisconnected_ClearAll();
                imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                return;
            }

            try
            {
                foreach (UserTxRxInfo info in listTxRxFile)
                {
                    info.Progress = 0;
                }

                if ((sshPreCommandthread != null) && (sshPreCommandthread.IsAlive))
                {
                    return;
                }
                sshPreCommandthread = new Thread(() => upgradeExecutePreCommand());
                sshPreCommandthread.IsBackground = true;
                sshPreCommandthread.Start();
                StateDeActive();
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
                imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
            }
        }

        private void btnCheckUpgrade_Click(object sender, RoutedEventArgs e)
        {
            progressStatus.Value = 0;
            progressStatus.Maximum = listSshCheck.Count;
            imageResult.Source = new BitmapImage();

            if (listSshCheck.Count == 0)
            {
                updateProgressLog_UI("check: no command", "Green");
                return;
            }

            if (((m_sftpClient == null) || (!m_sftpClient.IsConnected)) ||
                ((m_sshCommand == null) || (!m_sshCommand.IsConnected)))
            {
                StateDisconnected_ClearAll();
                imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
                return;
            }

            try
            {
                if ((sshCheckThread != null) && (sshCheckThread.IsAlive))
                {
                    return;
                }
                sshCheckThread = new Thread(() => upgradeCheckCommand());
                sshCheckThread.IsBackground = true;
                sshCheckThread.Start();
                StateDeActive();
            }
            catch (Exception ex)
            {
                updateProgressLog(ex.Message, "Red");
                imageResult.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/remove.png"));
            }
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

            try
            {
                foreach (var selected_item in listRemoteFile.SelectedItems)
                {
                    UserFileInfo file_item = selected_item as UserFileInfo;
                    if (file_item.Name == "..")
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
            }
            catch (Exception ex)
            {
                updateProgressLog("delete: " + ex.Message, "Red");
            }

            loadRemoteDirList();
        }

        private void listRemoteFileContextMenu_OnRefresh(object sender, RoutedEventArgs e)
        {
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
            StateDisconnected_ClearAll();
            Close();
        }
        
        private void expenderDebug_Collapsed(object sender, RoutedEventArgs e)
        {
            ColumnDefinitionCollection columns = gridMainControls.ColumnDefinitions;
            columns[1].Width = new GridLength(1, GridUnitType.Auto);

            Width -= 400;
        }

        private void expenderDebug_Expanded(object sender, RoutedEventArgs e)
        {
            ColumnDefinitionCollection columns = gridMainControls.ColumnDefinitions;
            columns[1].Width = new GridLength(400, GridUnitType.Star);

            Width += 400;
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
                else if ((e.Key == Key.V) && (e.KeyboardDevice.Modifiers == ModifierKeys.Control))
                {
                    m_sshShell.Write(Clipboard.GetText());
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

        private void DebugOnOffExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            expenderDebug.IsEnabled = !expenderDebug.IsEnabled;
            if(expenderDebug.IsEnabled == false)
            {
                expenderDebug.IsExpanded = false;
                expenderDebug.Visibility = Visibility.Hidden;
                rbTest.Visibility = Visibility.Hidden;
            }
            else
            {
                expenderDebug.Visibility = Visibility.Visible;
                rbTest.Visibility = Visibility.Visible;
            }
            mainWindow.Focus();
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
        public enum CommandType
        {
            CMD_EXEC = 0,
            CHECK_SIZE = 1,
            CMD_PASS_EXIT_ZERO = 2,
            CMD_PASS_EXIT_POSITIVE = 3,
            CMD_PASS_EXIT_NEGATIVE = 4,
            CMD_PASS_EXIT_VALUE = 5,
        };
        public UserCommand(string cmd, string description, bool sudo = false)
        {
            Cmd = cmd;
            IsSudo = sudo;
            Type = CommandType.CMD_EXEC;
            CmdDescription = description;
        }
        public UserCommand(string cmd, CommandType type, string description, int exit_status = 0)
        {
            Cmd = cmd;
            Type = type;
            ExitStatus = exit_status;
            if (type == CommandType.CMD_PASS_EXIT_ZERO)
            {
                ExitStatus = 0;
            }
            CmdDescription = description;
        }
        public UserCommand(string remote_file, int file_size)
        {
            RemotePath = remote_file;
            FileSize = file_size;
            Type = CommandType.CHECK_SIZE;
            CmdDescription = "";
        }

        public string Cmd { get; }
        public bool IsSudo { get; }
        public string RemotePath { get; }
        public int FileSize { get; }
        public int ExitStatus { get; }
        public string CmdDescription { get; set; }
        public CommandType Type { get; }
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
        public string Tooltip { get; }

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
