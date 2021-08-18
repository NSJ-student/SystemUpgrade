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
        Thread uploadThread;
        Thread connectThread;
        ulong uploadFileLength;
        bool uploadActive;

        // for SSH command
        List<UserCommand> listSshCommand;
        SshClient m_sshCommand;
        ShellStream m_sshShell;
        Thread sshCommandthread;

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
            listConnInfo.Add(new UserConnInfo("192.168.0.100", "sujin", "aprtmdnpf05!"));

            listTxRxFile = new List<UserTxRxInfo>();
            uploadActive = false;

            listSshCommand = new List<UserCommand>();

            DeactivateUI();
        }

        /**************************/
        //       UI Event
        /**************************/
        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if(((m_sftpClient!=null) && m_sftpClient.IsConnected) &&
                ((m_sshCommand != null) && m_sshCommand.IsConnected))
            {
                m_sshCommand.Disconnect();
                m_sftpClient.Disconnect();
                if(connectThread.IsAlive)
                {
                    connectThread.Abort();
                }
                if(sshCommandthread.IsAlive)
                {
                    sshCommandthread.Abort();
                }
                DeactivateUI();
            }
            else
            {
                UserConnInfo info = get_current_conn();

                try
                {
                    // ssh
                    m_sshCommand = new SshClient(info.Host, 22, info.UserName, info.Password);
                    m_sshCommand.ConnectionInfo.Timeout = TimeSpan.FromSeconds(120);

                    // sftp
                    m_sftpClient = new SftpClient(info.Host, 22, info.UserName, info.Password);
                    m_sftpClient.KeepAliveInterval = TimeSpan.FromSeconds(60);
                    m_sftpClient.ConnectionInfo.Timeout = TimeSpan.FromMinutes(180);
                    m_sftpClient.OperationTimeout = TimeSpan.FromMinutes(180);

                    if ((connectThread != null) && (connectThread.IsAlive))
                    {
                        return;
                    }

                    connectThread = new Thread(() => connectThreadFunc());
                    connectThread.IsBackground = true;
                    connectThread.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
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
                    if(loadRemoteDirList())
                    {
                        btnCheckUpgrade.IsEnabled = true;
                        btnUpgradeDatas.IsEnabled = true;
                        return;
                    }
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
            if ((m_sshCommand != null) && (m_sshCommand.IsConnected))
            {
                foreach (UserCommand cmd in listSshCommand)
                {
                    if(cmd.IsSudo)
                    {
                        m_sshShell.WriteLine(cmd.Cmd);
                        string output = m_sshShell.Expect("");
                        txtSshLog.AppendText(output);
                        m_sshShell.WriteLine(currentConnInfo.Password);
                    }
                    else
                    {
                        m_sshShell.WriteLine(cmd.Cmd);
                    }
                }
            }
            else
            {
                DeactivateUI();
                return;
            }

            if ((m_sftpClient != null) & (m_sftpClient.IsConnected))
            {
                if (uploadActive)
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
                    Console.WriteLine(ex.ToString());
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

        /**************************/
        //     User Function
        /**************************/
        private void DeactivateUI()
        {
            stackButtons.IsEnabled = false;
            listTxRxFile.Clear();
            listSshCommand.Clear();

            List<UserFileInfo> user_Items = new List<UserFileInfo>();
            listLocalFile.ItemsSource = user_Items;
            listRemoteFile.ItemsSource = user_Items;

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
        public bool LoadConfig(string config_path)
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
                    lblLocalPath.Content = local_dir;

                    var xdoc = XDocument.Load(config_path);

                    // get library list
                    var xelements = xdoc.Root.Elements("libs");

                    List<UserFileInfo> user_Items = new List<UserFileInfo>();

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

                        if (File.Exists(local_path))
                        {
                            int expected_size = 0;
                            element = xList.Element("size");
                            if (element != null)
                            {
                                expected_size = Convert.ToInt32(element.Value);
                            }

                            FileInfo file = new FileInfo(local_path);
                            if (expected_size == file.Length)
                            {
                                string file_size = file.Length.ToString("#,##0") + "B";

                                listTxRxFile.Add(new UserTxRxInfo(local_path, remote_path, true));
                                user_Items.Add(new UserFileInfo(file_name, file_size, true));
                            }
                            else
                            {
                                string file_size = expected_size.ToString("#,##0") + "B";
                                user_Items.Add(new UserFileInfo(file_name, file_size, false));
                                file_not_exists++;
                            }
                        }
                        else
                        {
                            user_Items.Add(new UserFileInfo(file_name));
                            file_not_exists++;
                        }
                    }
                    user_Items = user_Items.OrderBy(a => a.Name).ToList();
                    listLocalFile.ItemsSource = user_Items;

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

                    return (file_not_exists == 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
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
        public bool loadRemoteDirList()
        {
            if ((m_sftpClient == null) || (!m_sftpClient.IsConnected))
            {
                return false;
            }

            try
            {
                List<UserFileInfo> user_Items = new List<UserFileInfo>();
                foreach (var entry in m_sftpClient.ListDirectory(m_sftpClient.WorkingDirectory))
                {
                    if (entry.IsRegularFile)
                    {
                        String FileNameOnly = entry.Name;
                        String FileSize = entry.Length.ToString("#,##0") + "B";

                        user_Items.Add(new UserFileInfo(FileNameOnly, FileSize, true));
                    }
                }
                user_Items = user_Items.OrderBy(a => a.Name).ToList();

                listRemoteFile.ItemsSource = user_Items;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return false;
            }
        }

        /**************************/
        //       Thread
        /**************************/
        private void UpdateUploadProgresBar(ulong uploaded)
        {
            int percent = (int)((uploaded * 100) / uploadFileLength);

            Dispatcher.Invoke(new Action(delegate () { progressStatus.Value = percent; }));
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
                        ActivateUI();
                    });

                    m_sshCommand.Connect();
                    if (m_sshCommand.IsConnected)
                    {
                        m_sshShell = m_sshCommand.CreateShellStream("vt100", 80, 60, 800, 600, 65536);

                        if((sshCommandthread != null) && (sshCommandthread.IsAlive))
                        {
                            Dispatcher.Invoke(new Action(delegate () { StartStopWait(false); }));
                            return;
                        }
                        sshCommandthread = new Thread(() => recvCommSSHData());
                        sshCommandthread.IsBackground = true;
                        sshCommandthread.Start();

                        Dispatcher.Invoke(new Action(delegate () { btnConnect.Content = "Close"; }));
                    }
                    else
                    {
                        Console.WriteLine("ssh connect failed");
                    }
                }
                else
                {
                    Console.WriteLine("sftp connect failed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                btnConnect.Content = "Connect";
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
            uploadActive = true;
            try
            {
                int loop_count = listTxRxFile.Count;
                bool retry = false;
                do
                {
                    loop_count = listTxRxFile.Count;
                    retry = false;
                    foreach (UserTxRxInfo item in listTxRxFile)
                    {
                        if (item.Progress == 0 && item.Dir.Equals("->"))
                        {
                            string localPath = item.LocalPath;
                            string remotePath = item.RemotePath;
                            Stream fileStream = new FileStream(localPath, FileMode.Open);

                            Dispatcher.Invoke(new Action(delegate () {
                                txtProgressLog.AppendText("uploading [" + localPath + "]\r\n");
                                txtProgressLog.ScrollToEnd();
                            }));
                            uploadFileLength = (ulong)fileStream.Length;
                            m_sftpClient.UploadFile(fileStream, remotePath, UpdateUploadProgresBar);
                            fileStream.Close();

                            Dispatcher.Invoke(new Action(delegate () {
                                txtProgressLog.AppendText("    --> Done!!\r\n");
                                txtProgressLog.ScrollToEnd();
                            }));
                        }
                        if (loop_count != listTxRxFile.Count)
                        {
                            retry = true;
                            break;
                        }
                    }
                } while (retry);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

            Dispatcher.Invoke(new Action(delegate () { loadRemoteDirList(); }));
            uploadActive = false;
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
                            String strData = m_sshShell.ReadLine();

                            Dispatcher.Invoke(delegate () {
                                string str = new Regex(@"\x1B\[[^@-~]*[@-~]").Replace(strData, "");
                                txtSshLog.AppendText(str + "\r\n");
                                txtSshLog.ScrollToEnd();
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                }

                Thread.Sleep(10);
            }
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
        public UserTxRxInfo(string local, string remote, bool local_to_remote)
        {
            LocalPath = local;
            RemotePath = remote;
            if (local_to_remote)
            {
                Dir = "->";
            }
            else
            {
                Dir = "<-";
            }
            _progress = 0;
            Time = DateTime.Now.ToString("yyyy.MM.dd ") + DateTime.Now.ToString("HH:mm:ss");
        }
        public string LocalPath { get; set; }
        public string Dir { get; set; }
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
